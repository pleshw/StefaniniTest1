extern alias OrdersWorker;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute.ExceptionExtensions;
using Orders.Application.Observability;
using OrdersWorker::Orders.Worker;

namespace Orders.IntegrationTests;

/// <summary>The Worker's metrics job polls the OnHold orders and the dead letter queue on a schedule and survives a failed round.</summary>
public sealed class OperationalMetricsJobTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public async Task ExecuteAsync_PollsAtStartAndAgainAfterEveryInterval()
    {
        var onHold = Substitute.For<IOnHoldProbe>();
        onHold.ReadAsync(Arg.Any<CancellationToken>()).Returns(new OnHoldSnapshot(0, null));
        var deadLetters = Substitute.For<IDeadLetterDepthProbe>();
        deadLetters.ReadAsync(Arg.Any<CancellationToken>()).Returns(0L);
        using var provider = Provider(onHold, deadLetters);
        var job = Job(provider);

        await job.StartAsync(Token);
        await WaitFor(() => onHold.ReceivedCalls().Count() >= 3);
        await job.StopAsync(Token);

        onHold.ReceivedCalls().Count().ShouldBeGreaterThanOrEqualTo(3);
        deadLetters.ReceivedCalls().Count().ShouldBeGreaterThanOrEqualTo(3);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ExecuteAsync_AProbeThatFails_DoesNotStopTheNextRound()
    {
        var onHold = Substitute.For<IOnHoldProbe>();
        onHold.ReadAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("db down"));
        var deadLetters = Substitute.For<IDeadLetterDepthProbe>();
        deadLetters.ReadAsync(Arg.Any<CancellationToken>()).Returns(2L);
        using var provider = Provider(onHold, deadLetters);
        var job = Job(provider);

        await job.StartAsync(Token);
        await WaitFor(() => deadLetters.ReceivedCalls().Count() >= 3);
        await job.StopAsync(Token);

        deadLetters.ReceivedCalls().Count().ShouldBeGreaterThanOrEqualTo(3);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Options_DefaultToThirtySeconds()
    {
        new OperationalMetricsOptions().PollInterval.ShouldBe(TimeSpan.FromSeconds(30));
    }

    private static OperationalMetricsJob Job(ServiceProvider provider) => new(
        provider.GetRequiredService<IServiceScopeFactory>(),
        new OperationalMetricsOptions { PollInterval = TimeSpan.FromMilliseconds(50) },
        NullLogger<OperationalMetricsJob>.Instance);

    private static ServiceProvider Provider(IOnHoldProbe onHold, IDeadLetterDepthProbe deadLetters) => new ServiceCollection()
        .AddSingleton(onHold)
        .AddSingleton(deadLetters)
        .AddSingleton<OrdersMetrics>()
        .AddScoped<OperationalMetricsRunner>()
        .BuildServiceProvider();

    private static async Task WaitFor(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);

        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20, Token);
        }
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;
}
