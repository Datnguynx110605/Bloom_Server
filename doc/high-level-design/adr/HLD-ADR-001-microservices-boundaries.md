# HLD-ADR-001: Build Bloom as 8 domain microservices behind an API gateway

| | |
|---|---|
| **Status** | Accepted |
| **Date** | 2026-10-31 |
| **Deciders** | Project owner |
| **Related** | HG-2, HG-4, [hld.md §5](../hld.md#5-components) |

## Context
Normal traffic is about 150 req/s at peak, which a single service could handle. The launch spike (100k queued, ~20k status reads/s, ~200 checkouts/s) is concentrated on one path. Bloom is a portfolio project meant to show distributed-systems skills: sagas, outbox, inbox, independent scaling. The owner's first proposal had 9 services whose boundaries didn't match the approved domain (no catalog owner, inventory vs stock split, an out-of-scope recommendation service, pre-orders owning their own orders).

## Decision drivers
- The launch path must scale and fail independently (HG-2).
- Each service builds and deploys independently (HG-4).
- Every domain area has exactly one owner.
- Solo build effort.

## Considered options
1. Microservices with boundaries aligned to the domain
2. Modular monolith + separate launch service + notification worker
3. Microservices as first proposed

## Decision
Chosen: **microservices aligned to the domain**: `api-gateway`, `identity`, `catalog`, `inventory`, `order` (with cart and coupons), `launch`, `payment`, `fulfillment` (shipping, returns, warranty coverage), `notification`. Recommendation is dropped (out of scope). Justified by independent scaling and fault isolation of the launch path, not by DAU.

## Pros and cons of the options
### Domain-aligned microservices
- ✅ Real saga, outbox and inbox boundaries. The launch path scales alone.
- ❌ Highest build and operations cost.

### Modular monolith + launch service
- ✅ About a third of the build cost.
- ❌ Fewer real distributed flows to show.

### As first proposed
- ❌ Ownership gaps and a duplicated order lifecycle.

## Consequences
- ✅ Clear ownership per domain area (hld.md §5, §6).
- ✅ Coupons live in `order`, so the coupon hold is a local transaction at checkout start.
- ❌ Cross-service flows need sagas and eventual consistency.
- ⚠️ Build scope risk R-01.

## Links
- [hld.md §5, §6](../hld.md)
