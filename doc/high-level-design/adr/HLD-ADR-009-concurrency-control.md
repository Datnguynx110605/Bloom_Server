# HLD-ADR-009: Atomic conditional updates for counters; optimistic locking for read-modify-write

| | |
|---|---|
| **Status** | Accepted |
| **Date** | 2026-10-31 |
| **Deciders** | Project owner |
| **Related** | BI-01 … BI-04, QA-06 |

## Context
Two buyers can race for the last unit; two staff can edit the same product. Counters and general updates need different tools.

## Decision drivers
- No overselling (BI-01 … BI-04).
- No lost updates.
- Simple, explainable behaviour.

## Considered options
- **Counters:** atomic conditional update in the database · read-then-write in application memory
- **Read-modify-write:** optimistic locking · pessimistic locks
- **On conflict:** always 409 · automatic retry, then 409

## Decision
Chosen:
- **Counters** (stock, allocation, purchase limits, coupon uses) change only by **conditional updates in the database** (for example `... SET held = held + n WHERE sold + held + n <= allocation`). The application checks affected rows: 1 = success, 0 = rejected.
- **Read-modify-write flows** use **optimistic locking**: a row version in the `WHERE` clause, incremented on update; 0 affected rows = conflict.
- **Every conflict returns 409** to the caller. No automatic retry.

## Pros and cons of the options
### Conditional updates
- ✅ Correct under any concurrency; no locks held across requests.
- ❌ Hot rows under extreme contention (mitigated by the admission rate, HLD-ADR-012).

### Always 409
- ✅ Simplest; conflicts are visible.
- ❌ Brief races surface to users (R-07).

## Consequences
- ✅ BI-01 … BI-04 enforced by the database.
- ❌ Clients must handle 409.

## Links
- [hld.md §9.2](../hld.md#92-reliability-patterns)
