# PD-ADR-006: Track serial units per product flag; never resell returned units

| | |
|---|---|
| **Status** | Accepted |
| **Date** | 2026-10-31 |
| **Deciders** | Project owner |
| **Related** | CAT-R05, INV-R08–R16, WAR-R02, RET-R09, BI-07 |

## Context
Apple devices have serial numbers (and IMEI for cellular models), which are needed for warranty coverage and returns. Accessories such as cables usually aren't worth tracking per unit. A returned device can be resold, held, or sold as open-box. Each choice changes the inventory model.

## Decision drivers
- A realistic inventory model for high-value devices.
- Warranty coverage linked to a specific unit.
- Keep "new" stock trustworthy and the model simple.

## Considered options
**Which products are serialized**
1. Per-product flag set by staff
2. Everything serialized

**Returned units**
1. Held as non-sellable "Returned", never resold
2. Back to sellable new stock
3. Resold as open-box

## Decision
Chosen: **a per-product serialized flag** (serial units assigned to order lines when scanned at packing), and **returned serialized units are held and never resold**.

## Pros and cons of the options
### Per-product flag
- ✅ Matches real warehouses: devices tracked by serial, accessories by quantity.
- ❌ Two inventory paths to build and test.

### Everything serialized
- ✅ One path.
- ❌ Unrealistic and adds friction for accessories.

### Held, not resold
- ✅ "New" stock stays honest. No condition grades.
- ❌ Returned value isn't recovered in the system.

### Back to new stock
- ❌ Selling opened electronics as new is questionable.

### Open-box resale
- ✅ Recovers value.
- ❌ Needs condition grades and separate listings. More scope.

## Consequences
- ✅ Full traceability per device: receipt → order line → shipment → return.
- ❌ Two stock models (quantity and serial) side by side.
- ⚠️ The "never resold" rule also covers returned non-serialized items, held as a *Returned* quantity (INV-R16).

## Links
- [03-inventory](../03-inventory.md), [07-returns-refunds](../07-returns-refunds.md)
