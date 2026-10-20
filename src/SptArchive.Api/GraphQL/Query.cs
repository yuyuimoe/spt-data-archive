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
/// Fields holding raw JSON (<c>props</c>, <c>data</c>, <c>base</c>, ...) are returned as <c>JSON</c> values (TD-15).
/// Every official field takes an optional <c>sptVersion</c>: one exact archived release, or the latest when it is
/// left out (ADR-0014, ADR-0019). Each official row states its release in its <c>sptVersion</c> field.
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
