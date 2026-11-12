using System.Diagnostics.Metrics;

namespace Bloom.Inventory.Observability;

/// <summary>Instruments of 02-inventory §10.</summary>
public sealed class InventoryMetrics
{
    public const string MeterName = "Bloom.Inventory";

    private readonly Counter<long> _reserve, _adjustments, _ledger;
    private readonly Histogram<double> _reserveSeconds, _backorderWait;
    private long _stuck;
    private IReadOnlyDictionary<Guid, int> _available = new Dictionary<Guid, int>(), _backorders = new Dictionary<Guid, int>();

    public InventoryMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);
        _reserve = meter.CreateCounter<long>("reserve_total");
        _reserveSeconds = meter.CreateHistogram<double>("reserve_seconds", "s");
        _backorderWait = meter.CreateHistogram<double>("backorder_wait_seconds", "s");
        _adjustments = meter.CreateCounter<long>("adjustments_total");
        _ledger = meter.CreateCounter<long>("ledger_entries_total");
        meter.CreateObservableGauge("stuck_reservations", () => Interlocked.Read(ref _stuck));
        meter.CreateObservableGauge("stock_available", () => PerSku(_available));
        meter.CreateObservableGauge("backorders_waiting", () => PerSku(_backorders));
    }

    private static IEnumerable<Measurement<int>> PerSku(IReadOnlyDictionary<Guid, int> values) =>
        values.Select(v => new Measurement<int>(v.Value, new KeyValuePair<string, object?>("sku", v.Key.ToString())));

    /// <param name="result"><c>ok</c> or <c>sold_out</c>.</param>
    public void Reserve(string result, double seconds)
    {
        _reserve.Add(1, new KeyValuePair<string, object?>("result", result));
        _reserveSeconds.Record(seconds);
    }

    /// <summary>Time from payment to allocation.</summary>
    public void BackorderAllocated(double waitSeconds) => _backorderWait.Record(waitSeconds);

    public void Adjustment(string reasonCode) => _adjustments.Add(1, new KeyValuePair<string, object?>("reason_code", reasonCode));

    public void LedgerEntry(string entryType) => _ledger.Add(1, new KeyValuePair<string, object?>("entry_type", entryType));

    public void SetStuck(long count) => Interlocked.Exchange(ref _stuck, count);

    /// <summary>Replaces both gauges' samples (SKUs absent from a dictionary drop out).</summary>
    public void SetStock(IReadOnlyDictionary<Guid, int> available, IReadOnlyDictionary<Guid, int> backorders)
    {
        _available = available;
        _backorders = backorders;
    }
}
