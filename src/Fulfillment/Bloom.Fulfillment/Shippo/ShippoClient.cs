using System.Net.Http.Json;
using System.Text.Json;

namespace Bloom.Fulfillment.Shippo;

/// <summary>Plan R13: <c>HttpClient</c> + <c>System.Text.Json</c> against Shippo's REST API. The base address, key header and 10 s timeout are set where the client is registered.</summary>
public sealed class ShippoClient(HttpClient http, FulfillmentOptions options) : IShippo
{
    public static readonly Uri BaseAddress = new("https://api.goshippo.com/");
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    public const string MetadataPrefix = "bloom:";

    public async Task<ShippoLabel> BuyLabelAsync(LabelRequest request, CancellationToken ct)
    {
        var metadata = MetadataPrefix + request.ShipmentId.ToString("D");
        var parcel = options.Parcel;
        var shipment = new Dictionary<string, object?>
        {
            ["address_from"] = Address(request.From),
            ["address_to"] = Address(request.To),
            ["parcels"] = new[] { new { length = parcel.LengthIn, width = parcel.WidthIn, height = parcel.HeightIn, distance_unit = "in", weight = parcel.WeightOz, mass_unit = "oz" } },
            ["metadata"] = metadata,
            ["async"] = false,
        };
        if (request.IsReturn) shipment["extra"] = new { is_return = true };
        var rate = await CallAsync(HttpMethod.Post, "shipments/", shipment, s =>
            s.GetProperty("rates").EnumerateArray().Where(r => r.GetProperty("servicelevel").GetProperty("token").GetString() == request.ServiceLevelToken)
                .Select(r => r.GetProperty("object_id").GetString()).FirstOrDefault()
            ?? throw new ShippoException($"Shippo has no rate for service level '{request.ServiceLevelToken}'."), ct);

        return await CallAsync(HttpMethod.Post, "transactions/", new { rate, label_file_type = "PDF", metadata, async = false }, t =>
            t.GetProperty("status").GetString() == "SUCCESS"
                ? Label(t)
                : throw new ShippoException($"Shippo transaction ended as '{t.GetProperty("status").GetString()}'."), ct);
    }

    public async Task<IReadOnlyList<ShippoLabel>> ListLabelsAsync(DateTimeOffset since, CancellationToken ct)
    {
        var labels = new List<ShippoLabel>();
        string? next = "transactions/?results=100&page=1";
        while (next is not null)
        {
            next = await CallAsync(HttpMethod.Get, next, null, page =>
            {
                foreach (var t in page.GetProperty("results").EnumerateArray())
                {
                    // Lenient (F-1): a malformed entry that isn't ours is skipped, it must not fail every lookup. The label worker validates the one it matches.
                    if (t.ValueKind != JsonValueKind.Object || !t.TryGetProperty("object_created", out var c) || c.ValueKind != JsonValueKind.String || !c.TryGetDateTimeOffset(out var created)) continue;
                    if (created < since) return null; // newest first: nothing older matters
                    if (Text(t, "status") == "SUCCESS" && Text(t, "metadata").StartsWith(MetadataPrefix, StringComparison.Ordinal))
                        labels.Add(Parse(t, created));
                }
                return page.GetProperty("next").GetString();
            }, ct);
        }
        return labels;
    }

    public Task VoidLabelAsync(string transactionId, CancellationToken ct) =>
        CallAsync(HttpMethod.Post, "refunds/", new { transaction = transactionId, async = false }, _ => 0, ct);

    public Task<ShippoTracking> GetTrackingStatusAsync(string trackingNumber, CancellationToken ct) =>
        CallAsync(HttpMethod.Get, $"tracks/{Uri.EscapeDataString(options.Shippo.Carrier)}/{Uri.EscapeDataString(trackingNumber)}", null, t =>
        {
            var status = t.GetProperty("tracking_status");
            var date = status.TryGetProperty("status_date", out var d) && d.ValueKind == JsonValueKind.String && d.TryGetDateTimeOffset(out var parsed) ? parsed.ToUniversalTime() : (DateTimeOffset?)null; // Npgsql writes only offset 0
            return new ShippoTracking(status.GetProperty("status").GetString()!, date);
        }, ct);

    // Shippo needs a country; Bloom ships inside the US only (addresses carry no country).
    private static object Address(ShippoAddress a) =>
        new { name = a.Name, street1 = a.Street1, street2 = a.Street2 ?? "", city = a.City, state = a.State, zip = a.Zip, country = "US", phone = a.Phone };

    private static string Text(JsonElement t, string name) => t.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";

    // A "SUCCESS" transaction without id, tracking number or label URL is unusable: fail the attempt (retry path) instead of writing nulls.
    private static ShippoLabel Label(JsonElement t) => Parse(t, t.GetProperty("object_created").GetDateTimeOffset()).Validated();

    private static ShippoLabel Parse(JsonElement t, DateTimeOffset created) => new(Text(t, "object_id"), Text(t, "tracking_number"), Text(t, "label_url"), Text(t, "metadata"), created);

    /// <summary>One Shippo call; <paramref name="parse"/> reads the answer. Whatever goes wrong becomes a <see cref="ShippoException"/>.</summary>
    private async Task<T> CallAsync<T>(HttpMethod method, string url, object? body, Func<JsonElement, T> parse, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(method, url);
            if (body is not null) request.Content = JsonContent.Create(body);
            using var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) throw new ShippoException($"Shippo answered {(int)response.StatusCode} to {method} {url}.");
            using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            return parse(json.RootElement);
        }
        catch (Exception e) when (!ct.IsCancellationRequested && e is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new ShippoException($"Shippo call {method} {url} failed: {e.Message}", e);
        }
    }
}
