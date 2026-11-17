using System.Globalization;

namespace Bloom.LoadTest.Fakes;

/// <summary>
/// The eight Stripe endpoints Bloom.Payment calls, served from memory (ported from Bloom.Payment.Tests' FakeStripe, real clock):
/// sessions create / retrieve / expire, refunds create / retrieve / list, payment_intents list. Anything else answers 404.
/// Ids are <c>cs_test_n</c>, <c>pi_test_n</c>, <c>re_test_n</c>.
/// </summary>
public sealed class StripeFake
{
    public sealed record Session(string Id, string? OrderId, string? PaymentId, long Amount, string Status, string PaymentStatus, string? PaymentIntentId);

    private sealed class SessionRow
    {
        public required string Id { get; init; }
        public string Status { get; set; } = "open";
        public string PaymentStatus { get; set; } = "unpaid";
        public string? PaymentIntentId { get; set; }
        public long Amount { get; init; }
        public DateTimeOffset ExpiresAt { get; init; }
        public string? OrderId { get; init; }
        public string? PaymentId { get; init; }
    }

    private sealed record RefundRow(string Id, long Amount, string PaymentIntentId, string? RefundId, string? OrderId, DateTimeOffset Created);

    private sealed record IntentRow(string Id, long Amount, DateTimeOffset Created);

    private const int PageSize = 100;

    private readonly object _gate = new();
    private readonly List<SessionRow> _sessions = [];
    private readonly List<RefundRow> _refunds = [];
    private readonly List<IntentRow> _intents = [];
    private readonly Dictionary<string, (string Fingerprint, string Id)> _keys = [];
    private int _intentCount, _refundCount;

    public int SessionsCreated { get { lock (_gate) return _sessions.Count; } }

    /// <summary>The customer pays: the session is <c>complete</c> / <c>paid</c> and a succeeded PaymentIntent of the session amount exists. Null when the session is no longer open.</summary>
    public Session? Pay(string sessionId)
    {
        lock (_gate)
        {
            var s = _sessions.First(x => x.Id == sessionId);
            if (s.Status != "open") return null;
            s.Status = "complete";
            s.PaymentStatus = "paid";
            s.PaymentIntentId = $"pi_test_{++_intentCount}";
            _intents.Add(new(s.PaymentIntentId, s.Amount, DateTimeOffset.UtcNow));
            return ToSession(s);
        }
    }

    /// <summary>Serves one request. <paramref name="created"/> is set when it made a new session (not for an idempotent replay).</summary>
    public (int Status, object Body) Handle(string method, string path, IReadOnlyList<KeyValuePair<string, string>> form, string? idempotencyKey, out string? created)
    {
        created = null;
        var parts = path.Trim('/').Split('/');
        var f = form.GroupBy(p => p.Key).ToDictionary(g => g.Key, g => g.Last().Value);
        lock (_gate)
        {
            switch (method, parts)
            {
                case ("POST", ["v1", "checkout", "sessions"]):
                    var r = CreateSession(f, idempotencyKey, form, out var isNew);
                    if (isNew) created = _sessions[^1].Id;
                    return r;
                case ("GET", ["v1", "checkout", "sessions", var id]):
                    return _sessions.FirstOrDefault(s => s.Id == id) is { } row ? Ok(SessionJson(row)) : Missing();
                case ("POST", ["v1", "checkout", "sessions", var id, "expire"]):
                    if (_sessions.FirstOrDefault(s => s.Id == id) is not { } toExpire) return Missing();
                    if (toExpire.Status != "open") return Error(400, "invalid_request_error", "checkout_session_not_open");
                    toExpire.Status = "expired";
                    return Ok(SessionJson(toExpire));
                case ("POST", ["v1", "refunds"]):
                    return CreateRefund(f, idempotencyKey, form);
                case ("GET", ["v1", "refunds", var id]):
                    return _refunds.FirstOrDefault(x => x.Id == id) is { } refund ? Ok(RefundJson(refund)) : Missing();
                case ("GET", ["v1", "refunds"]):
                    return List(f, _refunds.Select(x => (x.Id, x.Created, (object)RefundJson(x))));
                case ("GET", ["v1", "payment_intents"]):
                    return List(f, _intents.Select(x => (x.Id, x.Created, (object)IntentJson(x))));
            }
        }
        return Error(404, "invalid_request_error", "fake_stripe_unserved");
    }

