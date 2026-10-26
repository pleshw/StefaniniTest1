namespace Orders.IntegrationTests;

public sealed class PlaceholderTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public void RunnerIsWired_ProjectBuilds_TestRuns()
    {
        (1 + 1).ShouldBe(2);
    }
}
