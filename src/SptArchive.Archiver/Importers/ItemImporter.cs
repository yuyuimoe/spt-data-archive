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
            var template = property.Value;
            var props = template.TryGetProperty("_props", out var propsElement) && propsElement.ValueKind == JsonValueKind.Object
                ? propsElement
                : default;

            await context.QueueAsync(
                new Item
                {
                    SptVersion = context.SptVersion,
                    Id = property.Name,
                    Name = template.GetScalarString("_name") ?? property.Name,
                    ParentId = template.GetScalarString("_parent") ?? string.Empty,
                    Type = template.GetScalarString("_type") ?? string.Empty,
                    Prototype = template.GetScalarString("_proto"),
                    Weight = props.GetDoubleOrNull("Weight"),
                    Width = props.GetInt32OrNull("Width"),
                    Height = props.GetInt32OrNull("Height"),
                    StackMaxSize = props.GetInt32OrNull("StackMaxSize"),
                    Props = props.ValueKind == JsonValueKind.Object ? props.GetRawText() : "{}",
                },
                cancellationToken);

            count++;
        }

        await context.FlushAsync(cancellationToken);
        logger.LogInformation("Items: {Count} templates", count);
    }
}
