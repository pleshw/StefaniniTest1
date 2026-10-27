using NSubstitute.ExceptionExtensions;
using Orders.Application.Observability;

namespace Orders.Application.UnitTests;

public sealed class OperationalMetricsRunnerTests : IDisposable
{
    private static readonly DateTimeOffset _now = new(2026, 3, 4, 12, 0, 0, TimeSpan.Zero);

    private readonly IOnHoldProbe _onHold = Substitute.For<IOnHoldProbe>();
    private readonly IDeadLetterDepthProbe _deadLetters = Substitute.For<IDeadLetterDepthProbe>();
    private readonly OrdersMetrics _metrics = new(new FixedTimeProvider(_now));
    private readonly MetricsRecorder _recorder;
    private readonly OperationalMetricsRunner _runner;

    public OperationalMetricsRunnerTests()
    {
        _recorder = new MetricsRecorder(_metrics.Meter);
        _runner = new OperationalMetricsRunner(_onHold, _deadLetters, _metrics);
    }

    public void Dispose()
    {
        _recorder.Dispose();
        _metrics.Dispose();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RunAsync_BothProbesAnswer_PublishesBothReadings()
    {
        _onHold.ReadAsync(Token).Returns(new OnHoldSnapshot(2, _now.AddMinutes(-30)));
        _deadLetters.ReadAsync(Token).Returns(4L);

        var result = await _runner.RunAsync(Token);

        result.ShouldBe(new OperationalMetricsResult(OnHoldRead: true, DeadLettersRead: true));
        _recorder.Collect();
        _recorder.Of("orders.on_hold").ShouldHaveSingleItem().Value.ShouldBe(2);
        _recorder.Of("orders.on_hold.oldest_age").ShouldHaveSingleItem().Value.ShouldBe(1800);
        _recorder.Of("rabbitmq.dlq.depth").ShouldHaveSingleItem().Value.ShouldBe(4);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RunAsync_NoDeadLetterQueue_ReportsNoDepth()
    {
        _onHold.ReadAsync(Token).Returns(new OnHoldSnapshot(0, null));
        _deadLetters.ReadAsync(Token).Returns((long?)null);

        var result = await _runner.RunAsync(Token);

        result.DeadLettersRead.ShouldBeTrue();
        _recorder.Collect();
        _recorder.Of("rabbitmq.dlq.depth").ShouldBeEmpty();
        _recorder.Of("orders.on_hold").ShouldHaveSingleItem().Value.ShouldBe(0);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RunAsync_BrokerFails_KeepsTheOtherReadingAndClearsTheDepth()
    {
        _onHold.ReadAsync(Token).Returns(new OnHoldSnapshot(1, _now.AddHours(-1)));
        _deadLetters.ReadAsync(Token).Returns(4L);
        await _runner.RunAsync(Token);
        _deadLetters.ReadAsync(Token).ThrowsAsync(new InvalidOperationException("broker down"));

        var result = await _runner.RunAsync(Token);

        result.ShouldBe(new OperationalMetricsResult(OnHoldRead: true, DeadLettersRead: false));
        _recorder.Collect();
        _recorder.Of("rabbitmq.dlq.depth").ShouldBeEmpty();
        _recorder.Of("orders.on_hold").ShouldHaveSingleItem().Value.ShouldBe(1);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RunAsync_DatabaseFails_ClearsOnHoldAndStillReadsTheQueue()
    {
        _onHold.ReadAsync(Token).ThrowsAsync(new InvalidOperationException("database down"));
        _deadLetters.ReadAsync(Token).Returns(9L);

        var result = await _runner.RunAsync(Token);

        result.ShouldBe(new OperationalMetricsResult(OnHoldRead: false, DeadLettersRead: true));
        _recorder.Collect();
        _recorder.Of("orders.on_hold").ShouldBeEmpty();
        _recorder.Of("rabbitmq.dlq.depth").ShouldHaveSingleItem().Value.ShouldBe(9);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task RunAsync_Cancelled_Propagates()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        _onHold.ReadAsync(cancellation.Token).ThrowsAsync(new OperationCanceledException(cancellation.Token));

        await Should.ThrowAsync<OperationCanceledException>(() => _runner.RunAsync(cancellation.Token));
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
