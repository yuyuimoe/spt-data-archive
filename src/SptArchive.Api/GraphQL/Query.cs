using HotChocolate;
using HotChocolate.Data;
using HotChocolate.Types;
using Microsoft.EntityFrameworkCore;
using SptArchive.Core.Data;
using SptArchive.Core.Entities;

namespace SptArchive.Api.GraphQL;

/// <summary>
/// The whole public surface of the API. There is no Mutation type: the archive is written only by the Archiver.
/// </summary>
/// <remarks>
/// Official data and mod data are separate collections (<c>items</c> / <c>modItems</c>, ...). Every list supports
/// paging, filtering and sorting, and only the columns a query selects are read from the database.
/// Fields holding raw JSON (<c>props</c>, <c>data</c>, <c>base</c>, <c>tableHashes</c>, <c>result</c>, ...) are
/// returned as <c>JSON</c> values (TD-15).
/// Every official field takes an optional <c>sptVersion</c>: one exact archived release, or the latest when it
/// is left out (ADR-0014, ADR-0019). Each official row states its release in its <c>sptVersion</c> field.
/// A mod field takes no release: a dataset carries none, and an observation states the release it ran on
/// (ADR-0013).
/// </remarks>
public sealed class Query
{
    // ---- Releases (ADR-0014, ADR-0019) ------------------------------------------------------------

    /// <summary>
    /// The server releases that the archive holds, newest first. A client reads this list to learn
    /// which values the <c>sptVersion</c> argument of the official queries accepts.
    /// </summary>
    public Task<List<SptVersion>> GetSptVersionsAsync(ArchiveDbContext db, CancellationToken cancellationToken) =>
        Releases.ListAsync(db, cancellationToken);

    // ---- Mods and languages -----------------------------------------------------------------------

    [UsePaging]
    [UseProjection]
    [UseFiltering]
    [UseSorting]
    public IQueryable<Mod> GetMods(ArchiveDbContext db) => db.Mods.OrderBy(x => x.Name).ThenBy(x => x.Version);

    [UseProjection]
    [UseFiltering]
    [UseSorting]
    public IQueryable<Language> GetLanguages(ArchiveDbContext db) => db.Languages.OrderBy(x => x.Code);

    // ---- Mod datasets, their content and their provenance (ADR-0013, ADR-0029, ADR-0032, ADR-0033) -------

