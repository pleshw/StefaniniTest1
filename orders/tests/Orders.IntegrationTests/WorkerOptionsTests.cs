extern alias OrdersWorker;

using Microsoft.Extensions.Configuration;
using OrdersWorker::Orders.Worker;

namespace Orders.IntegrationTests;

/// <summary>The retry delays of the Worker come from configuration, so tests can shorten them.</summary>
public sealed class WorkerOptionsTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public void RetryDelays_NotConfigured_AreFiveSecondsThirtySecondsAndFiveMinutes()
    {
        var options = Bind([]);

        options.EffectiveRetryDelays.ShouldBe([TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(5)]);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void RetryDelays_Configured_ReplaceTheDefaultsInsteadOfAddingToThem()
    {
        var options = Bind(new Dictionary<string, string?>
        {
            ["Orders:Worker:RetryDelays:0"] = "00:00:00.2",
            ["Orders:Worker:RetryDelays:1"] = "00:00:00.3",
            ["Orders:Worker:RetryDelays:2"] = "00:00:00.4",
        });

        options.EffectiveRetryDelays.ShouldBe([TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(400)]);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ScheduledJobPollingInterval_NotConfigured_IsFiveSeconds()
    {
        Bind([]).ScheduledJobPollingInterval.ShouldBe(TimeSpan.FromSeconds(5));
    }

    private static WorkerOptions Bind(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build().GetSection(WorkerOptions.SectionName).Get<WorkerOptions>() ?? new WorkerOptions();
}
