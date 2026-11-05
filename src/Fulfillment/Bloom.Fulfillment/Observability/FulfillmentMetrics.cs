using System.Diagnostics.Metrics;

namespace Bloom.Fulfillment.Observability;

/// <summary>Instruments of 05-fulfillment §10.</summary>
public sealed class FulfillmentMetrics
{
    public const string MeterName = "Bloom.Fulfillment";

    private readonly Counter<long> _labels, _refunds, _webhooks;
    private readonly Histogram<double> _handover;
    private long _possiblyLost;
    private IReadOnlyDictionary<string, long> _packTasks = new Dictionary<string, long>(), _returns = new Dictionary<string, long>();

    public FulfillmentMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);
        _handover = meter.CreateHistogram<double>("time_to_handover_seconds", "s");
        _labels = meter.CreateCounter<long>("labels_total");
        _refunds = meter.CreateCounter<long>("refund_amount_cents_total");
        _webhooks = meter.CreateCounter<long>("tracking_webhooks_total");
        meter.CreateObservableGauge("pack_tasks_by_status", () => ByStatus(_packTasks));
        meter.CreateObservableGauge("returns_by_status", () => ByStatus(_returns));
        meter.CreateObservableGauge("possibly_lost_open", () => Interlocked.Read(ref _possiblyLost));
    }

    private static IEnumerable<Measurement<long>> ByStatus(IReadOnlyDictionary<string, long> counts) =>
        counts.Select(c => new Measurement<long>(c.Value, new KeyValuePair<string, object?>("status", c.Key)));

    /// <summary>Time from the pack task's creation (payment) to handover.</summary>
    public void HandedOver(string shippingOption, double seconds) =>
        _handover.Record(seconds, new KeyValuePair<string, object?>("shipping_option", shippingOption));

    /// <param name="result"><c>bought</c>, <c>reused</c>, <c>failed</c> or <c>voided</c>.</param>
    public void Label(string result) => _labels.Add(1, new KeyValuePair<string, object?>("result", result));

    /// <param name="reason"><c>return</c> or <c>delivery_failure</c>.</param>
    public void Refund(string reason, long cents) => _refunds.Add(cents, new KeyValuePair<string, object?>("reason", reason));

    /// <param name="result"><c>applied</c>, <c>unchanged</c>, <c>unknown</c>, <c>bad_secret</c>, <c>invalid</c> or <c>error</c> (plan R16).</param>
    public void Webhook(string result) => _webhooks.Add(1, new KeyValuePair<string, object?>("result", result));

    /// <summary>Replaces the gauges' samples (statuses absent from a dictionary drop out).</summary>
    public void SetGauges(IReadOnlyDictionary<string, long> packTasks, IReadOnlyDictionary<string, long> returns, long possiblyLostOpen)
    {
        _packTasks = packTasks;
        _returns = returns;
        Interlocked.Exchange(ref _possiblyLost, possiblyLostOpen);
    }
}
