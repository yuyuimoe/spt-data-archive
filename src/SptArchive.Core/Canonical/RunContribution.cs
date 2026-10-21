using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SptArchive.Core.Canonical;

/// <summary>
/// What one run contributes to the archive: the rows a mod adds, the official rows it changes, and
/// nothing else. It is the result of comparing the export of a mod run against the baseline of its
/// release, in the canonical form of ADR-0028.
/// </summary>
/// <remarks>
/// The contribution is what the dataset stores and what its content hash covers (ADR-0029), so two
/// runs of the same mod version on different releases produce the same contribution and share one
/// dataset (ADR-0013). It says no release: the release belongs to the observation.
/// </remarks>
public sealed record RunContribution(
    IReadOnlyList<TemplateContribution> Templates,
    IReadOnlyList<LocaleContribution> Locales,
    IReadOnlyDictionary<string, string> UnstoredChanges,
    IReadOnlyDictionary<string, string> TableHashes,
    IReadOnlyDictionary<string, int> RowCounts,
    IReadOnlyList<Addressing.AddressEntry> IdMap,
    IReadOnlyList<string> Warnings)
{
    /// <summary>No template and no locale differs from the baseline.</summary>
    public bool IsEmpty => Templates.Count == 0 && Locales.Count == 0 && UnstoredChanges.Count == 0;

    /// <summary>True when the run changed a table that no row type of this archive holds.</summary>
    public bool HasUnstoredChanges => FilesWithoutRows.Any();

    /// <summary>
    /// The objects of the server database that have no table of their own: locations, bot types, hideout
    /// parts, prices and handbook entries (ADR-0041). They are rows in addition to the canonical hash of
    /// their files, which stays in <see cref="UnstoredChanges"/>, so they are not an input of
    /// <see cref="DatasetHash"/> and a dataset keeps the hash it had.
    /// </summary>
    public IReadOnlyList<ObjectContribution> Objects { get; init; } = [];

    /// <summary>
    /// The changed files that still have no row: the hash of each is all the archive keeps of the change
    /// (TD-45, TD-60). A file of the five tables of ADR-0041 is not among them.
    /// </summary>
    public IEnumerable<string> FilesWithoutRows =>
        UnstoredChanges.Keys.Where(path => !DatabaseObjects.HoldsRows(path)).OrderBy(path => path, StringComparer.Ordinal);
}

/// <summary>
/// One template under a dataset: an item, a quest, a customization or a trader, with the operation
/// and, for an addition, the object as the run left it (ADR-0017).
/// </summary>
/// <param name="TemplateId">The address of the object (ADR-0034) and the key of every row about it.</param>
/// <param name="RawId">The id the run gave the object, which the archive keeps as a lookup only.</param>
/// <param name="NewJson">The object as the run wrote it, for storage (ADR-0002).</param>
/// <param name="CanonicalJson">The object in canonical form, which is what the content hash covers (ADR-0029).</param>
public sealed record TemplateContribution(
    string Kind,
    string TemplateId,
    string RawId,
    string Op,
    string? ParentId,
    string? NewJson,
    string? CanonicalJson,
    IReadOnlyList<PathContribution> Changes);

/// <summary>
/// One path under a modified template (ADR-0017). The old value comes from the baseline, which holds
/// the official row of the release in the same shape, so no official table has to be read.
/// </summary>
public sealed record PathContribution(string Path, string Op, string? OldValue, string? NewValue);

/// <summary>
/// One locale entry that the run holds and the baseline does not, or holds with another value.
/// </summary>
public sealed record LocaleContribution(string Scope, string LanguageCode, string Key, string Value, string? OldValue);

/// <summary>
/// One file that changed and that no template or locale row holds: a location, a price table, a global
/// setting. Its canonical hash keeps two datasets apart when the only difference between them is such a
/// file. A file of the five tables of ADR-0041 also has rows, and stays here so the hash does not move.
/// </summary>
public sealed record UnstoredChange(string Path, string CanonicalHash);

/// <summary>One file of the dataset, as the run recorded it.</summary>
public sealed record ContributionFile(string Path, string Sha256, string? ConfigSha256);

/// <summary>
/// The content hash of a dataset (ADR-0029): the contribution rows, the configuration text and the
/// manifest of the files the mod ships. It states no release and no dependency, so a run of the same
/// content on another release, or with another dependency set, is the same dataset.
/// </summary>
public static class DatasetHash
{
    public static string Compute(RunContribution contribution, IEnumerable<ContributionFile> files)
    {
        var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false, SkipValidation = true }))
        {
            writer.WriteStartObject();

            writer.WriteStartArray("templates");
            foreach (var template in contribution.Templates
                         .OrderBy(row => row.Kind, StringComparer.Ordinal)
                         .ThenBy(row => row.TemplateId, StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteString("kind", template.Kind);
                writer.WriteString("id", template.TemplateId);
                writer.WriteString("op", template.Op);

                if (template.ParentId is null)
                {
                    writer.WriteNull("parent");
                }
                else
                {
                    writer.WriteString("parent", template.ParentId);
                }

                writer.WriteString("row", template.CanonicalJson ?? template.NewJson ?? string.Empty);

                writer.WriteStartArray("paths");
                foreach (var change in template.Changes)
                {
                    writer.WriteStartObject();
                    writer.WriteString("path", change.Path);
                    writer.WriteString("op", change.Op);
                    writer.WriteString("old", change.OldValue ?? string.Empty);
                    writer.WriteString("new", change.NewValue ?? string.Empty);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();

            writer.WriteStartArray("locales");
            foreach (var locale in contribution.Locales
                         .OrderBy(row => row.Scope, StringComparer.Ordinal)
                         .ThenBy(row => row.LanguageCode, StringComparer.Ordinal)
                         .ThenBy(row => row.Key, StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteString("scope", locale.Scope);
                writer.WriteString("language", locale.LanguageCode);
                writer.WriteString("key", locale.Key);
                writer.WriteString("value", locale.Value);
                writer.WriteString("old", locale.OldValue ?? string.Empty);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();

            writer.WriteStartArray("unstored");
            foreach (var change in contribution.UnstoredChanges.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteString("path", change.Key);
                writer.WriteString("hash", change.Value);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();

            writer.WriteStartArray("files");
            foreach (var file in files.OrderBy(file => file.Path, StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteString("path", file.Path);
                writer.WriteString("sha256", file.Sha256);
                if (file.ConfigSha256 is not null)
                {
                    writer.WriteString("config", file.ConfigSha256);
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();

            writer.WriteEndObject();
        }

        return Convert.ToHexString(SHA256.HashData(buffer.ToArray())).ToLowerInvariant();
    }

    /// <summary>SHA-256 of text, in the form the archive stores a file hash.</summary>
    public static string HashText(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
}
