# PD-ADR-005: Launches use a fixed allocation, a waiting room and per-launch purchase limits

| | |
|---|---|
| **Status** | Accepted |
| **Date** | 2026-10-31 |
| **Deciders** | Project owner |
| **Related** | LCH-R01–R28, BI-01, BI-03 |

## Context
A new device launch brings far more buyers than units, all arriving within minutes. Bloom needs a rule for how limited units are allocated, how the surge is controlled, and how to stop a few buyers or bots from taking everything. This is the project's headline backend problem.

## Decision drivers
- Never oversell (BI-01) or exceed per-account limits (BI-03) under contention.
- Protect the system from the launch surge.
- Fairness: no advantage for bots or constant refreshing.
- Keep the launch checkout path short.

## Considered options
**Allocation**
1. Fixed allocation, first come first served
2. Unlimited pre-orders with ship-date waves (Apple's model)
3. Fixed allocation + waiting room

**Queue order:** strict arrival order · random for pre-launch arrivals, then arrival order · opens at launch time only

**Purchase limit:** per launch, set by staff · per account + per address · none

**Queue scope:** one queue per launch · one per SKU

**Who can queue:** signed-in accounts only · anyone

**Mixed orders:** launch items separate · mixed with regular items

## Decision
Chosen:
- **Fixed pre-order allocation per launch SKU, with one waiting room per launch.**
- The waiting room opens **15 minutes before launch time**. Pre-launch arrivals get **random positions**, later arrivals queue in arrival order.
- Only **signed-in, email-verified accounts** can queue, one position per account.
- Admitted customers get a **10-minute admission pass** to start checkout.
- **Per-launch-product purchase limit set by staff**, counting paid and in-checkout units.
- **Launch orders contain only launch items** (plus warranty plans). No coupons.
- Released units go **straight back to the pool**.
- **One checkout per admission pass.** The waiting room closes for good when allocation is sold and no checkouts are in progress.
- From *Announced*, allocation can **only increase**. Purchase limits are **locked** from *Pre-order open*.
- The launch moves to *Released* automatically at its **release date**. Purchase limits end and the waiting room closes.

## Pros and cons of the options
### Fixed allocation + waiting room
- ✅ Explicit, testable no-oversell problem, plus controlled load.
- ✅ Fairer than a raw race.
- ❌ Adds a queue component and admission control.

### Fixed allocation, FCFS
- ✅ Simpler.
- ❌ The first seconds are a free-for-all that hits the system at full force.

### Unlimited with ship-date waves
- ✅ Never "sold out".
- ❌ Needs shipment-wave planning and ship-date recalculation. Weaker concurrency story.

### Random pre-launch order
- ✅ Early refreshing and bots gain nothing.
- ❌ Less transparent than strict arrival order.

### Signed-in queue only
- ✅ Blocks anonymous bots. Supports per-account limits.
- ❌ Customers must sign in before launch.

### Separate launch orders
- ✅ No split shipments. Short launch checkout path.
- ❌ Customers buying accessories too need two checkouts.

## Consequences
- ✅ A strong, well-bounded backend showcase: queue, admission, atomic allocation, limits.
- ❌ More moving parts than a normal checkout.
- ⚠️ Allocation can never shrink mid-launch, so a supplier cutting delivery is handled by pre-orders waiting for later receipts (INV-R11), not by reducing allocation.
- ⚠️ Quantitative launch targets are defined in the HLD.

## Links
- [04-launch](../04-launch.md)
