namespace Catalog.ArchitectureTests;

public sealed class PlaceholderTests
{
    [Fact]
    [Trait("Category", "Architecture")]
    public void RunnerIsWired_ProjectBuilds_TestRuns()
    {
        (1 + 1).ShouldBe(2);
    }
}
