# HLD-ADR-012: Launch path: Postgres counters, Redis sorted-set waiting room, configured admission rate

| | |
|---|---|
| **Status** | Accepted |
| **Date** | 2026-10-31 |
| **Deciders** | Project owner |
| **Related** | BI-01, BI-03, LCH-R04 … R28, QA-04, QA-05, PD-ADR-005 |

## Context
Launch target: 100k queued, 5k units, ~20k status reads/s, ~200 checkouts/s. Allocation and purchase limits must never be exceeded. Queue status must never hit a database.

## Decision drivers
- BI-01 and BI-03 durable and exact.
- 20k status reads/s at p95 < 50 ms.
- Predictable, testable load on checkout.

## Considered options
- **Counters:** Postgres conditional update · Redis Lua as truth · Redis gate + Postgres truth
- **Queue:** Redis sorted set + polling · Redis + WebSocket/SSE push · Postgres queue table
- **Admission:** configured rate capped by load test · adaptive
- **Queue durability:** AOF + replica, accept rare loss · mirror to Postgres

## Decision
Chosen:
- **Counters in launch's Postgres**: one row per launch SKU, one per (account, launch product), changed by conditional updates in one transaction (HLD-ADR-009). The admission pass is consumed in the same call (LCH-R22).
- **Waiting room in launch's Redis**: a sorted set per launch, random score for pre-launch joiners, arrival time afterwards. Clients poll at a server-set interval; each poll is one rank lookup.
- An **admission worker** admits a **configured rate per launch**, capped by the load-tested ceiling (target 200/s), and issues **signed admission passes**.
- launch's Redis uses **AOF (fsync every second) + a replica with failover**. Losing ~1 s of joins is an accepted risk.

## Pros and cons of the options
### Postgres counters
- ✅ Durable truth; no oversell on failover.
- ❌ Hot rows; viable because admission caps the rate.

### Redis Lua as truth
- ❌ A failover can roll counts back and oversell.

### Polling
- ✅ Stateless and simple to scale.
- ❌ Constant read traffic (by design served from Redis).

### Adaptive admission
- ❌ A feedback loop that's hard to test.

## Consequences
- ✅ Invariants durable; launch scales by adding gateway and launch replicas.
- ⚠️ R-02 (hot rows) and R-03 (queue loss) tracked.

## Links
- [hld.md §8.1](../hld.md#81-launch-queue--checkout)
