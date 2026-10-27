using Orders.Application.Observability;
using Orders.Domain;

namespace Orders.Application.UnitTests;

public sealed class OrdersMetricsTests : IDisposable
{
    private static readonly DateTimeOffset _now = new(2026, 3, 4, 12, 0, 0, TimeSpan.Zero);

    private readonly OrdersMetrics _metrics = new(new FixedTimeProvider(_now));
    private readonly MetricsRecorder _recorder;

    public OrdersMetricsTests() => _recorder = new MetricsRecorder(_metrics.Meter);

    public void Dispose()
    {
        _recorder.Dispose();
        _metrics.Dispose();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Meter_IsNamedOrders()
    {
        OrdersMetrics.MeterName.ShouldBe("Orders");
        _metrics.Meter.Name.ShouldBe("Orders");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void OrderCreated_Counts_ByChannelAndCurrency()
    {
        _metrics.OrderCreated(OrderChannel.Admin, Currency.Create("USD"));

        var measurement = _recorder.Of("orders.created").ShouldHaveSingleItem();
        measurement.Value.ShouldBe(1);
        measurement.Tag("channel").ShouldBe("admin");
        measurement.Tag("currency").ShouldBe("USD");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void DuplicateReplayed_Counts_ByChannel()
    {
        _metrics.DuplicateReplayed(OrderChannel.Shop);

        var measurement = _recorder.Of("orders.duplicate_replayed").ShouldHaveSingleItem();
        measurement.Value.ShouldBe(1);
        measurement.Tag("channel").ShouldBe("shop");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void DiscountApplied_Counts_ByCurrencyAndOncePerClient()
    {
        _metrics.DiscountApplied(Currency.Create("BRL"), oncePerClient: true);

        var measurement = _recorder.Of("discount.applied").ShouldHaveSingleItem();
        measurement.Value.ShouldBe(1);
        measurement.Tag("currency").ShouldBe("BRL");
        measurement.Tag("once_per_client").ShouldBe(true);
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData(true, "ok")]
    [InlineData(false, "error")]
    public void PriceLookup_RecordsTheDurationInMilliseconds_WithTheOutcome(bool succeeded, string outcome)
    {
        _metrics.PriceLookup(TimeSpan.FromMilliseconds(12.5), succeeded);

        var measurement = _recorder.Of("catalog.price_lookup.duration").ShouldHaveSingleItem();
        measurement.Value.ShouldBe(12.5);
        measurement.Tag("outcome").ShouldBe(outcome);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Gauges_BeforeAnyReading_ReportNothing()
    {
        _recorder.Collect();

        _recorder.Of("orders.on_hold").ShouldBeEmpty();
        _recorder.Of("orders.on_hold.oldest_age").ShouldBeEmpty();
        _recorder.Of("rabbitmq.dlq.depth").ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Gauges_AfterAReading_ReportCountDepthAndTheAgeOfTheOldestHeldOrder()
    {
        _metrics.SetOnHold(new OnHoldSnapshot(3, _now.AddHours(-5)));
        _metrics.SetDeadLetterDepth(7);

        _recorder.Collect();

        _recorder.Of("orders.on_hold").ShouldHaveSingleItem().Value.ShouldBe(3);
        _recorder.Of("orders.on_hold.oldest_age").ShouldHaveSingleItem().Value.ShouldBe(TimeSpan.FromHours(5).TotalSeconds);
        _recorder.Of("rabbitmq.dlq.depth").ShouldHaveSingleItem().Value.ShouldBe(7);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Gauges_NoOrderOnHold_ReportZeroAndNoAge()
    {
        _metrics.SetOnHold(new OnHoldSnapshot(0, null));

        _recorder.Collect();

        _recorder.Of("orders.on_hold").ShouldHaveSingleItem().Value.ShouldBe(0);
        _recorder.Of("orders.on_hold.oldest_age").ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Gauges_AfterTheReadingIsCleared_ReportNothingAgain()
    {
        _metrics.SetOnHold(new OnHoldSnapshot(3, _now.AddHours(-5)));
        _metrics.SetDeadLetterDepth(7);
        _metrics.SetOnHold(null);
        _metrics.SetDeadLetterDepth(null);

        _recorder.Collect();

        _recorder.Of("orders.on_hold").ShouldBeEmpty();
        _recorder.Of("orders.on_hold.oldest_age").ShouldBeEmpty();
        _recorder.Of("rabbitmq.dlq.depth").ShouldBeEmpty();
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
