using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SptArchive.Core.Entities;

namespace SptArchive.Archiver.Importers;

/// <summary>
/// Imports <c>templates/customization.json</c>: a map of customization id to
/// <c>{ _id, _name, _parent, _type, _props }</c>. These are the clothes a player wears, and the mods
/// that add clothes write to the same table (ADR-0022).
/// </summary>
public sealed class CustomizationImporter(ILogger<CustomizationImporter> logger) : IImporter
{
    public string Name => "customization";

    public async Task ImportAsync(ImportContext context, CancellationToken cancellationToken)
    {
        var path = Path.Combine(context.DatabasePath, "templates", "customization.json");
        using var document = await SourceFiles.ReadJsonAsync(path, cancellationToken);

        // Replace the rows of this release only. Rows of another release are a different dataset (ADR-0014).
        await context.Db.Customizations
            .Where(row => row.SptVersion == context.SptVersion)
            .ExecuteDeleteAsync(cancellationToken);

        var count = 0;

        foreach (var property in document.RootElement.EnumerateObject())
        {
            var values = TemplateMapper.ReadCustomization(property.Name, property.Value);
            await context.QueueAsync(TemplateMapper.Customization(values, context.SptVersion), cancellationToken);
            count++;
        }

        await context.FlushAsync(cancellationToken);
        logger.LogInformation("Customizations: {Count}", count);
    }
}
