# LLD-ADR-001: Two-phase idempotency with resume for operations that call a provider

[Docs](../../README.md) › [Low-level design](../README.md) › [Decision records](README.md)

| | |
|---|---|
| **Status** | Accepted |
| **Date** | 2026-10-31 |
| **Deciders** | Project owner |
| **Related** | Refines HLD-ADR-008; [00-reliability.md §7.4](../00-reliability.md#74-idempotency-guard-lld-adr-001) |

## Context
HLD-ADR-008 claims the idempotency key in the same transaction as the business change. Start checkout calls Stripe. A database transaction can't stay open during a network call without holding locks and connections for seconds. A crash between claiming the key and storing the result would leave the key stuck *in progress*.

## Decision drivers
- RL-03: same key → same final answer, one effect.
- No long-held transactions.
- Recover from crashes without manual cleanup.

## Considered options
1. Two-phase with resume
2. Single transaction; provider call made asynchronously by a worker

**Failure outcomes:** store business errors too · store only successes

## Decision
Chosen: **two-phase with resume**. Claim `in_progress` with `locked_until = now() + 60 s` and commit; call the provider with key `bloom:<operation>:<user_id>:<key>`; store the outcome as `completed` in a second transaction. A stale `in_progress` key is taken over by a conditional update and the call resumed; the provider deduplicates. Operations without a provider call keep the single-transaction form. **Success and deterministic business rejections are stored and replayed; transient failures release the key.**

## Pros and cons of the options
### Two-phase with resume
- ✅ Short transactions; crash-safe through takeover.
- ❌ Concurrent duplicates inside 60 s get `IDEMPOTENCY_IN_PROGRESS` instead of waiting.

### Async provider call
- ✅ One transaction, no stuck keys.
- ❌ Checkout needs a polling step; harder to meet QA-03.

### Store only successes
- ❌ A retried `SOLD_OUT` could later succeed with the same key.

## Consequences
- ✅ BI-05 holds through crashes.
- ⚠️ HLD-ADR-008's "same transaction" applies only to operations without a provider call.

## Links
- [00-reliability.md §7.4](../00-reliability.md)
