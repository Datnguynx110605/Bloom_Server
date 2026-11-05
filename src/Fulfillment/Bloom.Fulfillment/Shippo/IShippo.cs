namespace Bloom.Fulfillment.Shippo;

/// <summary>Shippo behind an interface (HLD-ADR-018). Every failure (transport, timeout, non-2xx, malformed answer) is a <see cref="ShippoException"/>.</summary>
public interface IShippo
{
    /// <summary>Plan R13: creates the shipment, picks the rate of <see cref="LabelRequest.ServiceLevelToken"/>, buys it.</summary>
    Task<ShippoLabel> BuyLabelAsync(LabelRequest request, CancellationToken ct);

    /// <summary>Successful transactions with <c>bloom:</c> metadata created at or after <paramref name="since"/>, newest first.</summary>
    Task<IReadOnlyList<ShippoLabel>> ListLabelsAsync(DateTimeOffset since, CancellationToken ct);

    Task VoidLabelAsync(string transactionId, CancellationToken ct);

    /// <summary>Shippo's <c>tracking_status.status</c> for the number (FR-04: statuses come only from here).</summary>
    Task<string> GetTrackingStatusAsync(string trackingNumber, CancellationToken ct);
}

public sealed record ShippoAddress(string Name, string Street1, string? Street2, string City, string State, string Zip, string Phone);

public sealed record LabelRequest(Guid ShipmentId, ShippoAddress From, ShippoAddress To, string ServiceLevelToken, bool IsReturn);

/// <param name="Metadata"><c>bloom:&lt;shipment_id&gt;</c>.</param>
public sealed record ShippoLabel(string TransactionId, string TrackingNumber, string LabelUrl, string Metadata, DateTimeOffset CreatedAt);

public sealed class ShippoException(string message, Exception? inner = null) : Exception(message, inner);
