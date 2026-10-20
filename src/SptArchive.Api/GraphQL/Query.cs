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
/// Fields holding raw JSON (<c>props</c>, <c>data</c>, <c>base</c>, ...) are returned as JSON text.
/// </remarks>
public sealed class Query
{
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
    public IQueryable<Item> GetItems(ArchiveDbContext db) => db.Items.OrderBy(x => x.Id);

    [GraphQLName("item")]
    public Task<Item?> GetItemAsync(string id, ArchiveDbContext db, CancellationToken cancellationToken) =>
        db.Items.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

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
    public IQueryable<Customization> GetCustomizations(ArchiveDbContext db, string? side = null) =>
        (side is null ? db.Customizations : db.Customizations.Where(row => row.Sides.Contains(side)))
        .OrderBy(x => x.Id);

    [GraphQLName("customization")]
    public Task<Customization?> GetCustomizationAsync(string id, ArchiveDbContext db, CancellationToken cancellationToken) =>
        db.Customizations.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

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
    public IQueryable<LocaleEntry> GetTranslations(ArchiveDbContext db) =>
        db.LocaleEntries.OrderBy(x => x.Scope).ThenBy(x => x.LanguageCode).ThenBy(x => x.Key);

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
    public IQueryable<Quest> GetQuests(ArchiveDbContext db) => db.Quests.OrderBy(x => x.Id);

    [GraphQLName("quest")]
    public Task<Quest?> GetQuestAsync(string id, ArchiveDbContext db, CancellationToken cancellationToken) =>
        db.Quests.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

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
    public IQueryable<Trader> GetTraders(ArchiveDbContext db) => db.Traders.OrderBy(x => x.Id);

    [GraphQLName("trader")]
    public Task<Trader?> GetTraderAsync(string id, ArchiveDbContext db, CancellationToken cancellationToken) =>
        db.Traders.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

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
    public IQueryable<TraderAssortItem> GetAssortItems(ArchiveDbContext db) =>
        db.TraderAssortItems.OrderBy(x => x.TraderId).ThenBy(x => x.InstanceId);

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
    public IQueryable<TraderQuestAssort> GetQuestAssorts(ArchiveDbContext db) =>
        db.TraderQuestAssorts.OrderBy(x => x.TraderId).ThenBy(x => x.Status).ThenBy(x => x.AssortInstanceId);

    [UsePaging]
    [UseProjection]
    [UseFiltering]
    [UseSorting]
    public IQueryable<ModTraderQuestAssort> GetModQuestAssorts(ArchiveDbContext db) =>
        db.ModTraderQuestAssorts.OrderBy(x => x.DatasetId).ThenBy(x => x.TraderId).ThenBy(x => x.Status).ThenBy(x => x.AssortInstanceId);
}
