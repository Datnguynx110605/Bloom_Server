# LLD-ADR-014: Release tombstone

[Docs](../../README.md) › [Low-level design](../README.md) › [Decision records](README.md)

| | |
|---|---|
| **Status** | Accepted |
| **Date** | 2026-11-12 |
| **Deciders** | Project owner |
| **Related** | IR-05, LLD-ADR-004, [02-inventory.md §6.5](../02-inventory.md#65-release-command-inventoryrelease-reservationv1), [§7](../02-inventory.md#7-concurrency-and-consistency), [03-order.md §5.2](../03-order.md) |

## Context
`order.paid.v1` (`inventory.order-paid`) and the release command `inventory.release-reservation.v1` travel on different queues. A retried `order.paid` (1 s … 2 min) can arrive after the release. Inventory then finds nothing to release, and the late `order.paid` creates backorders for a cancelled order, which block strict FIFO for that SKU (02-inventory §7, as written before this decision: "a fix needs a design change with an ADR"). The same gap exists in a second place: a `ReserveStock` that arrives after the checkout sweep has released the order would leave a reservation nobody releases.

## Decision drivers
- A released order must never hold or create stock claims again.
- Inventory still never releases a reservation on its own (IR-05).
- Additive message change only (LLD-ADR-004).

## Considered options
1. Leave as is (delayed `order.paid` creates backorders for a cancelled order)
2. Tombstone: record released orders, and have the paid handler and `ReserveStock` check it
3. Make the queues ordered (one queue for both messages)

## Decision
Chosen: **tombstone**.

- Every order-level release command inserts `released_orders(order_id)` (`ON CONFLICT DO NOTHING`). A later `order.paid.v1` for a tombstoned order creates no allocations and logs a warning. Tombstones are purged after 30 days.
- `ReserveStock` for an order in `released_orders` reserves nothing and returns the new reason `ORDER_RELEASED` (`FAILED_PRECONDITION`). `order` answers the checkout `CHECKOUT_CLOSED` (03-order §5.2).
- `inventory.release-reservation.v1` gains an optional `reshipId` (additive, stays v1 per LLD-ADR-004). If it is set, inventory releases only the allocations with `ref_id = reshipId`, writes **no** tombstone, and still publishes `reservation-released {orderId}`.

## Pros and cons of the options
### Leave as is
- ✅ No change.
- ❌ Backorders for a cancelled order block FIFO for that SKU until someone notices.

### Tombstone
- ✅ Closes both races (late paid, late reserve) in inventory, which owns the allocations.
- ✅ A reship release can't tombstone the order, because it carries `reshipId`.
- ❌ One more table and a purge job.
- ❌ A tombstone older than 30 days no longer protects; the retry window is minutes.

### One ordered queue
- ✅ No new state.
- ❌ Changes the broker topology (LLD-ADR-002) for every consumer; does not cover a late `ReserveStock`, which is a gRPC call.

## Consequences
- ✅ A late `order.paid` for a released order is a logged no-op; a late `ReserveStock` leaves no reservation.
- ✅ 02-inventory §7 row "Delayed paid after a release" is replaced by the tombstone rule.
- ➖ New table `released_orders` (02-inventory §4.5), new error reason `ORDER_RELEASED` (§5.4), `reshipId` in the release command (§5.3).

## Links
- [02-inventory.md §4.5](../02-inventory.md#45-released_orders)
- [02-inventory.md §6.5](../02-inventory.md#65-release-command-inventoryrelease-reservationv1)
- [03-order.md §5.2](../03-order.md)
- [LLD-ADR-004](LLD-ADR-004-message-format-versioning.md)
