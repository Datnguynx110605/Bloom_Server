# HLD-ADR-006: Publish every message through a transactional outbox with a polling relay

[Docs](../../README.md) › [High-level design](../high-level-design.md) › [Decision records](README.md)

| | |
|---|---|
| **Status** | Accepted |
| **Date** | 2026-10-31 |
| **Deciders** | Project owner |
| **Related** | QA-07, HG-3 |

## Context
Writing to the database and publishing to the broker are two systems. A crash between them loses a message or publishes one for a change that rolled back. The owner's design specified an outbox table and a relay fetching 100 rows per second.

## Decision drivers
- No lost messages (QA-07).
- Safe with several relay instances.
- Low delay without a fixed throughput ceiling.

## Considered options
- **Relay:** polling loop with `SKIP LOCKED` · fixed 100 rows/s cron · CDC (Debezium)
- **Message ID:** UUIDv7 · service prefix + sequence

## Decision
Chosen:
- Every message is inserted into the producer's **outbox table in the same database transaction** as the business change. Never in a cache. No payload hash.
- A **relay worker** claims pending rows in batches of 100 with `FOR UPDATE SKIP LOCKED`, publishes them, marks them sent, and **loops immediately while rows remain**, sleeping ~200 ms when empty. Sent rows are purged after a retention period (LLD).
- Outbox IDs are **UUIDv7**, globally unique and used as the message ID.

## Pros and cons of the options
### Polling loop
- ✅ No ceiling, sub-second delay, multi-instance safe.
- ❌ Constant light polling.

### Fixed 100/s cron
- ❌ Caps throughput; up to 1 s delay; needs an extra lock.

### CDC
- ✅ Lowest delay.
- ❌ Heavy extra infrastructure.

## Consequences
- ✅ At-least-once delivery.
- ❌ Duplicates are possible, so consumers need the inbox (HLD-ADR-007).

## Links
- [high-level-design.md §9.2](../high-level-design.md#92-reliability-patterns)
