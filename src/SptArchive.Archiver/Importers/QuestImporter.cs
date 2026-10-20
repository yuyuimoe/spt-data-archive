using Microsoft.EntityFrameworkCore;
using SptArchive.Core.Entities;

namespace SptArchive.Archiver.Importers;

/// <summary>
/// Imports <c>templates/quests.json</c>: a map of quest id to the quest object. The full object is
/// kept as jsonb; only the fields used to find quests become columns.
/// </summary>
public sealed class QuestImporter(ILogger<QuestImporter> logger) : IImporter
{
    public string Name => "quests";

    public async Task ImportAsync(ImportContext context, CancellationToken cancellationToken)
    {
        var path = Path.Combine(context.DatabasePath, "templates", "quests.json");
        using var document = await SourceFiles.ReadJsonAsync(path, cancellationToken);

        await context.Db.Quests
            .Where(quest => quest.SptVersion == context.SptVersion)
            .ExecuteDeleteAsync(cancellationToken);

        var count = 0;

        foreach (var property in document.RootElement.EnumerateObject())
        {
            var quest = property.Value;

            await context.QueueAsync(
                new Quest
                {
                    SptVersion = context.SptVersion,
                    Id = property.Name,
                    QuestName = quest.GetScalarString("QuestName"),
                    TraderId = quest.GetScalarString("traderId") ?? string.Empty,
                    Location = quest.GetScalarString("location"),
                    Type = quest.GetScalarString("type"),
                    Side = quest.GetScalarString("side"),
                    Restartable = quest.GetBool("restartable"),
                    Data = quest.GetRawText(),
                },
                cancellationToken);

            count++;
        }

        await context.FlushAsync(cancellationToken);
        logger.LogInformation("Quests: {Count}", count);
    }
}
