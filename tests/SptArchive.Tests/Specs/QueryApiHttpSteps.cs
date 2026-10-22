using Reqnroll;

namespace SptArchive.Tests.Specs;

/// <summary>The steps of <c>docs/specs/query-api.feature</c> that send an HTTP request to the Api.</summary>
[Binding]
public sealed class QueryApiHttpSteps(QueryWorld world)
{
    private readonly QueryWorld _world = world;
}
