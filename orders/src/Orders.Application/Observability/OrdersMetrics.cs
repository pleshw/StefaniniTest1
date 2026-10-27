using System.Diagnostics.Metrics;
using Orders.Domain;

namespace Orders.Application.Observability;

/// <summary>What the database says about the orders waiting for a person: how many, and since when the oldest one has.</summary>
public sealed record OnHoldSnapshot(long Count, DateTimeOffset? OldestSince);

/// <summary>The custom metrics of the Orders services. One <see cref="Meter"/> named <see cref="MeterName"/>; the services add that name to OpenTelemetry.</summary>
/// <remarks>
/// Counters and the histogram are written where the event happens. The three gauges are read from the last polled
/// value (<see cref="OperationalMetricsRunner"/> runs in the Worker): when a reading is missing, the gauge reports
/// nothing instead of a stale or made-up number.
/// </remarks>
public sealed class OrdersMetrics : IDisposable
{
    public const string MeterName = "Orders";

    private const long _unknown = -1;

    private readonly TimeProvider _time;
    private readonly Counter<long> _created;
    private readonly Counter<long> _duplicateReplayed;
    private readonly Counter<long> _discountApplied;
    private readonly Histogram<double> _priceLookupDuration;

    private OnHoldSnapshot? _onHold;
    private long _deadLetterDepth = _unknown;

    public OrdersMetrics(TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
        Meter = new Meter(MeterName);
        _created = Meter.CreateCounter<long>("orders.created", "{order}", "Orders placed (not replays), by channel and currency.");
        _duplicateReplayed = Meter.CreateCounter<long>(
            "orders.duplicate_replayed", "{request}", "Create-order requests answered from a stored idempotent response, by channel.");
        _discountApplied = Meter.CreateCounter<long>("discount.applied", "{discount}", "Discounts applied to placed orders, by currency.");
        _priceLookupDuration = Meter.CreateHistogram<double>(
            "catalog.price_lookup.duration", "ms", "Duration of the batch price query to Catalog, by outcome (ok or error).");
        Meter.CreateObservableGauge<long>("orders.on_hold", ObserveOnHold, "{order}", "Orders in OnHold.");
        Meter.CreateObservableGauge<double>(
            "orders.on_hold.oldest_age", ObserveOldestHold, "s", "Seconds the longest-waiting OnHold order has been waiting.");
        Meter.CreateObservableGauge<long>("rabbitmq.dlq.depth", ObserveDeadLetterDepth, "{message}", "Messages in the dead letter queue orders.dlq.");
    }

    public Meter Meter { get; }

    public void OrderCreated(OrderChannel channel, Currency currency) =>
        _created.Add(1, new KeyValuePair<string, object?>("channel", ChannelTag(channel)), new KeyValuePair<string, object?>("currency", currency.Code));

    public void DuplicateReplayed(OrderChannel channel) =>
        _duplicateReplayed.Add(1, new KeyValuePair<string, object?>("channel", ChannelTag(channel)));

    public void DiscountApplied(Currency currency, bool oncePerClient) =>
        _discountApplied.Add(1, new KeyValuePair<string, object?>("currency", currency.Code), new KeyValuePair<string, object?>("once_per_client", oncePerClient));

    public void PriceLookup(TimeSpan elapsed, bool succeeded) =>
        _priceLookupDuration.Record(elapsed.TotalMilliseconds, new KeyValuePair<string, object?>("outcome", succeeded ? "ok" : "error"));

    /// <summary>Stores the latest reading; null means it could not be read.</summary>
    public void SetOnHold(OnHoldSnapshot? snapshot) => Volatile.Write(ref _onHold, snapshot);

    /// <summary>Stores the latest reading; null means the queue does not exist or could not be read.</summary>
    public void SetDeadLetterDepth(long? depth) => Volatile.Write(ref _deadLetterDepth, depth ?? _unknown);

    public void Dispose() => Meter.Dispose();

    private static string ChannelTag(OrderChannel channel) => channel == OrderChannel.Shop ? "shop" : "admin";

    private IEnumerable<Measurement<long>> ObserveOnHold() =>
        Volatile.Read(ref _onHold) is { } onHold ? [new Measurement<long>(onHold.Count)] : [];

    // The age is worked out when the gauge is read, so it keeps growing between two polls.
    private IEnumerable<Measurement<double>> ObserveOldestHold() =>
        Volatile.Read(ref _onHold) is { OldestSince: { } since }
            ? [new Measurement<double>(Math.Max(0, (_time.GetUtcNow() - since).TotalSeconds))]
            : [];

    private IEnumerable<Measurement<long>> ObserveDeadLetterDepth() =>
        Volatile.Read(ref _deadLetterDepth) is var depth and >= 0 ? [new Measurement<long>(depth)] : [];
}

/// <summary>Reads the OnHold orders from the database (implemented by Infrastructure).</summary>
public interface IOnHoldProbe
{
    Task<OnHoldSnapshot> ReadAsync(CancellationToken cancellationToken);
}

/// <summary>Reads how many messages wait in the dead letter queue; null when the queue does not exist or messaging is off.</summary>
public interface IDeadLetterDepthProbe
{
    Task<long?> ReadAsync(CancellationToken cancellationToken);
}

/// <param name="OnHoldRead">False when the database could not be read; the gauges then report nothing.</param>
/// <param name="DeadLettersRead">False when the broker could not be read; the gauge then reports nothing.</param>
public sealed record OperationalMetricsResult(bool OnHoldRead, bool DeadLettersRead);

/// <summary>
/// One polling round: reads the OnHold orders and the dead letter depth and hands them to the gauges of
/// <see cref="OrdersMetrics"/>. A probe that fails clears its own gauge and leaves the other alone.
/// </summary>
public sealed class OperationalMetricsRunner(IOnHoldProbe onHold, IDeadLetterDepthProbe deadLetters, OrdersMetrics metrics)
{
    public async Task<OperationalMetricsResult> RunAsync(CancellationToken cancellationToken)
    {
        var onHoldRead = await TryReadAsync(onHold.ReadAsync, metrics.SetOnHold, cancellationToken);
        var deadLettersRead = await TryReadAsync(deadLetters.ReadAsync, metrics.SetDeadLetterDepth, cancellationToken);

        return new OperationalMetricsResult(onHoldRead, deadLettersRead);
    }

    private static async Task<bool> TryReadAsync<T>(Func<CancellationToken, Task<T>> read, Action<T?> publish, CancellationToken cancellationToken)
    {
        try
        {
            publish(await read(cancellationToken));

            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            publish(default);

            return false;
        }
    }
}
