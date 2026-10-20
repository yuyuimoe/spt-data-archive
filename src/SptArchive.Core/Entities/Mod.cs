namespace SptArchive.Core.Entities;

/// <summary>
/// One registered version of a server mod. Everything a mod adds lives in the <c>mod_*</c> tables
/// and points at the dataset of its content, so official data and mod data can never be mixed up.
/// </summary>
public class Mod
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>Unique identifier declared in the mod metadata (for example "com.author.modname").</summary>
    public required string ModGuid { get; set; }

    public required string Name { get; set; }

    public required string Version { get; set; }

    public string? Author { get; set; }

    /// <summary>Semver range of server versions the mod declares support for.</summary>
    public string? SptVersionRange { get; set; }

    public string? SourceUrl { get; set; }

    public string? License { get; set; }

    /// <summary>The complete raw mod metadata as JSON (jsonb), kept so nothing is lost.</summary>
    public string? Metadata { get; set; }

    public DateTimeOffset ArchivedAt { get; set; } = DateTimeOffset.UtcNow;
}
