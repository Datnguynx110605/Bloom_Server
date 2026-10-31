# HLD-ADR-013: Synchronous orchestrated holds at checkout; DB sweep for expiry; auto-refund late payments

| | |
|---|---|
| **Status** | Accepted |
| **Date** | 2026-10-31 |
| **Deciders** | Project owner |
| **Related** | CHK-R04 … R14, CAT-R08, PD-ADR-003, PD-ADR-004, HLD-ADR-005 |

## Context
Checkout start must answer "reserved" or "sold out" immediately, lock prices (CAT-R08), and expire after 15 minutes. Payments confirm asynchronously and can arrive after expiry.

## Decision drivers
- Immediate feedback for the customer.
- Correct prices at checkout start.
- Durable expiry.
- BI-01, BI-02 never violated by late payments.

## Considered options
- **Holds:** sync gRPC, orchestrated by order · fully async
- **Prices:** sync gRPC to catalog · local replica via events
- **Expiry:** DB sweep job · delayed message
- **Late payment:** auto-refund · try to re-reserve

## Decision
Chosen:
- `order` orchestrates checkout start: **batched prices from catalog (gRPC)**, then a **hold in launch or inventory (gRPC)**, then the order + snapshot + coupon hold in **one local transaction**. Failures after a hold trigger compensation commands.
- **Expiry**: a worker in `order` claims *Pending payment* orders past their deadline (`SKIP LOCKED`) and cancels them, emitting release commands. A few seconds of lateness is acceptable.
- **Late payment**: if the order is already *Cancelled*, `order` commands a full refund and notifies the customer. It never re-reserves.

## Pros and cons of the options
### Sync holds
- ✅ Immediate answer.
- ❌ catalog, inventory and launch are on the checkout path.

### Sync prices
- ✅ Always current.
- ❌ catalog must be highly available (replicas + cache).

### DB sweep
- ✅ Deadlines durable in the database.
- ❌ Seconds of lateness.

### Auto-refund
- ✅ Can't oversell.
- ❌ An unlucky customer has to start over.

## Consequences
- ✅ BI-01, BI-02 and CAT-R08 hold.
- ❌ Checkout latency includes three gRPC calls (budget p95 < 300 ms, QA-03).

## Links
- [hld.md §8.1, §8.2](../hld.md)
