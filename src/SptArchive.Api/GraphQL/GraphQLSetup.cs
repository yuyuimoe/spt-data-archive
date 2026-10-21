using HotChocolate.Data.Filters.Expressions;
using HotChocolate.Execution.Configuration;
using SptArchive.Core.Data;

namespace SptArchive.Api.GraphQL;

public static class GraphQLSetup
{
    /// <summary>
    /// The read-only schema. The Program and the tests build it the same way, so a test reads the
    /// schema that the Api serves.
    /// </summary>
    public static IRequestExecutorBuilder AddArchiveGraphQL(this IServiceCollection services)
    {
        // The limits that the whole-document field enforces (ADR-0040, decision 8).
        services.AddOptions<RawJsonOptions>();

        return services
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
            .AddTypeExtension<ModDatasetJson>()
            .AddTypeExtension<ModChangePathJson>()
            .AddTypeExtension<ModFileJson>()
            .AddTypeExtension<ModDataObservationJson>()
            .AddTypeExtension<ArchiveRunJson>()
            .AddTypeExtension<ModDatabaseObjectJson>()
            // A jsonb column offers comparison, whole or by path, and no text matching: the text operations a
            // string filter announces fail in Postgres against jsonb (JsonFilter.cs, ADR-0040).
            .AddType<ItemFilter>()
            .AddType<ModItemFilter>()
            .AddType<CustomizationFilter>()
            .AddType<ModCustomizationFilter>()
            .AddType<QuestFilter>()
            .AddType<ModQuestFilter>()
            .AddType<TraderFilter>()
            .AddType<ModTraderFilter>()
            .AddType<TraderAssortItemFilter>()
            .AddType<ModTraderAssortItemFilter>()
            .AddType<ModFilter>()
            .AddType<ModDatasetFilter>()
            .AddType<ModChangePathFilter>()
            .AddType<ModFileFilter>()
            .AddType<ModDataObservationFilter>()
            .AddType<ArchiveRunFilter>()
            .AddType<ModDatabaseObjectFilter>()
            // A JSON value that the shape check passes and PostgreSQL refuses is an error with a code (ADR-0040).
            .AddErrorFilter<JsonValueErrorFilter>()
            // The default security policy switches introspection off outside Development. A public data browser
            // needs the schema, so it stays on.
            .DisableIntrospection(false)
            .AddProjections()
            .AddFiltering(filtering =>
            {
                filtering.AddDefaults();
                filtering.Operation(JsonFilterOperations.DocumentEquals).Name("eq");
                filtering.Operation(JsonFilterOperations.DocumentNotEquals).Name("neq");
                filtering.Operation(JsonFilterOperations.PathEquals).Name("pathEq");
                filtering.Operation(JsonFilterOperations.PathNotEquals).Name("pathNeq");
                filtering.AddProviderExtension(new QueryableFilterProviderExtension(provider =>
                    provider.AddFieldHandler(new JsonOperationHandler())));
            })
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
    }

    /// <summary>The largest GraphQL request body, in bytes.</summary>
    public const int MaxRequestBytes = 1_000_000;

    /// <summary>
    /// The most work that the resolvers of one query may cost. One field of one row costs 1, so this is
    /// about 1,800 rows: nine full pages, with every field of a row.
    /// </summary>
    public const double MaxFieldCost = 20_000;

    /// <summary>
    /// The most objects that one query may return, counting the page size of every list. A page costs its
    /// size, so this refuses a request that asks for ten pages at once.
    /// </summary>
    public const double MaxTypeCost = 2_000;

    /// <summary>
    /// What one row of <c>rawJson</c> costs, where every other field of a row costs 1 (ADR-0040, decision 8).
    /// A page of 200 rows with the whole document costs more than <see cref="MaxFieldCost" /> and is refused
    /// before it runs, while a page of 50 rows still fits. The number is a weight and not a size: the bytes
    /// that a request may carry are bounded by <see cref="RawJsonOptions" /> when it runs.
    /// </summary>
    public const double RawJsonCost = 100;
}
