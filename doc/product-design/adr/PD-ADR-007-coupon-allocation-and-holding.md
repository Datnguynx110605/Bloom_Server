# PD-ADR-007: Split coupon discounts per line and hold coupon uses at checkout

| | |
|---|---|
| **Status** | Accepted |
| **Date** | 2026-10-31 |
| **Deciders** | Project owner |
| **Related** | PRM-R04–R10, CHK-R15, RET-R06, RET-R10, BI-04, BI-05 |

## Context
Coupons have limited uses and can be redeemed by many customers at once. Orders can be partly returned, so Bloom must know what each line actually cost after the discount. Both rules affect checkout, refunds and concurrency.

## Decision drivers
- Usage limits never exceeded (BI-04).
- Refunds never exceed what was paid, and are easy to audit (BI-05).
- Behavior customers find fair.

## Considered options
**Partial refunds with a coupon**
1. Split the discount per line at checkout and refund the line's paid amount
2. Re-check the coupon rule on return and reduce the refund if the minimum is no longer met
3. Refund the full line price, ignoring the discount

**When a use counts**
1. Held at checkout, restored on cancel
2. Held at checkout, never restored
3. Counted only on payment

## Decision
Chosen: **split the discount across eligible lines in proportion to price at checkout start, storing each line's paid amount**, and **hold the coupon use at checkout start** (consumed on payment, released on expiry, restored if cancelled before shipping or if delivery fails, not restored after a return).

## Pros and cons of the options
### Split per line
- ✅ Correct, standard, auditable. Refund = what the line cost.
- ❌ Needs a rounding rule for cents.

### Re-check the rule on return
- ✅ Prevents gaming the minimum spend.
- ❌ Complex and confusing for customers.

### Full line price back
- ❌ Bloom loses money. Easy to abuse.

### Held at checkout, restored on cancel
- ✅ Limits can't be exceeded. Customers keep the coupon if they cancel.
- ❌ Uses must expire together with stock reservations.

### Held, never restored
- ❌ Customers lose the coupon on cancellation.

### Counted only on payment
- ❌ Limited coupons can be over-redeemed, or rejected after the customer already paid.

## Consequences
- ✅ Each order line carries its own paid amount, which drives every refund.
- ❌ Coupon uses share the reservation lifecycle, which adds coupling.
- ⚠️ The cent-rounding rule for the split is left to the LLD (OQ-08-3).

## Links
- [08-promotions](../08-promotions.md), [05-checkout-payment](../05-checkout-payment.md), [07-returns-refunds](../07-returns-refunds.md)
