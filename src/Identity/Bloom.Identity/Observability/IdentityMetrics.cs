using System.Diagnostics.Metrics;

namespace Bloom.Identity.Observability;

/// <summary>Instruments of 06-identity §10; labels per plan I28.</summary>
public sealed class IdentityMetrics
{
    public const string MeterName = "Bloom.Identity";

    private readonly Counter<long> _signIns, _refreshes, _revocations, _codesSent, _codeFailures, _deletions, _auditEntries;
    private readonly Histogram<double> _signInSeconds;

    public IdentityMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);
        _signIns = meter.CreateCounter<long>("signins_total");
        _signInSeconds = meter.CreateHistogram<double>("signin_seconds", "s");
        _refreshes = meter.CreateCounter<long>("refresh_total");
        _revocations = meter.CreateCounter<long>("revocations_total");
        _codesSent = meter.CreateCounter<long>("codes_sent_total");
        _codeFailures = meter.CreateCounter<long>("code_failures_total");
        _deletions = meter.CreateCounter<long>("account_deletions_total");
        _auditEntries = meter.CreateCounter<long>("audit_entries_total");
    }

    /// <param name="type"><c>customer</c>, <c>staff</c> or <c>unknown</c>.</param>
    /// <param name="method"><c>password</c>, <c>google</c> or <c>mfa</c>.</param>
    /// <param name="result"><c>success</c>, <c>mfa_required</c>, <c>invalid</c> or <c>not_allowed</c>.</param>
    public void SignIn(string type, string method, string result) =>
        _signIns.Add(1, new("type", type), new("method", method), new("result", result));

    public void SignInDuration(double seconds) => _signInSeconds.Record(seconds);

    /// <param name="result"><c>rotated</c>, <c>reuse</c> or <c>invalid</c>.</param>
    public void Refresh(string result) => _refreshes.Add(1, new KeyValuePair<string, object?>("result", result));

    public void Revocation(string trigger) => _revocations.Add(1, new KeyValuePair<string, object?>("trigger", trigger));

    public void CodeSent(string purpose) => _codesSent.Add(1, new KeyValuePair<string, object?>("purpose", purpose));

    public void CodeFailure(string purpose) => _codeFailures.Add(1, new KeyValuePair<string, object?>("purpose", purpose));

    /// <param name="result"><c>deleted</c>, <c>unsettled_orders</c> or <c>order_unavailable</c>.</param>
    public void AccountDeletion(string result) => _deletions.Add(1, new KeyValuePair<string, object?>("result", result));

    public void AuditEntry(string service) => _auditEntries.Add(1, new KeyValuePair<string, object?>("service", service));
}
