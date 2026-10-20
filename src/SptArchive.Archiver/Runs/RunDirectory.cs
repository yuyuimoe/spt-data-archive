using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SptArchive.Core.Entities;

namespace SptArchive.Archiver.Runs;

/// <summary>
/// The run directory of one export: the account the run gives of itself (ADR-0029). It names the mods
/// that loaded, the file each one shipped with its hash, the files as they were before the server
/// started, and whether a person changed the configuration.
/// </summary>
/// <remarks>
/// Nothing here reads a table. The tables of the export are read by the importers that already read a
/// server installation, because the export has the layout of <c>SPT_Data/database</c> (ADR-0027).
/// </remarks>
public sealed class RunDirectory
{
    /// <summary>The exporter is the tool of the run, not a mod of the archive.</summary>
    public const string ToolModGuid = "com.sptarchive.exporter";

    /// <summary>A configuration file larger than this is recorded in the manifest only (ADR-0029).</summary>
    public const int MaxConfigTextBytes = 2 * 1024 * 1024;

    private readonly IReadOnlyDictionary<string, PreBootFile> _preBoot;

    private RunDirectory(
        string exportPath,
        string runPath,
        IReadOnlyList<LoadedMod> mods,
        IReadOnlyDictionary<string, PreBootFile> preBoot,
        RunOrigin origin,
        string? serverRelease,
        RunTarget? declaredTarget)
    {
        ExportPath = exportPath;
        RunPath = runPath;
        Mods = mods;
        _preBoot = preBoot;
        Origin = origin;
        ServerRelease = serverRelease;
        DeclaredTarget = declaredTarget;
    }

    /// <summary>The directory that holds the tables of the run: the layout of <c>SPT_Data/database</c>.</summary>
    public string ExportPath { get; }

    /// <summary>The <c>run/</c> directory inside it.</summary>
    public string RunPath { get; }

    /// <summary>Every mod the server loaded, in the order it listed them, including the exporter.</summary>
    public IReadOnlyList<LoadedMod> Mods { get; }

    /// <summary>Whether a person changed the configuration of the target, and why (ADR-0029, decision 7).</summary>
    public RunOrigin Origin { get; }

    /// <summary>
    /// The server release the run was made on, as the server states it in <c>run/release.json</c>
    /// (exporter 1.3.0). Null when the export came from an older exporter, which stated no release.
    /// </summary>
    public string? ServerRelease { get; }

    /// <summary>
    /// The mod the run says it was made for, from <c>run/target.json</c> (ADR-0033, decision 3). Null for a
    /// run from before that file existed: the target then comes from configuration and nothing proves it.
    /// </summary>
    public RunTarget? DeclaredTarget { get; }

    public static RunDirectory Read(string exportPath)
    {
        var runPath = Path.Combine(exportPath, "run");
        var loadedPath = Path.Combine(runPath, "loaded.json");
        var mods = TryReadLoadedMods(exportPath)
            ?? throw new InvalidOperationException(
                $"'{exportPath}' has no run/loaded.json, so the run did not say which mods it loaded. " +
                "Only an export of exporter 1.2.0 or later records this (ADR-0029); export the run again " +
                "instead of guessing from the directory names.");

        if (mods.Count == 0)
        {
            throw new InvalidOperationException($"'{loadedPath}' lists no mod, so there is nothing to archive.");
        }

        var preBootPath = Path.Combine(runPath, "pre-boot.json");
        var preBoot = File.Exists(preBootPath)
            ? ReadArray(preBootPath, "files").EnumerateArray()
                .Select(element => new PreBootFile(
                    Required(element, "path"),
                    element.GetProperty("size").GetInt64(),
                    Required(element, "sha256")))
                .ToDictionary(file => file.Path, StringComparer.Ordinal)
            : new Dictionary<string, PreBootFile>(StringComparer.Ordinal);

        return new RunDirectory(exportPath, runPath, mods, preBoot, ReadOrigin(runPath), ReadRelease(runPath), ReadTarget(runPath));
    }

    /// <summary>
    /// The mods the export lists in <c>run/loaded.json</c>, or null when it has no such file, which is the
    /// case for an export of an exporter older than 1.2.0. An official import only warns about that case,
    /// so it cannot use <see cref="Read"/>, which refuses it (ADR-0033, decision 1).
    /// </summary>
    public static IReadOnlyList<LoadedMod>? TryReadLoadedMods(string exportPath)
    {
        var loadedPath = Path.Combine(exportPath, "run", "loaded.json");

        return File.Exists(loadedPath)
            ? ReadArray(loadedPath, "mods").EnumerateArray().Select(ToLoadedMod).ToArray()
            : null;
    }

