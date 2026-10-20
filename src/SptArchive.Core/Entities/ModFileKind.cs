// This file is compiled into SptArchive.Core and linked into SptArchive.Exporter, so the rule that
// says what a configuration file is lives in one place (ADR-0029).
// It must not depend on anything but the base class library.

namespace SptArchive.Core.Entities;

/// <summary>What a file is to the mod that ships it (ADR-0029).</summary>
public static class ModFileKind
{
    /// <summary>A <c>.dll</c>: server code.</summary>
    public const string Assembly = "assembly";

    /// <summary>A Unity asset bundle or the <c>bundles.json</c> that lists them (ADR-0023).</summary>
    public const string Bundle = "bundle";

    /// <summary>A <c>db/</c> fragment of table data.</summary>
    public const string Data = "data";

    /// <summary>A configuration file: read by the mod, never authored by the archive (ADR-0029).</summary>
    public const string Config = "config";

    /// <summary>A document for a person (readme, licence, notices).</summary>
    public const string Text = "text";

    /// <summary>Anything else.</summary>
    public const string Other = "other";

    public static readonly string[] All = [Assembly, Bundle, Data, Config, Text, Other];

    /// <summary>
    /// Decision 2 of ADR-0029: a path with a segment <c>config</c>, <c>configs</c>, <c>presets</c> or
    /// <c>loader</c>; a name that contains <c>config</c>, <c>settings</c>, <c>preset</c>,
    /// <c>option</c> or <c>loader</c>; or the extension <c>.jsonc</c>. A file under a web root is never
    /// a configuration file: the web root is served to a browser, so its files are assets even when a
    /// name says "loader" (ECOTI ships three.js's <c>GLTFLoader.js</c> under <c>wwwroot/vendor</c>).
    /// The rule lives here and not in the exporter, so the exporter and the reader agree on what a
    /// configuration file is.
    /// </summary>
    public static bool IsConfigFile(string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/');

        if (normalized.EndsWith(".jsonc", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            return false;
        }

        // Checked first: a served asset is not a file a person edits, whatever its name contains.
        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (segments[i].Equals("wwwroot", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        // Every segment but the last is a directory name. A file at the root of the mod has none,
        // and the name rule below still applies to it ("config.json" is a configuration file).
        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (segments[i].Equals("config", StringComparison.OrdinalIgnoreCase)
                || segments[i].Equals("configs", StringComparison.OrdinalIgnoreCase)
                || segments[i].Equals("presets", StringComparison.OrdinalIgnoreCase)
                || segments[i].Equals("loader", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        var name = segments[^1];
        foreach (var word in (string[])["config", "settings", "preset", "option", "loader"])
        {
            if (name.Contains(word, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The kind of a file, from its path and extension.</summary>
    public static string Classify(string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/');
        var name = normalized[(normalized.LastIndexOf('/') + 1)..];

        if (IsConfigFile(normalized))
        {
            return Config;
        }

        if (name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            return Assembly;
        }

        if (name.EndsWith(".bundle", StringComparison.OrdinalIgnoreCase) || name.Equals("bundles.json", StringComparison.OrdinalIgnoreCase))
        {
            return Bundle;
        }

        if (normalized.StartsWith("db/", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("/db/", StringComparison.OrdinalIgnoreCase))
        {
            return Data;
        }

        var extension = Path.GetExtension(name).ToLowerInvariant();
        return extension switch
        {
            ".md" or ".txt" or ".licence" or ".license" or ".notice" => Text,
            _ => Other,
        };
    }
}
