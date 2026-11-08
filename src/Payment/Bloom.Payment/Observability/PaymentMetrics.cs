using System.Diagnostics.Metrics;

namespace Bloom.Payment.Observability;

/// <summary>Instruments of 04-payment §10; labels per plan R14.</summary>
public sealed class PaymentMetrics
{
    public const string MeterName = "Bloom.Payment";

    private readonly Counter<long> _created, _succeeded, _refunds, _webhooks, _fixes;
    private readonly Histogram<double> _webhookLag;
    private long _mismatches, _disputesOpen;

    public PaymentMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);
        _created = meter.CreateCounter<long>("payments_created_total");
        _succeeded = meter.CreateCounter<long>("payments_succeeded_total");
        _refunds = meter.CreateCounter<long>("refunds_total");
        _webhooks = meter.CreateCounter<long>("webhooks_total");
        _webhookLag = meter.CreateHistogram<double>("webhook_lag_seconds", "s");
        _fixes = meter.CreateCounter<long>("reconcile_fixes_total");
        meter.CreateObservableGauge("reconcile_mismatches", () => Interlocked.Read(ref _mismatches));
        meter.CreateObservableGauge("disputes_open", () => Interlocked.Read(ref _disputesOpen));
    }

    /// <param name="result"><c>created</c>, <c>existing</c>, <c>in_progress</c>, <c>different_amount</c> or <c>provider_unavailable</c>.</param>
    public void Created(string result) => _created.Add(1, Tag("result", result));

    public void Succeeded(bool late) => _succeeded.Add(1, Tag("late", late ? "true" : "false"));

    /// <param name="status"><c>submitted</c>, <c>succeeded</c> or <c>failed</c>.</param>
    public void Refund(string status) => _refunds.Add(1, Tag("status", status));

    /// <param name="type">The event's <c>type</c>, <c>unknown</c> before the body is parsed.</param>
    /// <param name="result"><c>processed</c>, <c>duplicate</c>, <c>bad_signature</c> or <c>error</c>.</param>
    public void Webhook(string type, string result) => _webhooks.Add(1, Tag("type", type), Tag("result", result));

    public void WebhookLag(double seconds) => _webhookLag.Record(seconds);

    /// <param name="kind"><c>payment_succeeded</c>, <c>payment_expired</c>, <c>refund_succeeded</c> or <c>refund_failed</c>.</param>
    public void ReconcileFix(string kind) => _fixes.Add(1, Tag("kind", kind));

    public void SetMismatches(long n) => Interlocked.Exchange(ref _mismatches, n);

    public void SetDisputesOpen(long n) => Interlocked.Exchange(ref _disputesOpen, n);

    private static KeyValuePair<string, object?> Tag(string key, string value) => new(key, value);
}
