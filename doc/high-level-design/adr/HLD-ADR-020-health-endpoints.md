# HLD-ADR-020: Health endpoints

[Docs](../../README.md) › [High-level design](../high-level-design.md) › [Decision records](README.md)

| | |
|---|---|
| **Status** | Accepted |
| **Date** | 2026-11-14 |
| **Deciders** | Project owner |
| **Related** | HLD-ADR-014, HLD-ADR-019, QA-01 |

## Context
Kubernetes needs a liveness and a readiness endpoint per pod (HLD-ADR-014: API and worker deployments of each service). Only the gateway has them (07-api-gateway §6.6). Several designs already keep a service running through a dependency outage instead of taking it out of rotation: consumers pause during their own database outage (HLD-ADR-019), launch status never touches Postgres (LR-04), catalog reads Postgres when Redis is down, the gateway degrades without Redis.

## Decision drivers
- A dependency blip must not pull every pod out of rotation at once (cascading failure).
- Keep the degraded behaviour already designed.
- One shape for every service.
- Minimal code.

## Considered options
Readiness:
1. Started only
2. The service's own Postgres reachable
3. Per-service rules in each LLD

Worker health:
- (a) `/healthz` only
- (b) `/healthz` and `/readyz` like API pods
- (c) Liveness from loop heartbeats

## Decision
Chosen: readiness **started only** and workers **`/healthz` only**.

Every domain service exposes `/healthz` (200 while the process runs) in API and worker mode, and `/readyz` in API mode only: 200 once started, 503 once shutdown begins so traffic drains before the pod exits. Neither checks a dependency. The endpoints are internal; the gateway does not route them.

## Pros and cons of the options
### 1. Started only
- ✅ No cascading unreadiness; outages are answered by the designed degraded paths.
- ❌ A pod with a dead dependency stays in rotation and answers `UNAVAILABLE`.

### 2. Own Postgres reachable
- ✅ Traffic avoids pods whose database is down.
- ❌ One database outage makes every pod of the service unready at once, and callers lose the service's `UNAVAILABLE` error contract.

### 3. Per-service rules
- ✅ Most precise.
- ❌ Eight sets of rules to write, document and keep in step.

### (a) `/healthz` only
- ✅ Matches the gateway worker.
- ❌ A hung loop is not restarted automatically.

### (b) `/healthz` and `/readyz`
- ❌ Readiness means nothing for a pod nothing routes to.

### (c) Heartbeat liveness
- ✅ Restarts hung workers.
- ❌ Heartbeat state and a limit per loop in the shared library.

## Consequences
- ✅ Every pod has probe targets.
- ✅ Shutdown drains API traffic.
- ❌ A stuck worker loop is caught by the existing alerts (outbox age, consumer lag, DLQ, stuck sagas, HLD §9.3), not by a restart.
- ⚠️ The probes themselves are configured in the Kubernetes manifests (not yet written).

## Links
- [HLD-ADR-014](HLD-ADR-014-deployment.md)
- [HLD-ADR-019](HLD-ADR-019-consumers-pause-during-own-db-outage.md)
- [high-level-design.md §9.5](../high-level-design.md#95-deployment-topology)
- [07-api-gateway §6.6](../../low-level-design/07-api-gateway.md#66-health)
