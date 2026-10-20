using System.Linq.Expressions;
using HotChocolate.Types;
using SptArchive.Core.Entities;

namespace SptArchive.Api.GraphQL;

/// <summary>
/// Exposes every jsonb column as an <c>Any</c> scalar value (HotChocolate has no JSON scalar; <c>Any</c> writes the value as JSON) (TD-15). The entity keeps the text,
/// so projection still reads the one column and the stored data does not change.
/// </summary>
internal static class JsonColumns
{
    public static void Json<T>(this IObjectTypeDescriptor<T> descriptor, Expression<Func<T, string?>> column, bool required = false)
        where T : class
    {
        var read = column.Compile();
        var field = descriptor.Field(column);

        if (required)
        {
            field.Type<NonNullType<AnyType>>().Resolve(context => JsonText.Parse(read(context.Parent<T>())));
        }
        else
        {
            field.Type<AnyType>().Resolve(context => JsonText.Parse(read(context.Parent<T>())));
        }
    }
}

public sealed class ItemJson : ObjectTypeExtension<Item>
{
    protected override void Configure(IObjectTypeDescriptor<Item> d) => d.Json(x => x.Props, required: true);
}

public sealed class ModItemJson : ObjectTypeExtension<ModItem>
{
    protected override void Configure(IObjectTypeDescriptor<ModItem> d) => d.Json(x => x.Props, required: true);
}

public sealed class CustomizationJson : ObjectTypeExtension<Customization>
{
    protected override void Configure(IObjectTypeDescriptor<Customization> d) => d.Json(x => x.Props, required: true);
}

public sealed class ModCustomizationJson : ObjectTypeExtension<ModCustomization>
{
    protected override void Configure(IObjectTypeDescriptor<ModCustomization> d) => d.Json(x => x.Props, required: true);
}

public sealed class QuestJson : ObjectTypeExtension<Quest>
{
    protected override void Configure(IObjectTypeDescriptor<Quest> d) => d.Json(x => x.Data, required: true);
}

public sealed class ModQuestJson : ObjectTypeExtension<ModQuest>
{
    protected override void Configure(IObjectTypeDescriptor<ModQuest> d) => d.Json(x => x.Data, required: true);
}

public sealed class TraderJson : ObjectTypeExtension<Trader>
{
    protected override void Configure(IObjectTypeDescriptor<Trader> d)
    {
        d.Json(x => x.Base, required: true);
        d.Json(x => x.Dialogue);
        d.Json(x => x.Suits);
        d.Json(x => x.Services);
    }
}

public sealed class ModTraderJson : ObjectTypeExtension<ModTrader>
{
    protected override void Configure(IObjectTypeDescriptor<ModTrader> d)
    {
        d.Json(x => x.Base, required: true);
        d.Json(x => x.Dialogue);
        d.Json(x => x.Suits);
        d.Json(x => x.Services);
    }
}

public sealed class TraderAssortItemJson : ObjectTypeExtension<TraderAssortItem>
{
    protected override void Configure(IObjectTypeDescriptor<TraderAssortItem> d)
    {
        d.Json(x => x.Upd);
        d.Json(x => x.BarterScheme);
    }
}

public sealed class ModTraderAssortItemJson : ObjectTypeExtension<ModTraderAssortItem>
{
    protected override void Configure(IObjectTypeDescriptor<ModTraderAssortItem> d)
    {
        d.Json(x => x.Upd);
        d.Json(x => x.BarterScheme);
    }
}

public sealed class ModJson : ObjectTypeExtension<Mod>
{
    protected override void Configure(IObjectTypeDescriptor<Mod> d) => d.Json(x => x.Metadata);
}