    /// <summary>The release the export states in <c>run/release.json</c>, or null when it states none (exporter 1.3.0).</summary>
    public static string? TryReadServerRelease(string exportPath) =>
        ReadRelease(Path.Combine(exportPath, "run"));

    private static LoadedMod ToLoadedMod(JsonElement element) => new(
        Directory: Required(element, "directory"),
        ModGuid: Required(element, "modGuid"),
        Name: Optional(element, "name") ?? Required(element, "directory"),
        Author: Optional(element, "author") ?? string.Empty,
        Version: Required(element, "version"),
        DeclaredSptVersion: Optional(element, "sptVersion") ?? string.Empty,
        HasPrepatcher: element.TryGetProperty("hasPrepatcher", out var prepatcher)
            && prepatcher.ValueKind == JsonValueKind.True,
        Dependencies: ReadDependencies(element));

    /// <summary>The mod this run is about, by its GUID. The exporter and the dependencies are not it.</summary>
    public LoadedMod Target(string modGuid)
    {
        return Mods.FirstOrDefault(mod => string.Equals(mod.ModGuid, modGuid, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                $"The run loaded no mod with the GUID '{modGuid}'. It loaded: " +
                string.Join(", ", Mods.Select(mod => $"{mod.ModGuid} {mod.Version}")) +
                ". A dependency is never the target: run the target again with its dependencies beside it (ADR-0021).");
    }

    /// <summary>The manifest of one mod: every file it ships, with its hash as the run stopped.</summary>
    public IReadOnlyList<ManifestEntry> ManifestOf(LoadedMod mod)
    {
        var manifestPath = Path.Combine(RunPath, "files", mod.Directory + ".json");

        if (!File.Exists(manifestPath))
        {
            throw new InvalidOperationException(
                $"'{manifestPath}' is missing, so the run did not record the files of '{mod.Directory}'. " +
                "The exporter writes one manifest per loaded mod (ADR-0029).");
        }

        return ReadArray(manifestPath, "files").EnumerateArray()
            .Select(element => new ManifestEntry(
                Required(element, "path"),
                element.GetProperty("size").GetInt64(),
                Required(element, "sha256"),
                Optional(element, "kind") ?? ModFileKind.Other))
            .ToArray();
    }

    /// <summary>
    /// The rows of <c>mod_files</c> for one mod. The path of a manifest entry is relative to the mod
    /// directory, and the stored path starts with it (ADR-0029). The configuration text is read from
    /// <c>run/config</c>, where the exporter copied it before it stopped the server.
    /// </summary>
    public IReadOnlyList<ModFile> FilesFor(LoadedMod mod, Guid datasetId)
    {
        var files = new List<ModFile>();

        foreach (var entry in ManifestOf(mod))
        {
            var storedPath = $"{mod.Directory}/{entry.Path}";
            var isConfig = string.Equals(entry.Kind, ModFileKind.Config, StringComparison.Ordinal);
            var text = isConfig ? ReadConfigText(mod, entry) : null;

            files.Add(new ModFile
            {
                DatasetId = datasetId,
                Path = storedPath,
                Kind = entry.Kind,
                Size = entry.Size,
                Sha256 = entry.Sha256,
                ShippedSha256 = _preBoot.TryGetValue(storedPath, out var before) ? before.Sha256 : null,
                ContentText = text,
                Content = ParseJsonOrNull(text),
            });
        }

        return files;
    }

    /// <summary>
    /// The dependency set of a run: every mod that loaded with the target, by GUID and version, sorted.
    /// The exporter is left out, because it is the tool and not a dependency of anything (ADR-0013).
    /// </summary>
    public static string DependencySetJson(IReadOnlyList<LoadedMod> mods)
    {
        var set = mods
            .Where(mod => !mod.IsTool)
            .Select(mod => new Dependency(mod.ModGuid, mod.Version, mod.DeclaredSptVersion))
            .OrderBy(mod => mod.ModGuid, StringComparer.Ordinal)
            .ToArray();

        return JsonSerializer.Serialize(set);
    }

    /// <summary>SHA-256 of the dependency set, which is half of the baseline cache key (ADR-0013).</summary>
    public static string HashDependencySet(string dependencySetJson) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(dependencySetJson))).ToLowerInvariant();

    private string? ReadConfigText(LoadedMod mod, ManifestEntry entry)
    {
        if (entry.Size > MaxConfigTextBytes)
        {
            return null;
        }

        var path = Path.Combine(RunPath, "config", mod.Directory, entry.Path.Replace('/', Path.DirectorySeparatorChar));

        // The exporter writes the text it found when the run stopped. A missing copy is not an error:
        // the manifest still names the file and its hash.
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    private static string? ParseJsonOrNull(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            return text;
        }
        catch (JsonException)
        {
            // A .jsonc file has comments and is not JSON. Its text is kept and its jsonb column stays null.
            return null;
        }
    }

    /// <summary>
    /// The release out of <c>run/release.json</c>. A run of an older exporter has no such file: the
    /// reader then knows no release and says so, instead of assuming the stated one (ADR-0014).
    /// </summary>
    private static string? ReadRelease(string runPath)
    {
        var path = Path.Combine(runPath, "release.json");

        if (!File.Exists(path))
        {
            return null;
        }

        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        var release = Optional(document.RootElement, "sptVersion");

        return string.IsNullOrWhiteSpace(release) ? null : release;
    }

    private static RunTarget? ReadTarget(string runPath)
    {
        var path = Path.Combine(runPath, "target.json");

        if (!File.Exists(path))
        {
            return null;
        }

        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        var root = document.RootElement;

        return new RunTarget(
            Required(root, "modGuid"),
            Required(root, "version"),
            Optional(root, "reason"));
    }

    private static RunOrigin ReadOrigin(string runPath)
    {
        var path = Path.Combine(runPath, "origin.json");

        if (!File.Exists(path))
        {
            return new RunOrigin(ModConfigOrigin.Shipped, null);
        }

        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        var origin = Optional(document.RootElement, "configOrigin") ?? ModConfigOrigin.Shipped;

        if (!ModConfigOrigin.All.Contains(origin, StringComparer.Ordinal))
        {
            throw new InvalidOperationException($"'{path}' states the configuration origin '{origin}', which is not one of: {string.Join(", ", ModConfigOrigin.All)}.");
        }

        return new RunOrigin(origin, Optional(document.RootElement, "reason"));
    }

    private static IReadOnlyDictionary<string, string> ReadDependencies(JsonElement element)
    {
        if (!element.TryGetProperty("modDependencies", out var dependencies)
            || dependencies.ValueKind != JsonValueKind.Object)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        return dependencies.EnumerateObject().ToDictionary(
            dependency => dependency.Name,
            dependency => dependency.Value.ValueKind == JsonValueKind.String
                ? dependency.Value.GetString()!
                : dependency.Value.GetRawText(),
            StringComparer.Ordinal);
    }

    private static JsonElement ReadArray(string path, string what)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));

        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException($"'{path}' is not an array of {what}; the run directory has an unexpected shape.");
        }

        // The element must outlive the document that parsed it.
        return document.RootElement.Clone();
    }

    private static string Required(JsonElement element, string name)
    {
        return Optional(element, name)
            ?? throw new InvalidOperationException($"A run directory entry has no '{name}'; the run did not record it (ADR-0029).");
    }

    private static string? Optional(JsonElement element, string name)
    {
        return element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private sealed record Dependency(string ModGuid, string Version, string DeclaredSptVersion);
}

/// <summary>One mod the server loaded, as <c>run/loaded.json</c> states it.</summary>
public sealed record LoadedMod(
    string Directory,
    string ModGuid,
    string Name,
    string Author,
    string Version,
    string DeclaredSptVersion,
    bool HasPrepatcher,
    IReadOnlyDictionary<string, string> Dependencies)
{
    public bool IsTool => string.Equals(ModGuid, RunDirectory.ToolModGuid, StringComparison.OrdinalIgnoreCase);
}

/// <summary>One entry of a mod manifest: a file it ships, with the hash the run stopped with.</summary>
public sealed record ManifestEntry(string Path, long Size, string Sha256, string Kind);

/// <summary>One file as it was before the server started, from <c>run/pre-boot.json</c> (ADR-0029).</summary>
public sealed record PreBootFile(string Path, long Size, string Sha256);

/// <summary>Whether a person changed the configuration of the run, and the reason they gave.</summary>
public sealed record RunOrigin(string ConfigOrigin, string? Reason);

/// <summary>The mod that a run states it was made for, and why (<c>run/target.json</c>, ADR-0033).</summary>
public sealed record RunTarget(string ModGuid, string Version, string? Reason);
