# PD-ADR-009: No price-change flag in the cart

[Docs](../../README.md) › [Product design](../00-overview.md) › [Decision records](README.md)

| | |
|---|---|
| **Status** | Accepted |
| **Date** | 2026-11-12 |
| **Deciders** | Project owner |
| **Related** | CHK-R02, CHK-R04, CHK-R18 |

## Context
CHK-R18 said the cart flags items that "have changed price". The cart stores no price (CHK-R02: it holds no stock and shows current prices), so a "changed" flag has no reference price to compare with.

## Decision drivers
- A flag must be computable from what the cart stores.
- The binding price check already exists at checkout start (CHK-R04).

## Considered options
1. Keep the flag and store a price in each cart line
2. Drop the flag; show the current price

## Decision
Chosen: **drop the price-change flag**. The cart shows each line at the current price and flags only out-of-stock items, both informational. The binding check stays at checkout start (CHK-R04), where checkout uses the current price.

## Pros and cons of the options
### Keep the flag
- ✅ The customer is told when a price moved.
- ❌ The cart must store a price per line and keep it in sync, for an informational hint.

### Drop the flag
- ✅ Nothing new to store; the cart stays as CHK-R02 describes it.
- ❌ A customer is not told that a price changed since they added the item.

## Consequences
- ✅ CHK-R18 and the cart response stay consistent with what the cart stores.
- ❌ No "price changed" hint in the cart.
- ⚠️ Downstream: 03-order §5.1 `GetCart` drops `price_changed`, and §8 "Cart item price changed since added" becomes "Shown at the current price; checkout uses it".

## Links
- [05-checkout-payment](../05-checkout-payment.md)
