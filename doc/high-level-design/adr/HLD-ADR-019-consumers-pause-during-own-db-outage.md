# HLD-ADR-019: Consumers pause during their own database outage

[Docs](../../README.md) › [High-level design](../high-level-design.md) › [Decision records](README.md)

| | |
|---|---|
| **Status** | Accepted |
| **Date** | 2026-11-12 |
| **Deciders** | Project owner |
| **Related** | HLD-ADR-007, HLD-ADR-004, QA-07 |

## Context
HLD-ADR-007 gives every transient failure 5 attempts in total, with retries after about 1 s, 5 s, 30 s and 2 min. A failure to reach the service's own PostgreSQL is transient, but it hits every message at once. A database outage longer than the retry schedule would burn all 5 attempts for every message in flight and dead-letter them, although nothing is wrong with the messages.

## Decision drivers
- A database outage must not dead-letter healthy messages.
- No message is lost or applied twice (HLD-ADR-007).
- Every other failure keeps the 5-attempt rule.

## Considered options
1. Keep the 5-attempt rule for every transient failure
2. Pause consumption while the service's own database is unreachable

## Decision
Chosen: **pause consumption while the service's own database is unreachable**. This amends HLD-ADR-007.

When `BEGIN`, `COMMIT` or the inbox insert fails because the service's own PostgreSQL can't be reached, the consumer host:
- NACKs the delivery with requeue, and does **not** count an attempt;
- stops consuming;
- probes the database every 1 s, and resumes consuming when the probe succeeds.

Every other failure keeps the 5-attempt rule of HLD-ADR-007.

## Pros and cons of the options
### Keep the 5-attempt rule
- ❌ A database outage longer than about 2 minutes dead-letters healthy messages and needs a manual replay.

### Pause during the outage
- ✅ Messages wait in the queue and are processed after recovery, with no DLQ noise.
- ✅ Safe: nothing was committed, and the inbox deduplicates any redelivery (HLD-ADR-007).
- ❌ A consumer host has a pause and probe state to maintain.

## Consequences
- ✅ A database outage no longer dead-letters messages.
- ❌ A poison message that happens to arrive during an outage is only detected after recovery.
- ⚠️ 00-reliability §7.3 step 8 gains this exception.

## Links
- [HLD-ADR-007](HLD-ADR-007-inbox-retries-dlq.md)
- [high-level-design.md §9.2](../high-level-design.md#92-reliability-patterns)
