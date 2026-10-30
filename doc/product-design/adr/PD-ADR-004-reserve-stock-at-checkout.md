# PD-ADR-004: Reserve stock at checkout start, with a 15-minute timeout

| | |
|---|---|
| **Status** | Accepted |
| **Date** | 2026-10-31 |
| **Deciders** | Project owner |
| **Related** | INV-R04–R07, CHK-R04, CHK-R05, CHK-R14, LCH-R15, LCH-R16 |

## Context
Bloom must never sell more units than it has (BI-02) or more than a launch allocation (BI-01). The moment stock is held for a customer decides both correctness and how much stock gets locked by people who never buy. This is the central inventory rule.

## Decision drivers
- No overselling, including under launch-day contention.
- Abandoned carts must not lock stock.
- Customers who reach payment should not lose the item mid-payment.

## Considered options
1. Reserve at checkout start, with a timeout
2. Reserve at add-to-cart
3. Decrement only after payment

## Decision
Chosen: **reserve at checkout start for 15 minutes**. Release on timeout; convert to a sale when payment succeeds. The cart holds no stock.

## Pros and cons of the options
### At checkout, with timeout
- ✅ Industry standard. Stock is held only for customers actively paying.
- ✅ Works for both regular and launch purchases.
- ❌ Customers can find an item unavailable when starting checkout.
- ❌ Needs reliable expiry of reservations.

### At add-to-cart
- ✅ No surprise at checkout.
- ❌ Abandoned carts lock stock. Very bad on launch day.

### Only after payment
- ✅ Nothing to expire.
- ❌ Two customers can pay for the last unit; one must be refunded. Unacceptable for fixed allocations.

## Consequences
- ✅ The "available" number is accurate at the moment of sale.
- ❌ The cart can show items that aren't available at checkout.
- ⚠️ The backend needs a reliable expiry mechanism that releases stock and coupon uses together, and must handle a payment arriving after expiry.

## Links
- [03-inventory](../03-inventory.md), [05-checkout-payment](../05-checkout-payment.md)
