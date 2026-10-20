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
            var values = TemplateMapper.ReadQuest(property.Name, property.Value);
            await context.QueueAsync(TemplateMapper.Quest(values, context.SptVersion), cancellationToken);
            count++;
        }

        await context.FlushAsync(cancellationToken);
        logger.LogInformation("Quests: {Count}", count);
    }
}