    /// <summary>
    /// The datasets that the archive holds, newest first. A dataset is the content of one mod version and
    /// carries no server release: an observation states the releases it ran on, so these fields take no
    /// <c>sptVersion</c> argument.
    /// </summary>
    [UsePaging]
    [UseProjection]
    [UseFiltering]
    [UseSorting]
    public IQueryable<ModDataset> GetModDatasets(ArchiveDbContext db) =>
        db.ModDatasets.OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id);

    [GraphQLName("modDataset")]
    public async Task<ModDataset?> GetModDatasetAsync(Guid id, ArchiveDbContext db, CancellationToken cancellationToken) =>
        await db.ModDatasets.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

    /// <summary>
    /// One row for each object that a dataset touched, with the operation that the mod applies to it
    /// (ADR-0017). An offer is a template of its own kind, named by its address (ADR-0032).
    /// </summary>
    [UsePaging]
    [UseProjection]
    [UseFiltering]
    [UseSorting]
    public IQueryable<ModTemplate> GetModTemplates(ArchiveDbContext db) =>
        db.ModTemplates.OrderBy(x => x.DatasetId).ThenBy(x => x.Kind).ThenBy(x => x.TemplateId);

    /// <summary>
    /// One row for each changed value under a template, with the value before the mod and the value after
    /// it (ADR-0032). The path is an RFC 6901 JSON pointer.
    /// </summary>
    [UsePaging]
    [UseProjection]
    [UseFiltering]
    [UseSorting]
    public IQueryable<ModChangePath> GetModChangePaths(ArchiveDbContext db) =>
        db.ModChangePaths.OrderBy(x => x.DatasetId).ThenBy(x => x.Kind).ThenBy(x => x.TemplateId).ThenBy(x => x.Path);

    /// <summary>
    /// The files that a dataset loaded, with the manifest entry of each and the text of a configuration
    /// file (ADR-0029). The path starts with the mod directory.
    /// </summary>
    [UsePaging]
    [UseProjection]
    [UseFiltering]
    [UseSorting]
    public IQueryable<ModFile> GetModFiles(ArchiveDbContext db) =>
        db.ModFiles.OrderBy(x => x.DatasetId).ThenBy(x => x.Path);

    [GraphQLName("modFile")]
    public async Task<ModFile?> GetModFileAsync(
        Guid datasetId, string path, ArchiveDbContext db, CancellationToken cancellationToken) =>
        await db.ModFiles.FirstOrDefaultAsync(x => x.DatasetId == datasetId && x.Path == path, cancellationToken);

    /// <summary>
    /// The map from an id that a run gave an object to the address that the archive gave it (ADR-0034).
    /// A reader that holds an id from the run, from a log or from the game finds the object here.
    /// </summary>
    [UsePaging]
    [UseProjection]
    [UseFiltering]
    [UseSorting]
    public IQueryable<ModIdMap> GetModIdMaps(ArchiveDbContext db) =>
        db.ModIdMaps.OrderBy(x => x.DatasetId).ThenBy(x => x.Kind).ThenBy(x => x.RawId);

    /// <summary>
    /// Every attempt to load one mod version on one server release, with the outcome and the release
    /// (ADR-0013). A dataset is found from a mod version here.
    /// </summary>
    [UsePaging]
    [UseProjection]
    [UseFiltering]
    [UseSorting]
    public IQueryable<ModDataObservation> GetModDataObservations(ArchiveDbContext db) =>
        db.ModDataObservations.OrderByDescending(x => x.ObservedAt).ThenByDescending(x => x.Id);

    // ---- Runs (ADR-0033) ---------------------------------------------------------------------------

    /// <summary>
    /// The history of the Archiver, newest first. Each run states why it happened, what it was about and
    /// what came out, as JSON.
    /// </summary>
    [UsePaging]
    [UseProjection]
    [UseFiltering]
    [UseSorting]
    public IQueryable<ArchiveRun> GetArchiveRuns(ArchiveDbContext db) =>
        db.ArchiveRuns.OrderByDescending(x => x.StartedAt).ThenByDescending(x => x.Id);

    [GraphQLName("archiveRun")]
    public async Task<ArchiveRun?> GetArchiveRunAsync(Guid id, ArchiveDbContext db, CancellationToken cancellationToken) =>
        await db.ArchiveRuns.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

    // ---- Objects that have no table of their own (ADR-0041) ------------------------------------------

    /// <summary>
    /// The source of every location, bot type, hideout part, price and handbook entry that a dataset adds,
    /// one row for each file of a location. A modified object has no row here: its changed values are
    /// <c>modChangePaths</c> under its template.
    /// </summary>
    /// <remarks>
    /// The row states its small facts as fields, and the document itself is <c>rawJson</c>, which costs a
    /// hundred times a small field and answers <c>RAW_JSON_TOO_LARGE</c> above a limit of megabytes
    /// (ADR-0040, decision 8). A document of a map is 132 MB, so a client filters it with <c>pathEq</c> or
    /// reads the facts that a row states, and never pulls a page of documents.
    /// </remarks>
    [UsePaging]
    [UseProjection]
    [UseFiltering]
    [UseSorting]
    public IQueryable<ModDatabaseObject> GetModDatabaseObjects(ArchiveDbContext db) =>
        db.ModDatabaseObjects.OrderBy(x => x.DatasetId).ThenBy(x => x.Kind).ThenBy(x => x.TemplateId).ThenBy(x => x.Part);

    [GraphQLName("modDatabaseObject")]
    public async Task<ModDatabaseObject?> GetModDatabaseObjectAsync(
        Guid datasetId, string kind, string templateId, ArchiveDbContext db, CancellationToken cancellationToken, string? part = null) =>
        await db.ModDatabaseObjects.FirstOrDefaultAsync(
            x => x.DatasetId == datasetId && x.Kind == kind && x.TemplateId == templateId && x.Part == (part ?? string.Empty),
            cancellationToken);

    // ---- Items --------------------------------------------------------------------------------------

    [UsePaging]
    [UseProjection]
    [UseFiltering]
    [UseSorting]
    public async Task<IQueryable<Item>> GetItemsAsync(ArchiveDbContext db, string? sptVersion, CancellationToken cancellationToken)
    {
        var release = await Releases.ResolveAsync(db, sptVersion, cancellationToken);
        return db.Items.Where(x => x.SptVersion == release).OrderBy(x => x.Id);
    }

    [GraphQLName("item")]
    public async Task<Item?> GetItemAsync(string id, string? sptVersion, ArchiveDbContext db, CancellationToken cancellationToken)
    {
        var release = await Releases.ResolveAsync(db, sptVersion, cancellationToken);
        return await db.Items.FirstOrDefaultAsync(x => x.SptVersion == release && x.Id == id, cancellationToken);
    }

    [UsePaging]
    [UseProjection]
    [UseFiltering]
    [UseSorting]
    public IQueryable<ModItem> GetModItems(ArchiveDbContext db) => db.ModItems.OrderBy(x => x.DatasetId).ThenBy(x => x.Id);

    // ---- Player customization (clothes) -------------------------------------------------------

    /// <summary>
    /// Clothes, optionally only those that one side may wear. "Sides" is an array, and the generic
    /// list filter can only say "every element" or "no element" (HotChocolate has no "contains"
    /// for a list), so the side is an argument of its own. It maps to one array containment
    /// check, which the GIN index on the column serves.
    /// </summary>
    [UsePaging]
    [UseProjection]
    [UseFiltering]
    [UseSorting]
    public async Task<IQueryable<Customization>> GetCustomizationsAsync(
        ArchiveDbContext db, string? sptVersion, CancellationToken cancellationToken, string? side = null)
    {
        var release = await Releases.ResolveAsync(db, sptVersion, cancellationToken);
        var rows = db.Customizations.Where(x => x.SptVersion == release);
        return (side is null ? rows : rows.Where(row => row.Sides.Contains(side))).OrderBy(x => x.Id);
    }

    [GraphQLName("customization")]
    public async Task<Customization?> GetCustomizationAsync(string id, string? sptVersion, ArchiveDbContext db, CancellationToken cancellationToken)
    {
        var release = await Releases.ResolveAsync(db, sptVersion, cancellationToken);
        return await db.Customizations.FirstOrDefaultAsync(x => x.SptVersion == release && x.Id == id, cancellationToken);
    }

    [UsePaging]
    [UseProjection]
    [UseFiltering]
    [UseSorting]
    public IQueryable<ModCustomization> GetModCustomizations(ArchiveDbContext db, string? side = null) =>
        (side is null ? db.ModCustomizations : db.ModCustomizations.Where(row => row.Sides.Contains(side)))
        .OrderBy(x => x.DatasetId).ThenBy(x => x.Id);

    // ---- Translations -------------------------------------------------------------------------------

    [UsePaging]
    [UseProjection]
    [UseFiltering]
    [UseSorting]
    public async Task<IQueryable<LocaleEntry>> GetTranslationsAsync(ArchiveDbContext db, string? sptVersion, CancellationToken cancellationToken)
    {
        var release = await Releases.ResolveAsync(db, sptVersion, cancellationToken);
        return db.LocaleEntries.Where(x => x.SptVersion == release)
            .OrderBy(x => x.Scope).ThenBy(x => x.LanguageCode).ThenBy(x => x.Key);
    }

    [UsePaging]
    [UseProjection]
    [UseFiltering]
    [UseSorting]
    public IQueryable<ModLocaleEntry> GetModTranslations(ArchiveDbContext db) =>
        db.ModLocaleEntries.OrderBy(x => x.DatasetId).ThenBy(x => x.Scope).ThenBy(x => x.LanguageCode).ThenBy(x => x.Key);

    // ---- Quests -------------------------------------------------------------------------------------

    [UsePaging]
    [UseProjection]
    [UseFiltering]
    [UseSorting]
    public async Task<IQueryable<Quest>> GetQuestsAsync(ArchiveDbContext db, string? sptVersion, CancellationToken cancellationToken)
    {
        var release = await Releases.ResolveAsync(db, sptVersion, cancellationToken);
        return db.Quests.Where(x => x.SptVersion == release).OrderBy(x => x.Id);
    }

    [GraphQLName("quest")]
    public async Task<Quest?> GetQuestAsync(string id, string? sptVersion, ArchiveDbContext db, CancellationToken cancellationToken)
    {
        var release = await Releases.ResolveAsync(db, sptVersion, cancellationToken);
        return await db.Quests.FirstOrDefaultAsync(x => x.SptVersion == release && x.Id == id, cancellationToken);
    }

    [UsePaging]
    [UseProjection]
    [UseFiltering]
    [UseSorting]
    public IQueryable<ModQuest> GetModQuests(ArchiveDbContext db) => db.ModQuests.OrderBy(x => x.DatasetId).ThenBy(x => x.Id);

    // ---- Traders ------------------------------------------------------------------------------------

    [UsePaging]
    [UseProjection]
    [UseFiltering]
    [UseSorting]
    public async Task<IQueryable<Trader>> GetTradersAsync(ArchiveDbContext db, string? sptVersion, CancellationToken cancellationToken)
    {
        var release = await Releases.ResolveAsync(db, sptVersion, cancellationToken);
        return db.Traders.Where(x => x.SptVersion == release).OrderBy(x => x.Id);
    }

    [GraphQLName("trader")]
    public async Task<Trader?> GetTraderAsync(string id, string? sptVersion, ArchiveDbContext db, CancellationToken cancellationToken)
    {
        var release = await Releases.ResolveAsync(db, sptVersion, cancellationToken);
        return await db.Traders.FirstOrDefaultAsync(x => x.SptVersion == release && x.Id == id, cancellationToken);
    }

    [UsePaging]
    [UseProjection]
    [UseFiltering]
    [UseSorting]
    public IQueryable<ModTrader> GetModTraders(ArchiveDbContext db) => db.ModTraders.OrderBy(x => x.DatasetId).ThenBy(x => x.Id);

    // ---- Assort (what traders sell) -----------------------------------------------------------------

    [UsePaging]
    [UseProjection]
    [UseFiltering]
    [UseSorting]
    public async Task<IQueryable<TraderAssortItem>> GetAssortItemsAsync(ArchiveDbContext db, string? sptVersion, CancellationToken cancellationToken)
    {
        var release = await Releases.ResolveAsync(db, sptVersion, cancellationToken);
        return db.TraderAssortItems.Where(x => x.SptVersion == release).OrderBy(x => x.TraderId).ThenBy(x => x.InstanceId);
    }

    [UsePaging]
    [UseProjection]
    [UseFiltering]
    [UseSorting]
    public IQueryable<ModTraderAssortItem> GetModAssortItems(ArchiveDbContext db) =>
        db.ModTraderAssortItems.OrderBy(x => x.DatasetId).ThenBy(x => x.TraderId).ThenBy(x => x.InstanceId);

    [UsePaging]
    [UseProjection]
    [UseFiltering]
    [UseSorting]
    public async Task<IQueryable<TraderQuestAssort>> GetQuestAssortsAsync(ArchiveDbContext db, string? sptVersion, CancellationToken cancellationToken)
    {
        var release = await Releases.ResolveAsync(db, sptVersion, cancellationToken);
        return db.TraderQuestAssorts.Where(x => x.SptVersion == release)
            .OrderBy(x => x.TraderId).ThenBy(x => x.Status).ThenBy(x => x.AssortInstanceId);
    }

    [UsePaging]
    [UseProjection]
    [UseFiltering]
    [UseSorting]
    public IQueryable<ModTraderQuestAssort> GetModQuestAssorts(ArchiveDbContext db) =>
        db.ModTraderQuestAssorts.OrderBy(x => x.DatasetId).ThenBy(x => x.TraderId).ThenBy(x => x.Status).ThenBy(x => x.AssortInstanceId);
}
