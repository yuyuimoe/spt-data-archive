using HotChocolate.Data.Filters;
using HotChocolate.Types;
using SptArchive.Core.Entities;

namespace SptArchive.Api.GraphQL;

/// <summary>
/// A filter over a jsonb column offers comparison only. The column is jsonb, so Postgres refuses the
/// text operations that <c>StringOperationFilterInput</c> announces: <c>contains</c> would become
/// <c>LIKE</c> on a jsonb value, which has no such operator, and the request fails in the database.
/// Equality and inequality work, because Postgres compares two jsonb values by their content, so the
/// key order and the spacing of the text do not matter. Filtering by a path inside the document is
/// not built yet (TD-52).
/// </summary>
public sealed class JsonOperationFilterInputType : StringOperationFilterInputType
{
    protected override void Configure(IFilterInputTypeDescriptor descriptor)
    {
        descriptor.Operation(DefaultFilterOperations.Equals).Type<StringType>();
        descriptor.Operation(DefaultFilterOperations.NotEquals).Type<StringType>();
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

internal static class JsonFilterExtensions
{
    public static void Json<T>(this IFilterInputTypeDescriptor<T> descriptor, System.Linq.Expressions.Expression<Func<T, string?>> column)
        where T : class =>
        descriptor.Field(column).Type<JsonOperationFilterInputType>();
}
