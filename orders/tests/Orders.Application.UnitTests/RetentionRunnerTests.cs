using Orders.Application.Retention;

namespace Orders.Application.UnitTests;

public sealed class RetentionRunnerTests
{
    private static readonly DateTimeOffset _now = new(2026, 6, 15, 12, 0, 0, TimeSpan.Zero);

    private readonly IRetentionStore _store = Substitute.For<IRetentionStore>();

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RunAsync_ExpiredRowsInSeveralBatches_DeletesBatchesOfOneThousandUntilABatchIsNotFull()
    {
        _store.DeleteExpiredIdempotencyAsync(_now, 1000, Token).Returns(1000, 1000, 37);
        _store.DeleteEventsOlderThanAsync(_now.AddDays(-30), 1000, Token).Returns(1000, 0);

        var result = await Runner().RunAsync(Token);

        result.ShouldBe(new RetentionResult(2037, 1000));
        await _store.Received(3).DeleteExpiredIdempotencyAsync(_now, 1000, Token);
        await _store.Received(2).DeleteEventsOlderThanAsync(_now.AddDays(-30), 1000, Token);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RunAsync_NothingExpired_AsksEachStoreOnce()
    {
        var result = await Runner().RunAsync(Token);

        result.ShouldBe(new RetentionResult(0, 0));
        await _store.Received(1).DeleteExpiredIdempotencyAsync(_now, 1000, Token);
        await _store.Received(1).DeleteEventsOlderThanAsync(_now.AddDays(-30), 1000, Token);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RunAsync_ConfiguredRetentionAndBatchSize_AreUsed()
    {
        var runner = new RetentionRunner(_store, new FixedTimeProvider(_now), new RetentionOptions { EventLogRetention = TimeSpan.FromDays(7), BatchSize = 50 });

        await runner.RunAsync(Token);

        await _store.Received(1).DeleteExpiredIdempotencyAsync(_now, 50, Token);
        await _store.Received(1).DeleteEventsOlderThanAsync(_now.AddDays(-7), 50, Token);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RunAsync_Cancelled_StopsBetweenBatches()
    {
        using var cancellation = new CancellationTokenSource();
        _store.DeleteExpiredIdempotencyAsync(_now, 1000, Arg.Any<CancellationToken>()).Returns(call =>
        {
            cancellation.Cancel();
            return 1000;
        });

        await Should.ThrowAsync<OperationCanceledException>(() => Runner().RunAsync(cancellation.Token));

        await _store.Received(1).DeleteExpiredIdempotencyAsync(_now, 1000, Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void RetentionOptions_Defaults_AreThirtyDaysOneThousandAndOneHour()
    {
        var options = new RetentionOptions();

        options.EventLogRetention.ShouldBe(TimeSpan.FromDays(30));
        options.BatchSize.ShouldBe(1000);
        options.Interval.ShouldBe(TimeSpan.FromHours(1));
    }

    private RetentionRunner Runner() => new(_store, new FixedTimeProvider(_now), new RetentionOptions());

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
