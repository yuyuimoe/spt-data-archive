using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SptArchive.Core.Data;

namespace SptArchive.Archiver.Importers;

/// <summary>One importer reads one part of the server database and writes it to its tables.</summary>
public interface IImporter
{
    string Name { get; }

    Task ImportAsync(ImportContext context, CancellationToken cancellationToken);
}

/// <summary>
/// Everything an importer needs. Rows are queued with <see cref="QueueAsync{T}"/> and written in
/// batches; the change tracker is cleared after every batch so memory stays flat however large the file is.
/// </summary>
public sealed class ImportContext(ArchiveDbContext db, string databasePath, string sptVersion, int batchSize)
{
    private int _pending;

    public ArchiveDbContext Db { get; } = db;

    /// <summary>The server's <c>SPT_Data/database</c> directory.</summary>
    public string DatabasePath { get; } = databasePath;

    /// <summary>
    /// The server release that every imported official row states (ADR-0014). The importer replaces
    /// only the rows of this release, so the rows of other releases stay exactly as they were.
    /// </summary>
    public string SptVersion { get; } = sptVersion;

    public async Task QueueAsync<T>(T entity, CancellationToken cancellationToken) where T : class
    {
        Db.Add(entity);

        if (++_pending >= batchSize)
        {
            await FlushAsync(cancellationToken);
        }
    }

    public async Task FlushAsync(CancellationToken cancellationToken)
    {
        if (_pending == 0)
        {
            return;
        }

        await Db.SaveChangesAsync(cancellationToken);
        Db.ChangeTracker.Clear();
        _pending = 0;
    }
}

internal static class SourceFiles
{
    private static readonly JsonDocumentOptions LenientJson = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>Reads and parses a JSON file. The caller disposes the returned document.</summary>
    public static async Task<JsonDocument> ReadJsonAsync(string path, CancellationToken cancellationToken)
    {
        EnsureNotLfsPointer(path);

        await using var stream = File.OpenRead(path);
        return await JsonDocument.ParseAsync(stream, LenientJson, cancellationToken);
    }

    /// <summary>
    /// Some large files in the server repository are stored with Git LFS. Without "git lfs pull" the
    /// checkout contains a ~130 byte text pointer instead of the data, which would otherwise fail with a
    /// confusing JSON error (or, worse, be archived as if it were real data).
    /// </summary>
    public static void EnsureNotLfsPointer(string path)
    {
        var info = new FileInfo(path);

        if (info.Length is 0 or >= 512)
        {
            return;
        }

        using var reader = new StreamReader(path);
        var firstLine = reader.ReadLine();

        if (firstLine?.StartsWith("version https://git-lfs.github.com", StringComparison.Ordinal) == true)
        {
            throw new InvalidOperationException(
                $"'{path}' is a Git LFS pointer, not the real file. Run 'git lfs pull' in the server repository first.");
        }
    }
}

internal static class JsonElementExtensions
{
    /// <summary>Reads a string, number or boolean property as text. Returns null when missing or any other kind.</summary>
    public static string? GetScalarString(this JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.GetRawText(),
            _ => null,
        };
    }

    public static double? GetDoubleOrNull(this JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetDouble(out var result))
        {
            return result;
        }

        return null;
    }

    /// <summary>Reads a whole number. Accepts values written as 2.0 and rejects real fractions.</summary>
    public static int? GetInt32OrNull(this JsonElement element, string name)
    {
        var number = element.GetDoubleOrNull(name);

        return number is { } n && n == Math.Floor(n) && Math.Abs(n) <= int.MaxValue ? (int)n : null;
    }

    public static bool GetBool(this JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.True;

    /// <summary>The raw JSON of a property, or null when it is missing or JSON null.</summary>
    public static string? GetRawOrNull(this JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out var value)
            && value.ValueKind != JsonValueKind.Null)
        {
            return value.GetRawText();
        }

        return null;
    }
}

/// <summary>Fingerprints the source files so an unchanged server database is not re-imported.</summary>
internal static class SourceHasher
{
    public static async Task<string> ComputeAsync(string root, CancellationToken cancellationToken)
    {
        using var total = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        var files = Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories)
            .OrderBy(file => file, StringComparer.Ordinal);

        foreach (var file in files)
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            total.AppendData(Encoding.UTF8.GetBytes(relative));

            await using var stream = File.OpenRead(file);
            total.AppendData(await SHA256.HashDataAsync(stream, cancellationToken));
        }

        return Convert.ToHexString(total.GetHashAndReset()).ToLowerInvariant();
    }
}
