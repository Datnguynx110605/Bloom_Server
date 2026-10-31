using System.Diagnostics.Metrics;
using Bloom.Shared.Observability;

namespace Bloom.Shared.Tests;

/// <summary>Records this host's service's measurements (counters, histograms and observable gauges) by instrument name and tags.</summary>
internal sealed class MetricCapture : IDisposable
{
    private readonly MeterListener _listener = new();
    private readonly List<(string Name, Dictionary<string, object?> Tags, double Value)> _all = [];

    public MetricCapture(string service)
    {
        _listener.InstrumentPublished = (i, l) => { if (i.Meter.Name == BloomMetrics.MeterName) l.EnableMeasurementEvents(i); };
        _listener.SetMeasurementEventCallback<long>((i, v, tags, _) => Add(i, v, tags));
        _listener.SetMeasurementEventCallback<double>((i, v, tags, _) => Add(i, v, tags));
        _listener.Start();

        void Add(Instrument i, double v, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            var d = tags.ToArray().ToDictionary(t => t.Key, t => t.Value);
            if (Equals(d.GetValueOrDefault("service"), service))
                lock (_all) _all.Add((i.Name, d, v));
        }
    }

    public long Sum(string name, string tag, string value)
    {
        lock (_all) return (long)_all.Where(m => m.Name == name && Equals(m.Tags.GetValueOrDefault(tag), value)).Sum(m => m.Value);
    }

    /// <summary>Polls the observable gauges once; returns the latest value of <paramref name="name"/> per <c>queue</c> tag ("" when it has none).</summary>
    public Dictionary<string, double> Gauge(string name)
    {
        _listener.RecordObservableInstruments();
        lock (_all)
            return _all.Where(m => m.Name == name)
                .GroupBy(m => (string?)m.Tags.GetValueOrDefault("queue") ?? "")
                .ToDictionary(g => g.Key, g => g.Last().Value);
    }

    public void Dispose() => _listener.Dispose();
}
