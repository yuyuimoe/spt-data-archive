using HotChocolate.Execution.Configuration;
using SptArchive.Core.Data;

namespace SptArchive.Api.GraphQL;

public static class GraphQLSetup
{
    /// <summary>
    /// The read-only schema. The Program and the tests build it the same way, so a test reads the
    /// schema that the Api serves.
    /// </summary>
    public static IRequestExecutorBuilder AddArchiveGraphQL(this IServiceCollection services) =>
        services
            // Every real query is a few hundred bytes. A larger body is refused before it is parsed.
            .AddGraphQLServer(maxAllowedRequestSize: MaxRequestBytes)
            .RegisterDbContextFactory<ArchiveDbContext>()
            .AddQueryType<Query>()
            .AddType<HotChocolate.Types.AnyType>()
            .AddTypeExtension<ItemJson>()
            .AddTypeExtension<ModItemJson>()
            .AddTypeExtension<CustomizationJson>()
            .AddTypeExtension<ModCustomizationJson>()
            .AddTypeExtension<QuestJson>()
            .AddTypeExtension<ModQuestJson>()
            .AddTypeExtension<TraderJson>()
            .AddTypeExtension<ModTraderJson>()
            .AddTypeExtension<TraderAssortItemJson>()
            .AddTypeExtension<ModTraderAssortItemJson>()
            .AddTypeExtension<ModJson>()
            // The default security policy switches introspection off outside Development. A public data browser
            // needs the schema, so it stays on.
            .DisableIntrospection(false)
            .AddProjections()
            .AddFiltering()
            .AddSorting()
            .ModifyPagingOptions(o =>
            {
                o.DefaultPageSize = 50;
                o.MaxPageSize = 200;
                o.IncludeTotalCount = true;
            })
            .ModifyRequestOptions(o => o.ExecutionTimeout = TimeSpan.FromSeconds(10))
            .AddMaxExecutionDepthRule(10)
            // Depth bounds nesting and not breadth. Cost bounds a wide query: many fields, many rows per page,
            // and the filter and sort terms that become SQL. Introspection stays on: schema discovery is the product.
            .ModifyCostOptions(o =>
            {
                o.EnforceCostLimits = true;
                o.MaxFieldCost = MaxFieldCost;
                o.MaxTypeCost = MaxTypeCost;
            });

    /// <summary>The largest GraphQL request body, in bytes.</summary>
    public const int MaxRequestBytes = 1_000_000;

    /// <summary>The most work that the resolvers of one query may cost.</summary>
    public const double MaxFieldCost = 2_000;

    /// <summary>The most objects that one query may return, counting the page size of every list.</summary>
    public const double MaxTypeCost = 2_000;
}
