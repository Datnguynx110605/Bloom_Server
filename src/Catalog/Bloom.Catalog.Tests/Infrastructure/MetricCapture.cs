using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;

namespace Bloom.Catalog.Tests;

/// <summary>Records one host's measurements: only instruments whose meter was created by that host's <see cref="IMeterFactory"/>.</summary>
public sealed class MetricCapture : IDisposable
{
    private readonly MeterListener _listener = new();
    private readonly List<(string Name, Dictionary<string, object?> Tags, double Value)> _all = [];

    public MetricCapture(IServiceProvider services)
    {
        var factory = services.GetRequiredService<IMeterFactory>();
        _listener.InstrumentPublished = (i, l) => { if (ReferenceEquals(i.Meter.Scope, factory)) l.EnableMeasurementEvents(i); };
        _listener.SetMeasurementEventCallback<long>((i, v, tags, _) => Add(i, v, tags));
        _listener.SetMeasurementEventCallback<double>((i, v, tags, _) => Add(i, v, tags));
        _listener.Start();

        void Add(Instrument i, double v, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            var d = tags.ToArray().ToDictionary(t => t.Key, t => t.Value);
            lock (_all) _all.Add((i.Name, d, v));
        }
    }

    /// <summary>Sum of the counter's measurements carrying all <paramref name="tags"/>.</summary>
    public long Sum(string name, params (string Tag, string Value)[] tags)
    {
        lock (_all) return (long)Matching(name, tags).Sum(m => m.Value);
    }

    /// <summary>Number of measurements recorded on a histogram carrying all <paramref name="tags"/>.</summary>
    public int Count(string name, params (string Tag, string Value)[] tags)
    {
        lock (_all) return Matching(name, tags).Count();
    }

    /// <summary>The newest value of an observable gauge carrying all <paramref name="tags"/>; polls the gauges first.</summary>
    public double Latest(string name, params (string Tag, string Value)[] tags)
    {
        _listener.RecordObservableInstruments();
        lock (_all) return Matching(name, tags).Last().Value;
    }

    public void Dispose() => _listener.Dispose();

    private IEnumerable<(string Name, Dictionary<string, object?> Tags, double Value)> Matching(string name, (string Tag, string Value)[] tags) =>
        _all.Where(m => m.Name == name && tags.All(t => Equals(m.Tags.GetValueOrDefault(t.Tag), t.Value)));
}
