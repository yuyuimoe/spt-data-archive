using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SptArchive.Core.Entities;

namespace SptArchive.Archiver.Importers;

/// <summary>
/// Imports <c>traders/{traderId}/</c>: <c>base.json</c> (required), <c>assort.json</c>, <c>questassort.json</c>
/// and the optional <c>dialogue.json</c>, <c>suits.json</c> and <c>services.json</c>.
/// </summary>
/// <remarks>
/// <c>bearsuits.json</c> and <c>usecsuits.json</c> (side-specific clothing) are not imported yet.
/// </remarks>
public sealed class TraderImporter(ILogger<TraderImporter> logger) : IImporter
{
    public string Name => "traders";

    public async Task ImportAsync(ImportContext context, CancellationToken cancellationToken)
    {
        var tradersDirectory = Path.Combine(context.DatabasePath, "traders");

        // Children first, so the foreign keys to traders are never violated.
        // Only the rows of this release are replaced; other releases keep their own traders (ADR-0014).
        await context.Db.TraderQuestAssorts
            .Where(row => row.SptVersion == context.SptVersion)
            .ExecuteDeleteAsync(cancellationToken);
        await context.Db.TraderAssortItems
            .Where(row => row.SptVersion == context.SptVersion)
            .ExecuteDeleteAsync(cancellationToken);
        await context.Db.Traders
            .Where(trader => trader.SptVersion == context.SptVersion)
            .ExecuteDeleteAsync(cancellationToken);

        var traders = 0;
        var offers = 0;

        var directories = Directory.EnumerateDirectories(tradersDirectory).OrderBy(d => d, StringComparer.Ordinal);

        foreach (var directory in directories)
        {
            var traderId = Path.GetFileName(directory);
            var basePath = Path.Combine(directory, "base.json");

            if (!File.Exists(basePath))
            {
                logger.LogWarning("Trader folder {Folder} has no base.json; skipping", traderId);
                continue;
            }

            using var baseDocument = await SourceFiles.ReadJsonAsync(basePath, cancellationToken);
            var trader = baseDocument.RootElement;

            var values = TemplateMapper.ReadTrader(
                traderId,
                trader,
                await ReadRawAsync(directory, "dialogue.json", cancellationToken),
                await ReadRawAsync(directory, "suits.json", cancellationToken),
                await ReadRawAsync(directory, "services.json", cancellationToken));

            await context.QueueAsync(TemplateMapper.Trader(values, context.SptVersion), cancellationToken);

            offers += await ImportAssortAsync(context, traderId, directory, cancellationToken);
            await ImportQuestAssortAsync(context, traderId, directory, cancellationToken);

            traders++;
        }

        await context.FlushAsync(cancellationToken);
        logger.LogInformation("Traders: {Traders} traders, {Offers} assort rows", traders, offers);
    }

    private static async Task<string?> ReadRawAsync(string directory, string fileName, CancellationToken cancellationToken)
    {
        var path = Path.Combine(directory, fileName);

        if (!File.Exists(path))
        {
            return null;
        }

        using var document = await SourceFiles.ReadJsonAsync(path, cancellationToken);
        return document.RootElement.GetRawText();
    }

    /// <summary>
    /// <c>assort.json</c> has three parts: <c>items</c> (the offer trees), <c>barter_scheme</c> and
    /// <c>loyal_level_items</c>, the last two keyed by the instance id of each root item.
    /// They are folded into one row per item so an offer is a single query.
    /// </summary>
    private static async Task<int> ImportAssortAsync(
        ImportContext context,
        string traderId,
        string directory,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(directory, "assort.json");

        if (!File.Exists(path))
        {
            return 0;
        }

        using var document = await SourceFiles.ReadJsonAsync(path, cancellationToken);
        var root = document.RootElement;

        // The server writes "null" for a trader with no offer at all (Ref in 4.1.6). Null is not a missing file.
        if (root.ValueKind != JsonValueKind.Object)
        {
            return 0;
        }

        if (!root.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
        {
            return 0;
        }

        root.TryGetProperty("barter_scheme", out var barterSchemes);
        root.TryGetProperty("loyal_level_items", out var loyaltyLevels);

        var count = 0;

        foreach (var item in items.EnumerateArray())
        {
            var instanceId = item.GetScalarString("_id");

            if (instanceId is null)
            {
                continue;
            }

            string? barterScheme = null;
            if (barterSchemes.ValueKind == JsonValueKind.Object && barterSchemes.TryGetProperty(instanceId, out var scheme))
            {
                barterScheme = scheme.GetRawText();
            }

            int? loyaltyLevel = null;
            if (loyaltyLevels.ValueKind == JsonValueKind.Object && loyaltyLevels.TryGetProperty(instanceId, out var level))
            {
                loyaltyLevel = level.TryGetInt32(out var parsed) ? parsed : null;
            }

            await context.QueueAsync(
                new TraderAssortItem
                {
                    SptVersion = context.SptVersion,
                    TraderId = traderId,
                    InstanceId = instanceId,
                    TemplateId = item.GetScalarString("_tpl") ?? string.Empty,
                    ParentId = item.GetScalarString("parentId"),
                    SlotId = item.GetScalarString("slotId"),
                    Upd = item.GetRawOrNull("upd"),
                    LoyaltyLevel = loyaltyLevel,
                    BarterScheme = barterScheme,
                },
                cancellationToken);

            count++;
        }

        return count;
    }

    /// <summary>
    /// <c>questassort.json</c> maps a quest state ("started", "success", "fail") to
    /// <c>{ assortInstanceId: questId }</c>: the quest state that unlocks an offer.
    /// </summary>
    private static async Task ImportQuestAssortAsync(
        ImportContext context,
        string traderId,
        string directory,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(directory, "questassort.json");

        if (!File.Exists(path))
        {
            return;
        }

        using var document = await SourceFiles.ReadJsonAsync(path, cancellationToken);

        // The server writes "null" for a trader with no quest link at all (Ref in 4.1.6).
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var status in document.RootElement.EnumerateObject())
        {
            if (status.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            foreach (var unlock in status.Value.EnumerateObject())
            {
                await context.QueueAsync(
                    new TraderQuestAssort
                    {
                        SptVersion = context.SptVersion,
                        TraderId = traderId,
                        Status = status.Name,
                        AssortInstanceId = unlock.Name,
                        QuestId = unlock.Value.GetString() ?? string.Empty,
                    },
                    cancellationToken);
            }
        }
    }
}
