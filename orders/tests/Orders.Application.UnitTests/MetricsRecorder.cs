using System.Collections.Concurrent;
using System.Diagnostics.Metrics;

namespace Orders.Application.UnitTests;

/// <summary>What one instrument reported: its name, the value and the tags of that measurement.</summary>
internal sealed record Recorded(string Instrument, double Value, IReadOnlyDictionary<string, object?> Tags)
{
    public object? Tag(string name) => Tags.GetValueOrDefault(name);
}

/// <summary>
/// Listens to one <see cref="Meter"/> instance (not to every meter of that name, so tests that run in parallel do not
/// see each other's measurements) with the BCL's <see cref="MeterListener"/>.
/// </summary>
internal sealed class MetricsRecorder : IDisposable
{
    private readonly MeterListener _listener = new();
    private readonly ConcurrentQueue<Recorded> _measurements = new();

    public MetricsRecorder(Meter meter)
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (ReferenceEquals(instrument.Meter, meter))
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => Add(instrument, value, tags));
        _listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => Add(instrument, value, tags));
        _listener.Start();
    }

    /// <summary>Asks the observable gauges for their current values, as a metrics reader does on every collection.</summary>
    public void Collect() => _listener.RecordObservableInstruments();

    public IReadOnlyList<Recorded> Of(string instrument) => [.. _measurements.Where(measurement => measurement.Instrument == instrument)];

    public void Dispose() => _listener.Dispose();

    private void Add(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags) =>
        _measurements.Enqueue(new Recorded(instrument.Name, value, tags.ToArray().ToDictionary(tag => tag.Key, tag => tag.Value)));
}
