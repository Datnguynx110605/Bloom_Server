using System.Net;
using System.Text;
using System.Text.Json;
using Bloom.Fulfillment.Shippo;

namespace Bloom.Fulfillment.Tests;

/// <summary>Shippo's five endpoints of plan R13 served from memory, behind the real <see cref="ShippoClient"/> (no test reaches the internet). Tracking numbers are <c>TRK&lt;n&gt;</c>, label URLs <c>https://labels.test/&lt;n&gt;.pdf</c>.</summary>
public sealed class FakeShippo : HttpMessageHandler
{
    private const string Standard = "usps_ground_advantage", Express = "usps_priority"; // the tokens of TestFulfillment's config

    private readonly object _gate = new();
    private readonly List<ShippoLabel> _transactions = [];
    private readonly List<string> _tokens = [], _refunds = [], _paths = [];
    private readonly List<JsonElement> _shipmentRequests = [];
    private readonly Dictionary<string, string> _ratesById = [], _tracking = [];
    private int _failNext;

    public IReadOnlyList<ShippoLabel> Transactions { get { lock (_gate) return [.. _transactions]; } }

    /// <summary>Transaction ids refunded (voided), in call order.</summary>
    public IReadOnlyList<string> Refunds { get { lock (_gate) return [.. _refunds]; } }

    /// <summary>The service-level token of each bought transaction, in the order of <see cref="Transactions"/> (labels from <see cref="AddLabel"/> have none).</summary>
    public IReadOnlyList<string> BoughtTokens { get { lock (_gate) return [.. _tokens]; } }

    /// <summary>The bodies of the <c>POST shipments/</c> calls.</summary>
    public IReadOnlyList<JsonElement> ShipmentRequests { get { lock (_gate) return [.. _shipmentRequests]; } }

    /// <summary>The next <c>POST transactions/</c> stores its transaction, then fails with a timeout (F-06).</summary>
    public bool TimeoutAfterStoringNextTransaction { get; set; }

    /// <summary>Shippo offers no rate of the requested service level.</summary>
    public bool NoMatchingRate { get; set; }

    /// <summary>The next <c>POST transactions/</c> answers <c>SUCCESS</c> with an empty tracking number.</summary>
    public bool EmptyTrackingNumberNextTransaction { get; set; }

    /// <summary>Runs inside <c>POST transactions/</c>, before the transaction is stored.</summary>
    public Action? BeforeTransaction { get; set; }

    /// <summary>Runs at the start of every call, before anything else (lets a test move the clock while "Shippo" answers, F-3).</summary>
    public Action? OnRequest { get; set; }

    /// <summary>Calls whose path (without the leading slash) starts with <paramref name="pathPrefix"/>, failed ones included; <c>""</c> counts all.</summary>
    public int Calls(string pathPrefix)
    {
        lock (_gate) return _paths.Count(p => p.StartsWith(pathPrefix, StringComparison.Ordinal));
    }

    /// <summary>The next <paramref name="n"/> calls answer 500.</summary>
    public void FailNext(int n)
    {
        lock (_gate) _failNext = n;
    }

    public void SetTracking(string number, string status)
    {
        lock (_gate) _tracking[number] = status;
    }

    /// <summary>A transaction Shippo already holds (a foreign or duplicate label). Add in creation order: the list answers newest first.</summary>
    public ShippoLabel AddLabel(string metadata, DateTimeOffset? created = null, string? trackingNumber = null)
    {
        lock (_gate) return Store(metadata, null, created, trackingNumber);
    }

    private ShippoLabel Store(string metadata, string? token, DateTimeOffset? created = null, string? trackingNumber = null)
    {
        var n = _transactions.Count + 1; // callers hold _gate
        var label = new ShippoLabel($"tx-{n}", trackingNumber ?? $"TRK{n}", $"https://labels.test/{n}.pdf", metadata, created ?? DateTimeOffset.UtcNow);
        _transactions.Add(label);
        if (token is not null) _tokens.Add(token);
        return label;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        OnRequest?.Invoke();
        var path = request.RequestUri!.AbsolutePath.TrimStart('/');
        JsonElement? body = request.Content is null ? null : JsonDocument.Parse(await request.Content.ReadAsStringAsync(ct)).RootElement;
        lock (_gate)
        {
            _paths.Add(path);
            if (_failNext > 0)
            {
                _failNext--;
                return Json(HttpStatusCode.InternalServerError, new { detail = "scripted failure" });
            }
        }

        if (request.Method == HttpMethod.Post && path == "shipments/") return Ok(NewShipment(body!.Value));
        if (request.Method == HttpMethod.Post && path == "transactions/")
        {
            BeforeTransaction?.Invoke();
            lock (_gate)
            {
                var label = Store(body!.Value.GetProperty("metadata").GetString()!, _ratesById[body.Value.GetProperty("rate").GetString()!]);
                if (EmptyTrackingNumberNextTransaction)
                {
                    EmptyTrackingNumberNextTransaction = false;
                    return Ok(Transaction(label with { TrackingNumber = "" }));
                }
                if (!TimeoutAfterStoringNextTransaction) return Ok(Transaction(label));
                TimeoutAfterStoringNextTransaction = false;
            }
            throw new TaskCanceledException("Scripted timeout after Shippo stored the transaction.");
        }
        if (request.Method == HttpMethod.Get && path == "transactions/") return Ok(ListPage(request.RequestUri!));
        if (request.Method == HttpMethod.Post && path == "refunds/")
        {
            lock (_gate) _refunds.Add(body!.Value.GetProperty("transaction").GetString()!);
            return Ok(new { status = "SUCCESS" });
        }
        if (request.Method == HttpMethod.Get && path.StartsWith("tracks/", StringComparison.Ordinal))
        {
            lock (_gate) return Ok(new { tracking_status = new { status = _tracking.GetValueOrDefault(path.Split('/')[^1], "PRE_TRANSIT") } });
        }
        return Json(HttpStatusCode.NotFound, new { detail = "not found" });
    }

    private object NewShipment(JsonElement body)
    {
        lock (_gate)
        {
            _shipmentRequests.Add(body.Clone());
            var n = _shipmentRequests.Count;
            var tokens = NoMatchingRate ? ["other_service"] : new[] { Standard, Express };
            var rates = tokens.Select(t => new { object_id = $"rate-{n}-{t}", servicelevel = new { token = t } }).ToList();
            foreach (var r in rates) _ratesById[r.object_id] = r.servicelevel.token;
            return new { object_id = $"shp-{n}", rates };
        }
    }

    private static object Transaction(ShippoLabel l) => new
    {
        object_id = l.TransactionId, status = "SUCCESS", tracking_number = l.TrackingNumber, label_url = l.LabelUrl, metadata = l.Metadata, object_created = l.CreatedAt,
    };

    private object ListPage(Uri uri)
    {
        var page = int.Parse(System.Web.HttpUtility.ParseQueryString(uri.Query)["page"] ?? "1");
        lock (_gate)
        {
            var newestFirst = Enumerable.Reverse(_transactions).ToList();
            var results = newestFirst.Skip((page - 1) * 100).Take(100).Select(Transaction).ToList();
            var next = newestFirst.Count > page * 100 ? $"https://api.goshippo.com/transactions/?results=100&page={page + 1}" : null;
            return new { next, results };
        }
    }

    private static HttpResponseMessage Ok(object body) => Json(HttpStatusCode.OK, body);

    private static HttpResponseMessage Json(HttpStatusCode status, object body) =>
        new(status) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
}
