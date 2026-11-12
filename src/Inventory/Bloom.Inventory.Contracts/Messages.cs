namespace Bloom.Inventory.Contracts;

/// <summary>Published when every pre-order allocation of the order is committed (02-inventory §6.4).</summary>
public sealed record StockAllocatedV1(Guid OrderId);

/// <summary>Published after a release command is applied (02-inventory §6.5).</summary>
public sealed record ReservationReleasedV1(Guid OrderId);

/// <summary>Command from order: free everything the order holds. From fulfillment, with <paramref name="ReshipId"/>: free only that reship (02-inventory §6.5).</summary>
public sealed record ReleaseReservationV1(Guid OrderId, Guid? ReshipId = null);
