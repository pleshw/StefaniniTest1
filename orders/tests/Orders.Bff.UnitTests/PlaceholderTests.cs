namespace Orders.Bff.UnitTests;

public sealed class PlaceholderTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public void RunnerIsWired_ProjectBuilds_TestRuns()
    {
        (1 + 1).ShouldBe(2);
    }
}
