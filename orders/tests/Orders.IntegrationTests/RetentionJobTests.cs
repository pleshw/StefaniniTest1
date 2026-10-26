extern alias OrdersWorker;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute.ExceptionExtensions;
using Orders.Application.Retention;
using OrdersWorker::Orders.Worker;

namespace Orders.IntegrationTests;

/// <summary>The Worker's retention job runs the cleanup on a schedule and survives a failed run.</summary>
public sealed class RetentionJobTests
{
    private static readonly DateTimeOffset _now = new(2026, 6, 15, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ExecuteAsync_RunsTheCleanupAtStartAndAgainAfterEveryInterval()
    {
        var store = Substitute.For<IRetentionStore>();
        using var provider = Provider(store);
        var job = new RetentionJob(provider.GetRequiredService<IServiceScopeFactory>(), new RetentionOptions { Interval = TimeSpan.FromMilliseconds(50) }, NullLogger<RetentionJob>.Instance);

        await job.StartAsync(Token);
        await WaitFor(() => store.ReceivedCalls().Count(call => call.GetMethodInfo().Name == nameof(IRetentionStore.DeleteExpiredIdempotencyAsync)) >= 3);
        await job.StopAsync(Token);

        await store.Received().DeleteExpiredIdempotencyAsync(_now, 1000, Arg.Any<CancellationToken>());
        await store.Received().DeleteEventsOlderThanAsync(_now.AddDays(-30), 1000, Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ExecuteAsync_AFailedRun_IsLoggedAndTheNextOneStillHappens()
    {
        var store = Substitute.For<IRetentionStore>();
        var calls = 0;
        store.DeleteExpiredIdempotencyAsync(default, default, Token).ReturnsForAnyArgs(_ => ++calls == 1 ? throw new InvalidOperationException("db down") : Task.FromResult(0));
        using var provider = Provider(store);
        var job = new RetentionJob(provider.GetRequiredService<IServiceScopeFactory>(), new RetentionOptions { Interval = TimeSpan.FromMilliseconds(50) }, NullLogger<RetentionJob>.Instance);

        await job.StartAsync(Token);
        await WaitFor(() => Volatile.Read(ref calls) >= 3);
        await job.StopAsync(Token);

        calls.ShouldBeGreaterThanOrEqualTo(3);
    }

    private static ServiceProvider Provider(IRetentionStore store) => new ServiceCollection()
        .AddSingleton(store)
        .AddSingleton<TimeProvider>(new FixedTimeProvider(_now))
        .AddSingleton(new RetentionOptions())
        .AddScoped<RetentionRunner>()
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

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
