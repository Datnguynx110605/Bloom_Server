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
                    if (t.GetProperty("object_created").GetDateTimeOffset() < since) return null; // newest first: nothing older matters
                    if (t.GetProperty("status").GetString() == "SUCCESS" && (t.GetProperty("metadata").GetString() ?? "").StartsWith(MetadataPrefix, StringComparison.Ordinal))
                        labels.Add(Label(t));
                }
                return page.GetProperty("next").GetString();
            }, ct);
        }
        return labels;
    }

    public Task VoidLabelAsync(string transactionId, CancellationToken ct) =>
        CallAsync(HttpMethod.Post, "refunds/", new { transaction = transactionId, async = false }, _ => 0, ct);

    public Task<string> GetTrackingStatusAsync(string trackingNumber, CancellationToken ct) =>
        CallAsync(HttpMethod.Get, $"tracks/{Uri.EscapeDataString(options.Shippo.Carrier)}/{Uri.EscapeDataString(trackingNumber)}", null,
            t => t.GetProperty("tracking_status").GetProperty("status").GetString()!, ct);

    // Shippo needs a country; Bloom ships inside the US only (addresses carry no country).
    private static object Address(ShippoAddress a) =>
        new { name = a.Name, street1 = a.Street1, street2 = a.Street2 ?? "", city = a.City, state = a.State, zip = a.Zip, country = "US", phone = a.Phone };

    private static ShippoLabel Label(JsonElement t) => new(
        t.GetProperty("object_id").GetString()!, t.GetProperty("tracking_number").GetString()!, t.GetProperty("label_url").GetString()!,
        t.GetProperty("metadata").GetString()!, t.GetProperty("object_created").GetDateTimeOffset());

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
