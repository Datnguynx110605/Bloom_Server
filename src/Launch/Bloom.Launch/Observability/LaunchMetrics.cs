using System.Collections.Concurrent;
using System.Diagnostics.Metrics;

namespace Bloom.Launch.Observability;

/// <summary>Instruments of 01-launch §10; labels per plan R14 (<c>launch</c> and <c>sku</c> are ids in <c>D</c> format). Gauges report the last value set per label set.</summary>
public sealed class LaunchMetrics
{
    public const string MeterName = "Bloom.Launch";

    private readonly Counter<long> _admissions, _ticks, _holds;
    private readonly Histogram<double> _holdSeconds, _statusSeconds;
    private readonly ConcurrentDictionary<Guid, long> _size = [], _outstanding = [], _stuck = [];
    private readonly ConcurrentDictionary<(Guid Launch, Guid Sku), long> _available = [];

    public LaunchMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);
        _admissions = meter.CreateCounter<long>("admissions_total");
        _ticks = meter.CreateCounter<long>("admission_ticks_total");
        _holds = meter.CreateCounter<long>("holds_total");
        _holdSeconds = meter.CreateHistogram<double>("hold_seconds", "s");
        _statusSeconds = meter.CreateHistogram<double>("waiting_room_status_seconds", "s");
        meter.CreateObservableGauge("waiting_room_size", () => PerLaunch(_size));
        meter.CreateObservableGauge("passes_outstanding", () => PerLaunch(_outstanding));
        meter.CreateObservableGauge("stuck_holds", () => PerLaunch(_stuck));
        meter.CreateObservableGauge("allocation_available", () =>
            _available.Select(kv => new Measurement<long>(kv.Value, Tag("launch", kv.Key.Launch), Tag("sku", kv.Key.Sku))));
    }

    public void SetQueue(Guid launch, long size, long outstanding)
    {
        _size[launch] = size;
        _outstanding[launch] = outstanding;
    }

    public void Admitted(Guid launch, int n) => _admissions.Add(n, Tag("launch", launch));

    /// <param name="result"><c>admitted</c>, <c>skipped_lock</c> or <c>none</c>.</param>
    public void Tick(Guid launch, string result) => _ticks.Add(1, Tag("launch", launch), new("result", result));

    public void SetAvailable(Guid launch, Guid sku, long n) => _available[(launch, sku)] = n;

    /// <param name="result"><c>ok</c>, <c>sold_out</c>, <c>limit_exceeded</c>, <c>pass_invalid</c>, <c>pass_expired</c> or <c>pass_already_used</c>.</param>
    public void Hold(Guid launch, string result) => _holds.Add(1, Tag("launch", launch), new("result", result));

    public void HoldSeconds(double s) => _holdSeconds.Record(s);

    public void StatusSeconds(double s) => _statusSeconds.Record(s);

    public void SetStuck(Guid launch, long n) => _stuck[launch] = n;

    private static IEnumerable<Measurement<long>> PerLaunch(ConcurrentDictionary<Guid, long> values) =>
        values.Select(kv => new Measurement<long>(kv.Value, Tag("launch", kv.Key)));

    private static KeyValuePair<string, object?> Tag(string key, Guid id) => new(key, id.ToString("D"));
}
