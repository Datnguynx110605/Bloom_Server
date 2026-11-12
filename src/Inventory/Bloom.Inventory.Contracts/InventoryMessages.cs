namespace Bloom.Inventory.Contracts;

/// <summary>Message types of 02-inventory §5.3 that inventory publishes (<see cref="StockAllocated"/>, <see cref="ReservationReleased"/>) and consumes (<see cref="ReleaseReservation"/>).</summary>
public static class InventoryMessages
{
    public const string StockAllocated = "inventory.stock-allocated.v1";
    public const string ReservationReleased = "inventory.reservation-released.v1";
    public const string ReleaseReservation = "inventory.release-reservation.v1";
}
