using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SptArchive.Core.Entities;

namespace SptArchive.Archiver.Importers;

/// <summary>
/// Imports <c>locales/languages.json</c> and every <c>locales/global/{code}.json</c>.
/// </summary>
/// <remarks>
/// The "menu", "server" and "web" locale folders are not imported yet. "menu" files are nested objects
/// rather than flat key/value maps, so they need their own mapping.
/// </remarks>
public sealed class LocaleImporter(ILogger<LocaleImporter> logger) : IImporter
{
    private const string Scope = "global";

    public string Name => "locales";

    public async Task ImportAsync(ImportContext context, CancellationToken cancellationToken)
    {
        var localesDirectory = Path.Combine(context.DatabasePath, "locales");

        // Languages are upserted, never deleted: mod locale rows reference them too.
        var languages = await ReadLanguagesAsync(Path.Combine(localesDirectory, "languages.json"), cancellationToken);
        var existing = await context.Db.Languages.ToDictionaryAsync(language => language.Code, cancellationToken);

        foreach (var (code, name) in languages)
        {
            if (existing.TryGetValue(code, out var language))
            {
                language.Name = name;
            }
            else
            {
                context.Db.Languages.Add(new Language { Code = code, Name = name });
            }
        }

        await context.Db.SaveChangesAsync(cancellationToken);
        context.Db.ChangeTracker.Clear();

        // The entries of this release are replaced inside the run's transaction; readers keep seeing the
        // old rows until it commits, and other releases are not touched (ADR-0014).
        await context.Db.LocaleEntries
            .Where(entry => entry.SptVersion == context.SptVersion)
            .ExecuteDeleteAsync(cancellationToken);

        foreach (var code in languages.Keys)
        {
            var path = Path.Combine(localesDirectory, Scope, $"{code}.json");

            if (!File.Exists(path))
            {
                logger.LogWarning("Language {Code} is listed but {Path} does not exist; skipping", code, path);
                continue;
            }

            var count = await ImportLanguageAsync(context, code, path, cancellationToken);
            logger.LogInformation("Locale {Code}: {Count} entries", code, count);
        }

        await context.FlushAsync(cancellationToken);
    }

    private static async Task<Dictionary<string, string>> ReadLanguagesAsync(string path, CancellationToken cancellationToken)
    {
        using var document = await SourceFiles.ReadJsonAsync(path, cancellationToken);

        return document.RootElement.EnumerateObject()
            .ToDictionary(property => property.Name, property => property.Value.GetString() ?? property.Name);
    }

    private static async Task<int> ImportLanguageAsync(
        ImportContext context,
        string languageCode,
        string path,
        CancellationToken cancellationToken)
    {
        using var document = await SourceFiles.ReadJsonAsync(path, cancellationToken);

        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var property in document.RootElement.EnumerateObject())
        {
            // A duplicated key in the source would violate the primary key; keep the first one.
            if (!seen.Add(property.Name))
            {
                continue;
            }

            var (entityId, field) = SplitKey(property.Name);

            await context.QueueAsync(
                new LocaleEntry
                {
                    SptVersion = context.SptVersion,
                    Scope = Scope,
                    LanguageCode = languageCode,
                    Key = property.Name,
                    Value = property.Value.ValueKind == JsonValueKind.String
                        ? property.Value.GetString() ?? string.Empty
                        : property.Value.GetRawText(),
                    EntityId = entityId,
                    Field = field,
                },
                cancellationToken);
        }

        return seen.Count;
    }

    /// <summary>
    /// Entity keys look like "&lt;24 hex chars&gt; &lt;Field&gt;", for example "5422acb9af1c889c16000029 Name".
    /// Everything else (UI strings such as "Voip/DisabledForOffline") has no entity.
    /// </summary>
    internal static (string? EntityId, string? Field) SplitKey(string key)
    {
        if (key.Length > 25 && key[24] == ' ' && IsHex(key.AsSpan(0, 24)))
        {
            return (key[..24], key[25..]);
        }

        return (null, null);
    }

    private static bool IsHex(ReadOnlySpan<char> text)
    {
        foreach (var character in text)
        {
            if (!char.IsAsciiHexDigit(character))
            {
                return false;
            }
        }

        return true;
    }
}
