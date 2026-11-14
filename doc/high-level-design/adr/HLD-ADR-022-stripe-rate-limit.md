# HLD-ADR-022: Stripe rate limit sized to the launch admission rate

[Docs](../../README.md) › [High-level design](../high-level-design.md) › [Decision records](README.md)

| | |
|---|---|
| **Status** | Accepted |
| **Date** | 2026-11-14 |
| **Deciders** | Project owner |
| **Related** | QA-05, HLD-ADR-012, HLD-ADR-013, HLD-ADR-018, R-11 |

## Context
QA-05 sustains 200 checkouts/s at a launch, and every checkout creates one Stripe Checkout Session synchronously (HLD-ADR-013, LLD-ADR-007). Stripe's default API rate limit (about 100 requests/s in live mode, 25 in test mode) is below that. Payment treats a 429 as transient: `CreatePayment` answers `UNAVAILABLE`, the order stays `pending_payment`, and the customer retries within the 15-minute checkout.

## Decision drivers
- Keep QA-05.
- No new code path on the checkout.
- No customer-visible change.

## Considered options
1. Raise the account's Stripe rate limit before a launch, sized to the admission rate
2. Cap the admission rate at the Stripe limit (lower QA-05)
3. Create Checkout Sessions asynchronously

## Decision
Chosen: **raise the account's Stripe rate limit before a launch**. Before a launch, the operator confirms the Stripe account's rate limit covers the launch's configured admission rate (checkouts/s) with margin, and requests an increase from Stripe when it does not. The 429 handling is unchanged.

## Pros and cons of the options
### 1. Raise the Stripe rate limit
- ✅ Keeps QA-05, no code.
- ❌ An operational step, depends on Stripe granting it.

### 2. Cap the admission rate
- ✅ No dependency on Stripe.
- ❌ Lowers QA-05, slower launches.

### 3. Asynchronous Checkout Sessions
- ✅ Decouples Stripe.
- ❌ The client needs a second call or polling (rejected in HLD-ADR-021 / LLD-ADR-001).

## Consequences
- ✅ QA-05 kept with no code change.
- ❌ A launch depends on a checked Stripe limit.
- ⚠️ The launch runbook (not yet written) must include the check; risk R-11 tracks it.

## Links
- [HLD-ADR-013](HLD-ADR-013-checkout-orchestration.md)
- [HLD-ADR-021](HLD-ADR-021-checkout-latency-excludes-payment.md)
- [high-level-design.md §7.3](../high-level-design.md#73-external-integrations)
- [high-level-design.md §11](../high-level-design.md#11-risks)
