using System.Text.Json;

namespace SptArchive.Tests.Specs;

/// <summary>
/// What the steps of one scenario share: the test transaction and the last answer. Reqnroll makes one
/// instance for each scenario and hands it to every step class that asks for it, so a feature may use the
/// "Then" steps of another feature (both mod-api.feature and query-api.feature say "the response has no
/// errors") without a second copy of them.
/// </summary>
public sealed class QueryWorld
{
    private TestScope? _scope;

    /// <summary>The last answer of a GraphQL request, as the Api writes it.</summary>
    public JsonElement Response { get; set; }

    /// <summary>The Api field that the last answer is about.</summary>
    public string Field { get; set; } = "";

    /// <summary>The SQL that the last request ran, when a step collects it.</summary>
    public List<string> Commands { get; } = [];

    public async Task<TestScope> ScopeAsync() =>
        _scope ??= await ArchiveDatabase.BeginAsync(TestContext.Current.CancellationToken);

    /// <summary>Rolls the scenario back. Safe to call twice.</summary>
    public async Task DisposeAsync()
    {
        if (_scope is not null)
        {
            var scope = _scope;
            _scope = null;
            await scope.DisposeAsync();
        }
    }
}
