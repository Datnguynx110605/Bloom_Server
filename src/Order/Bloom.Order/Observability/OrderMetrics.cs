using System.Diagnostics.Metrics;

namespace Bloom.Order.Observability;

/// <summary>Instruments of 03-order §10.</summary>
public sealed class OrderMetrics
{
    public const string MeterName = "Bloom.Order";

    private static readonly HashSet<string> Results =
    [
        "ok", "sold_out", "limit_exceeded", "coupon_not_found", "coupon_inactive", "coupon_not_started", "coupon_expired", "coupon_min_spend",
        "coupon_not_applicable", "coupon_limit_reached", "order_total_too_low",
    ];

    private readonly Histogram<double> _checkoutStart;
    private readonly Counter<long> _checkouts, _expired, _latePayments, _coupons, _cancellations;
    private IReadOnlyDictionary<string, long> _orders = new Dictionary<string, long>(), _running = new Dictionary<string, long>(), _stuck = new Dictionary<string, long>();

    public OrderMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);
        _checkoutStart = meter.CreateHistogram<double>("checkout_start_seconds", "s");
        _checkouts = meter.CreateCounter<long>("checkout_total");
        _expired = meter.CreateCounter<long>("checkout_expired_total");
        _latePayments = meter.CreateCounter<long>("late_payments_total");
        _coupons = meter.CreateCounter<long>("coupon_redemptions_total");
        _cancellations = meter.CreateCounter<long>("cancellations_total");
        meter.CreateObservableGauge("orders_by_status", () => Samples(_orders, "status"));
        meter.CreateObservableGauge("sagas_running", () => Samples(_running, "type"));
        meter.CreateObservableGauge("sagas_stuck", () => Samples(_stuck, "type"));
    }

    private static IEnumerable<Measurement<long>> Samples(IReadOnlyDictionary<string, long> counts, string tag) =>
        counts.Select(c => new Measurement<long>(c.Value, new KeyValuePair<string, object?>(tag, c.Key)));

    /// <param name="kind"><c>regular</c> or <c>launch</c>.</param>
    public void CheckoutStarted(string kind, double seconds) => _checkoutStart.Record(seconds, new KeyValuePair<string, object?>("kind", kind));

    /// <param name="result"><c>ok</c> or the reason of the failure: <c>SOLD_OUT</c>, <c>LIMIT_EXCEEDED</c>, a coupon refusal (<c>COUPON_MIN_SPEND</c> becomes <c>coupon_min_spend</c>), <c>ORDER_TOTAL_TOO_LOW</c>; anything else is <c>failed</c>.</param>
    public void Checkout(string kind, string result)
    {
        var r = result.ToLowerInvariant();
        _checkouts.Add(1, new KeyValuePair<string, object?>("kind", kind), new KeyValuePair<string, object?>("result", Results.Contains(r) ? r : "failed"));
    }

    public void CheckoutExpired(int n) => _expired.Add(n);

    public void LatePayment() => _latePayments.Add(1);

    /// <param name="result"><c>held</c>, <c>limit_reached</c>, <c>consumed</c>, <c>released</c> or <c>restored</c> (plan R13).</param>
    public void Coupon(string result) => _coupons.Add(1, new KeyValuePair<string, object?>("result", result));

    /// <param name="actor"><c>customer</c> or <c>support</c>.</param>
    public void Cancellation(string actor, string result) =>
        _cancellations.Add(1, new KeyValuePair<string, object?>("actor", actor), new KeyValuePair<string, object?>("result", result));

    /// <summary>Replaces the gauges' samples (the caller lists every status and type, zeros included).</summary>
    public void SetGauges(IReadOnlyDictionary<string, long> ordersByStatus, IReadOnlyDictionary<string, long> sagasRunning, IReadOnlyDictionary<string, long> sagasStuck)
    {
        _orders = ordersByStatus;
        _running = sagasRunning;
        _stuck = sagasStuck;
    }
}
