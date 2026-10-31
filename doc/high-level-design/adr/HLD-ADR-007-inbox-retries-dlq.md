# HLD-ADR-007: Deduplicate with an inbox in the consumer transaction; retry 5 times, then dead-letter

| | |
|---|---|
| **Status** | Accepted |
| **Date** | 2026-10-31 |
| **Deciders** | Project owner |
| **Related** | QA-07, HG-3, HLD-ADR-006 |

## Context
At-least-once delivery means duplicates. Consumers also fail, transiently or permanently.

## Decision drivers
- Never apply a message twice.
- Absorb transient failures; isolate poison messages.

## Considered options
- **Inbox:** in the consumer DB, same transaction as the effect · separate dedup step (for example Redis)
- **Retries:** exponential with delay queues · immediate retries

## Decision
Chosen:
- The consumer inserts the **message ID (= outbox UUIDv7) into its inbox table in the same transaction as the business effect**. A duplicate violates the unique key, so it's ACKed and skipped.
- **Retries after ~1 s, 5 s, 30 s, 2 min, 10 min** (delay queues with TTL), then the **dead-letter queue**. Any DLQ message raises an alert. An ops tool can inspect, replay or discard. Replay is safe because of the inbox.

## Pros and cons of the options
### Inbox in the same transaction
- ✅ Atomic: the effect and the dedup record commit together.
- ❌ An inbox table per consumer database.

### Separate dedup step
- ❌ A crash between steps loses or duplicates the effect.

### Immediate retries
- ❌ A 10-second outage burns all attempts.

## Consequences
- ✅ Exactly-once *effect* on top of at-least-once delivery.
- ❌ A replay tool must be built.

## Links
- [hld.md §9.2](../hld.md#92-reliability-patterns)
