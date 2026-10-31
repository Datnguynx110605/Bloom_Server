# LLD-ADR-010: One DLQ operation at a time, enforced by a PostgreSQL advisory lock

[Docs](../../README.md) › [Low-level design](../README.md) › [Decision records](README.md)

| | |
|---|---|
| **Status** | Accepted |
| **Date** | 2026-10-31 |
| **Deciders** | Project owner |
| **Related** | LLD-ADR-002, LLD-ADR-009, [00-reliability.md §7.7](../00-reliability.md#77-dlq-admin) |

## Context
§7.7 used an exclusive consumer so that a second DLQ operation fails with `DLQ_BUSY`. Every DLQ is a quorum queue (LLD-ADR-002). While building the shared library we found that RabbitMQ 4.1 quorum queues accept a second consumer even when the first one subscribed as exclusive, so the exclusive flag never detects a second operation.

## Decision drivers
- Keep the `ABORTED` / `DLQ_BUSY` contract of §7.7 and §8.
- Never process a dead letter twice, and keep the order after a full pass.
- Keep DLQs as quorum queues (LLD-ADR-002).

## Considered options
1. PostgreSQL session advisory lock per DLQ
2. Check the DLQ's consumer count before and after subscribing
3. Make DLQs classic queues

## Decision
Chosen: **advisory lock**. Before it touches the broker, an operation opens a connection to the service's database and calls `pg_try_advisory_lock` with a key derived from the DLQ name. It holds the lock for the whole pass, which also covers the per-message audit commits, and releases it at the end. If the lock is already taken, the operation returns `DLQ_BUSY`. If the process dies, its connection closes and PostgreSQL releases the lock.

## Pros and cons of the options
### Advisory lock
- ✅ Atomic. The first operation always proceeds, the second always gets `DLQ_BUSY`, and the order is preserved.
- ✅ All API instances of a service share the service's database, so the lock covers every instance.
- ❌ Holds one database connection for the length of a pass.

### Consumer count
- ❌ Not atomic. A race can reorder messages, the count may be stale in a cluster, and both operations can back off.

### Classic DLQs
- ❌ Contradicts LLD-ADR-002 (quorum everywhere) and loses replication.

## Consequences
- ✅ `DLQ_BUSY` works on quorum queues, and T-23 passes.
- ❌ A DLQ operation needs the service's database to be up, which the audit step already requires.

## Links
- [00-reliability.md §7.7](../00-reliability.md#77-dlq-admin)
