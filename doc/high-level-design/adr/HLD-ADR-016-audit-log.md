# HLD-ADR-016: identity owns the audit log, fed by staff-action events

| | |
|---|---|
| **Status** | Accepted |
| **Date** | 2026-10-31 |
| **Deciders** | Project owner |
| **Related** | BI-06, ACC-R11, LCH-R26, INV-R12 |

## Context
Staff actions happen in every service, but Admin needs one place to review them (ACC-R11).

## Decision drivers
- One searchable timeline.
- No lost audit entries.
- No extra service.

## Considered options
1. identity, fed by events
2. Each service locally
3. Dedicated audit service

## Decision
Chosen: **every service publishes a "staff action recorded" event through its outbox**, in the same transaction as the action. **identity** consumes these events into one append-only audit store that Admin can query.

## Pros and cons of the options
### identity, fed by events
- ✅ One timeline; the outbox guarantees no entry is lost.
- ❌ Audit entries appear after a short delay.

### Each service locally
- ❌ Admin has to query eight places.

### Dedicated service
- ❌ One more service.

## Consequences
- ✅ BI-06 audit part enforced by the outbox.
- ❌ identity's storage grows with all staff activity (retention: OQ-HLD-8).

## Links
- [hld.md §6](../hld.md#6-data-ownership)
