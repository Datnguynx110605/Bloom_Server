# HLD-ADR-008: Claim idempotency keys in the service DB; four layers against double charges

| | |
|---|---|
| **Status** | Accepted |
| **Date** | 2026-10-31 |
| **Deciders** | Project owner |
| **Related** | BI-05, CHK-R11, QA-09 |

## Context
A double click, a client retry or a provider retry must never charge a customer twice. The owner's design: an idempotency key plus a payload hash. Same key and same hash returns the stored result; same key with a different hash is rejected; a different key is processed. Required for payment operations and provider callbacks.

## Decision drivers
- BI-05: charged at most once.
- Concurrent duplicates must not both run.
- Provider retries and callbacks are deduplicated too.

## Considered options
- **Key store:** service DB in the same transaction · Redis `SETNX`
- **Protection:** four layers · API key + callbacks only

## Decision
Chosen:
- The idempotency key (scoped per user, kept 24 h) and payload hash are **inserted with a unique constraint in the same transaction** as the business change, starting *in progress*. A concurrent duplicate gets **409 in progress**; a later duplicate gets the stored result; same key with a different hash is rejected.
- **Four layers:** (1) API idempotency key on pay; (2) database rule of at most one successful payment per order; (3) Stripe idempotency key on every charge and refund call; (4) webhooks verified by signature, deduplicated by provider event ID, plus a **reconciliation job** for payments pending without a webhook.

## Pros and cons of the options
### DB, same transaction
- ✅ Atomic with the effect, durable.
- ❌ One more table write per request.

### Redis SETNX
- ❌ Not atomic with the database change.

### Two layers only
- ❌ Our retry after a provider timeout can create a second charge.

## Consequences
- ✅ BI-05 holds against client, server and provider retries.
- ❌ A reconciliation job must be built and monitored.
- ⚠️ Refined by [LLD-ADR-001](../../low-level-design/adr/LLD-ADR-001-two-phase-idempotency.md): operations that call a provider claim the key in a first transaction and store the outcome in a second, with stale-key takeover after 60 s.

## Links
- [hld.md §8.2, §9.2](../hld.md)
