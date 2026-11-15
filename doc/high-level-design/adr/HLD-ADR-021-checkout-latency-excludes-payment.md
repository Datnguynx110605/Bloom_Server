# HLD-ADR-021: Checkout start latency excludes the payment call

[Docs](../../README.md) › [High-level design](../high-level-design.md) › [Decision records](README.md)

| | |
|---|---|
| **Status** | Accepted |
| **Date** | 2026-11-14 |
| **Deciders** | Project owner |
| **Related** | QA-03, OR-01, HLD-ADR-013, HLD-ADR-018 |

## Context
QA-03 sets checkout start at p95 < 300 ms at launch peak, and OR-01 counts the catalog, launch/inventory and payment calls in it. The payment call creates a Stripe Checkout Session synchronously (HLD-ADR-013, [§8.1](../high-level-design.md#81-launch-queue--checkout)): a round trip to an external provider whose latency Bloom neither controls nor scales. Stripe's latency alone can use most of the budget, so the target as written can't be met or acted on.

## Decision drivers
- The target measures what Bloom controls and what the launch spike loads: prices, holds and order's own transactions.
- The customer still gets the Stripe Checkout URL in the checkout-start response (HLD-ADR-013).
- No extra round trip or polling step for the client (LLD-ADR-001).

## Considered options
1. Keep QA-03 end to end, the payment call included
2. Measure QA-03 without the payment call
3. Move Checkout Session creation off the response path (answer with the reservation first; the client fetches the URL later)

## Decision
Chosen: **measure QA-03 without the payment call**. This amends HLD-ADR-013's latency consequence.

Checkout start latency (QA-03, OR-01, `checkout_start_seconds`) is the time `order` spends answering *start checkout*, minus the time spent in the `CreatePayment` call to `payment`. The flow does not change: the payment is still created synchronously and the response still carries the Checkout URL.

## Pros and cons of the options
### Keep end to end
- ✅ Measures what the customer waits for.
- ❌ Stripe's latency is inside the budget; the target can't be met, and an alert on it can't be acted on.

### Without the payment call
- ✅ Measures the contended path Bloom scales; an alert on it is actionable.
- ✅ No change to the API, the gateway or the client.
- ❌ The customer waits longer than the metric shows, by the payment call's time.

### Off the response path
- ✅ "Reserved" reaches the customer sooner.
- ❌ The customer still needs the URL to pay, so they reach Stripe no sooner.
- ❌ Adds an endpoint or a polling step to the gateway and the client.

## Consequences
- ✅ QA-03 is reachable and covers catalog, launch, inventory and order.
- ❌ No target covers the full time the customer waits for the Checkout URL.
- ⚠️ The payment call's own duration stays visible in the gRPC client spans (HLD-ADR-015).

## Links
- [HLD-ADR-013](HLD-ADR-013-checkout-orchestration.md)
- [high-level-design.md §3.2](../high-level-design.md#32-quality-attributes)
- [03-order.md §2, §10](../../low-level-design/03-order.md)
