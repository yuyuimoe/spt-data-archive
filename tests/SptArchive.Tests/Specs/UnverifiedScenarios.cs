using Reqnroll;
using Xunit;

namespace SptArchive.Tests.Specs;

/// <summary>
/// A scenario that keeps the tag <c>@unverified</c> has no step definitions yet, and its reason is in the
/// tech-debt list. The runner reports it as skipped, so a linked feature file never shows it as passing and
/// never fails on a step that does not exist (TD-62).
/// </summary>
[Binding]
public sealed class UnverifiedScenarios
{
    [BeforeScenario("unverified", Order = 0)]
    public void SkipScenario() =>
        Assert.Skip("The scenario is tagged @unverified: no test runs it.");
}
