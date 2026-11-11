# LLD-ADR-011: DLQ pass lock is transaction-scoped

[Docs](../../README.md) › [Low-level design](../README.md) › [Decision records](README.md)

| | |
|---|---|
| **Status** | Accepted |
| **Date** | 2026-11-12 |
| **Deciders** | Project owner |
| **Related** | LLD-ADR-009, LLD-ADR-010 (superseded), HLD-ADR-004, [00-reliability.md §7.7](../00-reliability.md#77-dlq-admin) |

## Context
LLD-ADR-010 enforces one DLQ operation at a time with a session advisory lock (`pg_try_advisory_lock`) and records that the DLQ admin service must then bypass the PgBouncer transaction-mode pooler (HLD-ADR-004), because session locks don't survive it. Services reach PostgreSQL through that pooler, so a second, direct connection path would exist for this one feature.

## Decision drivers
- Keep the `ABORTED` / `DLQ_BUSY` contract of §7.7 and §8, and the single-operation guarantee of LLD-ADR-010.
- Work through the transaction-mode pooler (HLD-ADR-004), with no special connection path.
- A dead process must not leave the lock held.

## Considered options
1. Keep the session advisory lock on a direct or session-mode connection (LLD-ADR-010)
2. Transaction-scoped advisory lock held by one transaction for the whole pass

## Decision
Chosen: **transaction-scoped advisory lock**. This supersedes LLD-ADR-010.

An operation opens one transaction on the service's database and calls `pg_try_advisory_xact_lock` with the same key as before (derived from the DLQ name). If it returns false, the operation returns `DLQ_BUSY`. The transaction stays open for the whole pass; its `COMMIT` at the end releases the lock. The per-message audit commits stay on their own connections. If the process dies, its connection closes and PostgreSQL releases the lock.

## Pros and cons of the options
### Session advisory lock (LLD-ADR-010)
- ❌ Doesn't survive PgBouncer transaction mode, so the DLQ admin service needs a direct or session-mode connection.

### Transaction-scoped advisory lock
- ✅ Same atomic guarantee as LLD-ADR-010: the first operation proceeds, the second gets `DLQ_BUSY`, and the order is preserved.
- ✅ Works through the transaction-mode pooler, since the lock lives as long as one transaction, which the pooler pins to one server connection.
- ❌ Holds one open transaction for the length of a pass.

## Consequences
- ✅ `DLQ_BUSY` works on quorum queues and through the pooler, and T-23 still passes.
- ✅ The DLQ admin service uses the service's normal connection string.
- ❌ A DLQ operation needs the service's database to be up, which the audit step already requires.

## Links
- [LLD-ADR-010](LLD-ADR-010-dlq-busy-advisory-lock.md)
- [00-reliability.md §7.7](../00-reliability.md#77-dlq-admin)
