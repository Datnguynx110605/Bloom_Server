# HLD-ADR-005: Orchestrate compensating flows, choreograph simple reactions

| | |
|---|---|
| **Status** | Accepted |
| **Date** | 2026-10-31 |
| **Deciders** | Project owner |
| **Related** | HG-3, [hld.md §8](../hld.md#8-key-flows) |

## Context
Checkout, cancellation, return → refund and delivery failure span several services and need compensations and timeouts. Notifications, review eligibility and counter updates are one-way reactions.

## Decision drivers
- Traceable flows with explicit state and timeouts.
- Loose coupling where no compensation is needed.
- Compensations must never be lost.

## Considered options
1. Hybrid
2. Orchestration everywhere
3. Choreography everywhere

**Compensation transport:** commands via outbox · sync gRPC with retry

## Decision
Chosen: **hybrid**. `order` orchestrates checkout and cancellation. `fulfillment` orchestrates return → refund and delivery failure. Each keeps explicit saga state. Simple reactions are choreographed through events. **Compensations are commands** written to the orchestrator's outbox in the same transaction as its saga state, delivered via RabbitMQ and applied idempotently (inbox). Forward holds at checkout stay synchronous gRPC.

## Pros and cons of the options
### Hybrid
- ✅ Explicit where it matters, loose elsewhere.
- ❌ Two styles to understand.

### Orchestration everywhere
- ❌ Orchestrators know about everything.

### Choreography everywhere
- ❌ Checkout steps scattered; hard to time out or trace.

### Compensations via sync gRPC
- ❌ Lost if the orchestrator crashes mid-retry.

## Consequences
- ✅ A started flow always completes or is compensated.
- ❌ Compensations are eventual (seconds), not instant.

## Links
- [hld.md §8.3, §8.4](../hld.md)
