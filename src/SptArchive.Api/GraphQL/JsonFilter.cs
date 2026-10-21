using HotChocolate.Data.Filters;
using HotChocolate.Types;
using SptArchive.Core.Entities;

namespace SptArchive.Api.GraphQL;

/// <summary>
/// A filter over a jsonb column (ADR-0040). The column is jsonb, so Postgres refuses the text operations that
/// <c>StringOperationFilterInputType</c> announces: <c>contains</c> would become <c>LIKE</c> on a jsonb value,
/// which has no such operator, and the request fails in the database. The type offers only operations that
/// <see cref="JsonOperationHandler"/> translates to jsonb operators:
/// <c>eq</c> and <c>neq</c> compare the whole document, as JSON text (a string needs its quotes); <c>pathEq</c> and
/// <c>pathNeq</c> compare the value at a path inside the document, as a JSON value.
/// </summary>
public sealed class JsonOperationFilterInputType : FilterInputType
{
    protected override void Configure(IFilterInputTypeDescriptor descriptor)
    {
        descriptor.Operation(JsonFilterOperations.DocumentEquals).Type<StringType>()
            .Description("The whole document equals this JSON text. A string is written with its quotes: \"\\\"Bear\\\"\".");
        descriptor.Operation(JsonFilterOperations.DocumentNotEquals).Type<StringType>()
            .Description("The whole document differs from this JSON text.");
        descriptor.Operation(JsonFilterOperations.PathEquals).Type<JsonPathConditionType>()
            .Description("The document holds this JSON value at this path.");
        descriptor.Operation(JsonFilterOperations.PathNotEquals).Type<JsonPathConditionType>()
            .Description("The document holds another value at this path, or nothing at it.");
    }
}

/// <summary>The input of <c>pathEq</c> and <c>pathNeq</c>. <see cref="JsonOperationHandler"/> reads it from the query document.</summary>
public sealed class JsonPathConditionType : InputObjectType
{
    protected override void Configure(IInputObjectTypeDescriptor descriptor)
    {
        descriptor.Name("JsonPathConditionInput");
        descriptor.Field("path").Type<NonNullType<ListType<NonNullType<StringType>>>>()
            .Description("The object keys and array indexes from the root of the document, as text, for example [\"Nested\", \"list\", \"1\"]. 1 to 10 segments.");
        descriptor.Field("value").Type<AnyType>()
            .Description("Any JSON value. Write null to compare with JSON null. A number, a string, a list and an object compare by content.");
    }
}

/// <summary>
/// Points the jsonb columns of an entity at <see cref="JsonOperationFilterInputType"/>. A type
/// extension changes only the fields that it names; the other fields keep the default filter input.
/// </summary>
public sealed class ItemFilter : FilterInputType<Item>
{
    protected override void Configure(IFilterInputTypeDescriptor<Item> d) => d.Json(x => x.Props);
}

public sealed class ModItemFilter : FilterInputType<ModItem>
{
    protected override void Configure(IFilterInputTypeDescriptor<ModItem> d) => d.Json(x => x.Props);
}

public sealed class CustomizationFilter : FilterInputType<Customization>
{
    protected override void Configure(IFilterInputTypeDescriptor<Customization> d) => d.Json(x => x.Props);
}

public sealed class ModCustomizationFilter : FilterInputType<ModCustomization>
{
    protected override void Configure(IFilterInputTypeDescriptor<ModCustomization> d) => d.Json(x => x.Props);
}

public sealed class QuestFilter : FilterInputType<Quest>
{
    protected override void Configure(IFilterInputTypeDescriptor<Quest> d) => d.Json(x => x.Data);
}

public sealed class ModQuestFilter : FilterInputType<ModQuest>
{
    protected override void Configure(IFilterInputTypeDescriptor<ModQuest> d) => d.Json(x => x.Data);
}

public sealed class TraderFilter : FilterInputType<Trader>
{
    protected override void Configure(IFilterInputTypeDescriptor<Trader> d)
    {
        d.Json(x => x.Base);
        d.Json(x => x.Dialogue);
        d.Json(x => x.Suits);
        d.Json(x => x.Services);
    }
}

public sealed class ModTraderFilter : FilterInputType<ModTrader>
{
    protected override void Configure(IFilterInputTypeDescriptor<ModTrader> d)
    {
        d.Json(x => x.Base);
        d.Json(x => x.Dialogue);
        d.Json(x => x.Suits);
        d.Json(x => x.Services);
    }
}

public sealed class TraderAssortItemFilter : FilterInputType<TraderAssortItem>
{
    protected override void Configure(IFilterInputTypeDescriptor<TraderAssortItem> d)
    {
        d.Json(x => x.Upd);
        d.Json(x => x.BarterScheme);
    }
}

public sealed class ModTraderAssortItemFilter : FilterInputType<ModTraderAssortItem>
{
    protected override void Configure(IFilterInputTypeDescriptor<ModTraderAssortItem> d)
    {
        d.Json(x => x.Upd);
        d.Json(x => x.BarterScheme);
    }
}

public sealed class ModFilter : FilterInputType<Mod>
{
    protected override void Configure(IFilterInputTypeDescriptor<Mod> d) => d.Json(x => x.Metadata);
}

public sealed class ModDatasetFilter : FilterInputType<ModDataset>
{
    protected override void Configure(IFilterInputTypeDescriptor<ModDataset> d)
    {
        d.Json(x => x.TableHashes);
        d.Json(x => x.RowCounts);
    }
}

public sealed class ModChangePathFilter : FilterInputType<ModChangePath>
{
    protected override void Configure(IFilterInputTypeDescriptor<ModChangePath> d)
    {
        d.Json(x => x.OldValue);
        d.Json(x => x.NewValue);
    }
}

public sealed class ModFileFilter : FilterInputType<ModFile>
{
    protected override void Configure(IFilterInputTypeDescriptor<ModFile> d) => d.Json(x => x.Content);
}

public sealed class ModDataObservationFilter : FilterInputType<ModDataObservation>
{
    protected override void Configure(IFilterInputTypeDescriptor<ModDataObservation> d) => d.Json(x => x.DependencySet);
}

public sealed class ArchiveRunFilter : FilterInputType<ArchiveRun>
{
    protected override void Configure(IFilterInputTypeDescriptor<ArchiveRun> d) => d.Json(x => x.Result);
}

internal static class JsonFilterExtensions
{
    public static void Json<T>(this IFilterInputTypeDescriptor<T> descriptor, System.Linq.Expressions.Expression<Func<T, string?>> column)
        where T : class =>
        descriptor.Field(column).Type<JsonOperationFilterInputType>();
}