    // callers hold _gate
    private (int, object) CreateSession(Dictionary<string, string> f, string? key, IReadOnlyList<KeyValuePair<string, string>> form, out bool isNew)
    {
        isNew = false;
        if (Replay(key, form) is { } replay) return replay;
        if (!f.TryGetValue("expires_at", out var e) || !long.TryParse(e, out var expires) || expires < DateTimeOffset.UtcNow.AddMinutes(30).ToUnixTimeSeconds())
            return Error(400, "invalid_request_error", "parameter_invalid_integer");
        long amount = 0;
        for (var i = 0; f.TryGetValue($"line_items[{i}][price_data][unit_amount]", out var unit); i++)
            amount += long.Parse(unit, CultureInfo.InvariantCulture) * long.Parse(f.GetValueOrDefault($"line_items[{i}][quantity]", "1"), CultureInfo.InvariantCulture);
        var s = new SessionRow
        {
            Id = $"cs_test_{_sessions.Count + 1}", Amount = amount, ExpiresAt = DateTimeOffset.FromUnixTimeSeconds(expires),
            OrderId = f.GetValueOrDefault("metadata[order_id]"), PaymentId = f.GetValueOrDefault("metadata[payment_id]"),
        };
        _sessions.Add(s);
        Remember(key, form, s.Id);
        isNew = true;
        return Ok(SessionJson(s));
    }

    private (int, object) CreateRefund(Dictionary<string, string> f, string? key, IReadOnlyList<KeyValuePair<string, string>> form)
    {
        if (Replay(key, form) is { } replay) return replay;
        var refund = new RefundRow($"re_test_{++_refundCount}", long.Parse(f["amount"], CultureInfo.InvariantCulture), f["payment_intent"],
            f.GetValueOrDefault("metadata[refund_id]"), f.GetValueOrDefault("metadata[order_id]"), DateTimeOffset.UtcNow);
        _refunds.Add(refund);
        Remember(key, form, refund.Id);
        return Ok(RefundJson(refund));
    }

    // Same key and same body: the stored object. Same key and another body: idempotency_error.
    private (int, object)? Replay(string? key, IReadOnlyList<KeyValuePair<string, string>> form)
    {
        if (key is null || !_keys.TryGetValue(key, out var stored)) return null;
        if (stored.Fingerprint != Fingerprint(form)) return Error(400, "idempotency_error", null);
        return stored.Id.StartsWith("cs_", StringComparison.Ordinal) ? Ok(SessionJson(_sessions.First(s => s.Id == stored.Id))) : Ok(RefundJson(_refunds.First(x => x.Id == stored.Id)));
    }

    private void Remember(string? key, IReadOnlyList<KeyValuePair<string, string>> form, string id)
    {
        if (key is not null) _keys[key] = (Fingerprint(form), id);
    }

    private static string Fingerprint(IReadOnlyList<KeyValuePair<string, string>> form) => string.Join('&', form.Select(p => $"{p.Key}={p.Value}"));

    // Newest first, like Stripe; the filter is created[gte] <= created < created[lt] in whole seconds.
    private static (int, object) List(Dictionary<string, string> f, IEnumerable<(string Id, DateTimeOffset Created, object Json)> items)
    {
        var gte = long.Parse(f.GetValueOrDefault("created[gte]", long.MinValue.ToString()));
        var lt = long.Parse(f.GetValueOrDefault("created[lt]", long.MaxValue.ToString()));
        var ordered = items.Reverse().Where(i => i.Created.ToUnixTimeSeconds() >= gte && i.Created.ToUnixTimeSeconds() < lt).OrderByDescending(i => i.Created.ToUnixTimeSeconds()).ToList();
        var start = f.TryGetValue("starting_after", out var after) ? ordered.FindIndex(i => i.Id == after) + 1 : 0;
        var page = ordered.Skip(start).Take(PageSize).ToList();
        return Ok(new { @object = "list", data = page.Select(i => i.Json), has_more = start + page.Count < ordered.Count });
    }

    private static object SessionJson(SessionRow s) => new
    {
        id = s.Id, @object = "checkout.session", url = $"https://checkout.stripe.test/{s.Id}", status = s.Status, payment_status = s.PaymentStatus,
        payment_intent = s.PaymentIntentId, amount_total = s.Amount, expires_at = s.ExpiresAt.ToUnixTimeSeconds(),
        metadata = new { order_id = s.OrderId, payment_id = s.PaymentId },
    };

    private static object RefundJson(RefundRow r) => new
    {
        id = r.Id, @object = "refund", status = "succeeded", amount = r.Amount, failure_reason = (string?)null, payment_intent = r.PaymentIntentId,
        created = r.Created.ToUnixTimeSeconds(),
        metadata = new Dictionary<string, string>(new[] { ("refund_id", r.RefundId), ("order_id", r.OrderId) }.Where(p => p.Item2 is not null).Select(p => KeyValuePair.Create(p.Item1, p.Item2!))),
    };

    private static object IntentJson(IntentRow p) => new { id = p.Id, @object = "payment_intent", status = "succeeded", amount = p.Amount, created = p.Created.ToUnixTimeSeconds() };

    private static Session ToSession(SessionRow s) => new(s.Id, s.OrderId, s.PaymentId, s.Amount, s.Status, s.PaymentStatus, s.PaymentIntentId);

    private static (int, object) Ok(object body) => (200, body);

    private static (int, object) Missing() => Error(404, "invalid_request_error", "resource_missing");

    private static (int, object) Error(int status, string type, string? code) =>
        (status, new { error = code is null ? new Dictionary<string, string> { ["type"] = type } : new Dictionary<string, string> { ["type"] = type, ["code"] = code } });
}
