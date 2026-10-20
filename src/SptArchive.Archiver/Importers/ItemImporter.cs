using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SptArchive.Core.Entities;

namespace SptArchive.Archiver.Importers;

/// <summary>
/// Imports <c>templates/items.json</c>: a map of item id to <c>{ _id, _name, _parent, _type, _props, _proto }</c>.
/// </summary>
/// <remarks>
/// In the server repository this file is stored with Git LFS, so it only exists after "git lfs pull".
/// </remarks>
public sealed class ItemImporter(ILogger<ItemImporter> logger) : IImporter
{
    public string Name => "items";

    public async Task ImportAsync(ImportContext context, CancellationToken cancellationToken)
    {
        var path = Path.Combine(context.DatabasePath, "templates", "items.json");
        using var document = await SourceFiles.ReadJsonAsync(path, cancellationToken);

        // Replace the rows of this release only. Rows of another release are a different dataset (ADR-0014).
        await context.Db.Items
            .Where(item => item.SptVersion == context.SptVersion)
            .ExecuteDeleteAsync(cancellationToken);

        var count = 0;

        foreach (var property in document.RootElement.EnumerateObject())
        {
            var values = TemplateMapper.ReadItem(property.Name, property.Value);
            await context.QueueAsync(TemplateMapper.OfficialItem(values, context.SptVersion), cancellationToken);
            count++;
        }

        await context.FlushAsync(cancellationToken);
        logger.LogInformation("Items: {Count} templates", count);
    }
}
