# LLD-ADR-005: Optimistic locking uses an explicit version column

| | |
|---|---|
| **Status** | Accepted |
| **Date** | 2026-10-31 |
| **Deciders** | Project owner |
| **Related** | HLD-ADR-009, [00-reliability.md §7.6](../00-reliability.md#76-optimistic-locking) |

## Context
Read-modify-write updates need a version to detect concurrent changes (the owner's row-versioning design). PostgreSQL offers a hidden `xmin` column or an explicit column.

## Decision drivers
- Visible, testable behaviour.
- Version can be exposed to clients.

## Considered options
1. Explicit `version bigint` column
2. PostgreSQL `xmin`

## Decision
Chosen: **explicit `version bigint not null default 0`**, checked in the `WHERE` clause and incremented by 1 on every update, returned to clients (gRPC field, ETag for JSON).

## Pros and cons of the options
### Explicit column
- ✅ Matches the owner's design; portable; clients can hold it.
- ❌ One extra column per table.

### xmin
- ✅ No extra column; native EF Core support.
- ❌ Internal value, changes on VACUUM FREEZE, awkward to expose.

## Consequences
- ✅ Conflicts are deterministic and testable (T-18).

## Links
- [00-reliability.md §4.4, §7.6](../00-reliability.md)
