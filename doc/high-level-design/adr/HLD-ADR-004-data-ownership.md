# HLD-ADR-004: Database per service in one HA cluster, Redis per service, event-fed copies

| | |
|---|---|
| **Status** | Accepted |
| **Date** | 2026-10-31 |
| **Deciders** | Project owner |
| **Related** | QA-01, [hld.md §6](../hld.md#6-data-ownership) |

## Context
Microservices need clear data ownership. Running 8 HA Postgres clusters and shared caches both have costs. The 99.9% target needs automatic database failover.

## Decision drivers
- One source of truth per piece of data.
- 99.9% availability.
- Operational effort for one person.

## Considered options
- **Postgres:** one HA cluster with a database per service · cluster per service · single instance
- **Redis:** per service · shared with key-prefix rules · shared with no rules
- **Cross-service data:** event-fed copies or gRPC lookup, decided per case

## Decision
Chosen:
- **One CloudNativePG cluster** (primary + 1 replica, automatic failover) hosting **8 databases**, each with its own credentials. No cross-database access.
- **One Redis instance per service.** gateway and launch get a replica with failover; others run single.
- **No service reads another's store.** Data is shared by gRPC or event-fed copies: notification's contact copy and the gateway's revocation list (from identity events), catalog's verified-buyer eligibility (from order events).

## Pros and cons of the options
### One cluster, DB per service
- ✅ Logical isolation with one cluster to run.
- ❌ Noisy-neighbour risk (R-06).

### Cluster per service
- ❌ 16+ database pods.

### Redis per service
- ✅ Strongest cache isolation.
- ❌ More instances; idle ones for services with little cache need.

## Consequences
- ✅ Ownership is enforceable by credentials.
- ❌ Copies are eventually consistent.
- ⚠️ launch can move to its own cluster if load tests show contention.

## Links
- [hld.md §6, §9.5](../hld.md)
