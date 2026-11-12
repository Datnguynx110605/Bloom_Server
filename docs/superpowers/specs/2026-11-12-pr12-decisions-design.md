# PR #12 open items: owner decisions and residual findings

| | |
|---|---|
| **Date** | 2026-11-12 |
| **Branch** | `fix/pr12-decisions` (from `fix/pr-followups` at `6f899ea`; PR base `fix/pr-followups`, stacked on PR #12) |
| **Source** | PR #12 description: "Owner decisions needed (parked)" items 1–12, the D27 ruling, and "Residual and parked findings" |
| **Status** | Approved (2026-11-12) |

## 1. Intent

PR #12 ended with 12 owner decisions, one doc edit proposed by a ruling (D27), and residual findings. The owner decided all of them in a session on 2026-11-12. This project puts those decisions into `doc/` and code, each with a test, so that PR #12 and this PR together leave no open item except the ones §7 lists.

Every answer below is the owner's choice. Points marked **[my call]** were proposed by me in the closing playback and the owner accepted that playback without changes ("nothing"). Strike any in review.

## 2. Goals, non-goals, anti-goals

**Goals**
- Every item in §3 is done as stated, with a test where §3 names one.
- Every doc edit in §4 is applied upstream first: product, then HLD, then LLD. Each changed document's header becomes `Approved (2026-11-12)`.
- `dotnet build -c Release` with 0 warnings. Every test project passes, run one project at a time.

**Non-goals**
- No change beyond §3. In particular: no "skip $0 refund" path (see R4), no work on PR #12's deferred ledger minors, no history cleanup of PR #12's commits.

**Anti-goals** (it fails even if it "works")
- A `doc/` edit that isn't listed in §4.
- Weakening an existing guarantee (no double apply, no oversell, no over-refund, no lost message) to make a fix easier.
- A commit that mixes `doc/` and code.

## 3. Decisions

| ID | Decision | Code and tests |
|---|---|---|
| **1 · D33 order side** | Order tracks returns **per return**, not with a counter. New table `order_returns(order_id, return_id, closed_at timestamptz null)`, PK `(order_id, return_id)`. `return-requested` → `INSERT … ON CONFLICT DO NOTHING`. `return-closed` → upsert that sets `closed_at` if it is null (inserts a closed row if `requested` hasn't arrived yet). An open return = a row with `closed_at IS NULL`. `HasUnsettledOrders` (ACC-R13) uses that instead of `open_returns > 0`. The `orders.open_returns` column is dropped. **[my call]** The closed-row insert handles `return-closed` arriving before `return-requested` (separate queues), which the counter also got wrong. No backfill (pre-production). | Migration. Tests: requested→closed→closed (decided after expired) leaves no open return; two returns, one closed twice, still one open; closed before requested ends closed; `HasUnsettledOrders` true while a row is open. |
| **2 · stale ADR lists** | Index lines list the new ADRs (§4). | None (doc only). |
| **3 · IR-05** | IR-05 names both senders (§4). | None. |
| **4 · release vs reserve race** | Inventory's release-command consumer, `ReserveStock` and the `order.paid.v1` consumer each take `pg_advisory_xact_lock(<key from order_id>)` as the first statement of their transaction, before the tombstone check and before any SKU lock. The allocator and the `reshipId` release branch don't take it. **[my call]** Key: a stable 64-bit value derived from the order id, defined in one place. | Forced-interleaving tests: a release blocked after its tombstone insert makes a concurrent `ReserveStock` wait, which then returns `ORDER_RELEASED`; the same for `order.paid` (no allocation created). |
| **5 · address vs delete** | `UpsertAddress` creating an address (or setting a default) reads `status` under the account row lock it already takes; `deleted` → `NOT_FOUND`. | Forced-interleaving test: an upsert that waited behind a delete gets `NOT_FOUND` and no address row exists. |
| **6 · re-reship** | The reship id is per flag occurrence: `NameBased(ReshipNamespace, "<shipment_id>:<dismissed_at as Unix ms, or empty when null>")`. `CommitReshipAsync` reads `dismissed_at` from the failure row it already reads; `ReshipAsync` keeps receiving the id from the guard. A lost failure can only be re-flagged after a dismissal, so the id is stable within one flag (concurrent confirmers share it; the D27 same-id check still holds) and new after each dismissal. | Test of the full sequence: reship commits stock → dismissed (D27 release) → re-flag → confirm lost (reship) → inventory commits stock again under a new id, and the reship task is created. |
| **D27 doc** | 05-fulfillment line 286 matches the code's same-reship guard (§4). | None. |
| **7 · D32 handover time** | `IShippo.GetTrackingStatusAsync` returns the status **and** Shippo's `tracking_status.status_date` (nullable). A scan-implied handover sets `handed_over_at = status_date`, or the processing time when it's missing or unparsable, and records `time_to_handover_seconds` as the manual HandOver does. | Tests: scan with a date → `handed_over_at` = that date and one histogram sample; scan without a date → processing time. |
| **8 · settlement-side close** | Doc only: the phrase is removed (§4). The admission tick already closes the room (LCH-R23). | None. |
| **9 · IN-7** | `mark-lost-in-transit` for a serialized SKU with no `shipped` serial is skipped (no write) when the ledger already holds a `lost_in_transit` entry for that `refId` and SKU. It stays a `PermanentFailure` when nothing was ever counted. | Test: a second distinct command for a serialized ref is a no-op (counters and ledger unchanged, not dead-lettered); a ref with no shipped and no counted serial still fails permanently. |
| **10 · L-6** | `CustomerTests.cs:201`: the early joiners' positions equal the set {1, 2, 3}. Line 177 unchanged. | Test-only. |
| **11 · delivery limit** | Consumer queues get `x-dead-letter-exchange = ""` and `x-dead-letter-routing-key = <queue>.dlq`. The default quorum delivery limit (20) stays. A message over the limit lands in the DLQ (replayable, LLD-ADR-009) instead of being dropped. Recorded in new **LLD-ADR-015** "Consumer queues dead-letter at the delivery limit". Existing queues must be re-declared; for local dev, remove the RabbitMQ volume (note in the PR). | `TopologyTests`: the consumer queue's arguments. A test that a message NACK-requeued past the limit reaches `<queue>.dlq`, if the test broker allows a low limit on that queue; otherwise the argument test alone **[my call]**. |
| **12 · pause scope** | Doc only (§4): the pause is per consumer queue. | None. |
| **R1** | `RedisDegradedProbe`: a `RedisServerException` that isn't an outage (for example `NOPERM`) means "Redis answered": not degraded, doesn't escape. | Test with a non-outage server error. |
| **R2** | The shared outage predicate also matches `MISCONF`, `OOM` and `NOREPLICAS` (→ degraded mode, not 500). `EdgeStore` loads its Lua script on the primary (a non-replica server), not `GetServers()[0]`. | Predicate tests per prefix; a test or assertion that the script is loaded on the primary. |
| **R3** | One after-commit mechanism: the `AfterCommitInterceptor` and its context hooks move into `Bloom.Shared`. Inventory, Notification and Payment use it. Payment's `Transitions` registers its actions there, and the 5 `transitions.Emit()` calls are removed. A rolled-back transaction still drops its actions. No behaviour change. | Shared tests: runs after commit, dropped after rollback, a later transaction doesn't run a rolled-back one's actions. Existing Payment P-4 tests keep passing. |
| **R4** | New rule **CHK-R20**: "An order's total must be at least $0.50, the card processor's minimum. Checkout refuses to start otherwise." 03-order §6.2 step 4 (compute totals): `total < 50` → **`ORDER_TOTAL_TOO_LOW`** (`FAILED_PRECONDITION`), handled like the other step 3–4 failures (order → `cancelled` / `checkout_failed`, error stored on the key, no holds placed). Checkout metric result `order_total_too_low`. **[my call]** No "skip $0 cancellation refund" path: with CHK-R20 a paid order's total is ≥ 50 cents, so that refund can't be 0. **[my call]** No HLD edit (no interface or quality attribute changes). | Tests: a 100 % coupon with Standard shipping → `ORDER_TOTAL_TOO_LOW` and the order cancelled; a total of exactly 50 → succeeds; 49 → refused. |

## 4. Doc edits (approved list)

One commit per layer for indexes and per LLD document otherwise; no commit mixes `doc/` and code. Order: product → HLD → LLD.

**Product design**
- `00-overview.md` (≈ line 142): add the PD-ADR-009 row to the ADR list.
- `05-checkout-payment.md`: add CHK-R20 (text in §3 R4) next to the pricing/payment rules.

**HLD**
- `high-level-design.md` (≈ line 401): add the HLD-ADR-019 row.

**LLD**
- `README.md` line 11 and the component index: list LLD-ADR-011 … 015 against the components that cite them.
- `doc/README.md` (map, lines 27/30/35): `PD-ADR-001 … 009`, `HLD-ADR-001 … 019`, `LLD-ADR-001 … 015`.
- `adr/LLD-ADR-015-consumer-queue-dlx.md` (new) and `adr/README.md` row. Related: LLD-ADR-002, HLD-ADR-019, LLD-ADR-009.
- `00-reliability.md`: header "Decision records" → include 011 and 015; §6 queue-arguments row "Consumer queue" gains the two DLX arguments and the LLD-ADR-015 link; §7.3 step 8 (line 233) "The pause is per process" → "The pause is per consumer queue (each queue pauses on its own outage and probes on its own)".
- `01-launch.md`: header decision records include LLD-ADR-012 if missing; line 307 drop "and the settlement-side close".
- `02-inventory.md`: header decision records include LLD-ADR-014 if missing; IR-05 → "`inventory` never releases a reservation on its own; only a command from `order` (by order) or from `fulfillment` (by `reshipId`, §6.5) does."; §7 new row "Release vs concurrent reserve / paid → per-order transaction advisory lock taken first by the release consumer, `ReserveStock` and `order.paid` (before the tombstone check and any SKU lock)"; §8 new row "Repeated lost-in-transit for a serialized ref → no-op once its units are counted".
- `03-order.md`: §4 drop `open_returns`, add `order_returns`; §5 row (line 208) → insert / mark closed per `returnId`; error table `ORDER_TOTAL_TOO_LOW`; §6.2 step 4 and §6.3 step 8 mention CHK-R20; line 376 "`open_returns > 0`" → "an `order_returns` row with `closed_at` null".
- `05-fulfillment.md`: line 286 "…if the apply transaction finds the failure no longer confirmed **and not resolved by this same reship**, it writes…"; §8 new row "Reship after a released reship → the reship id is per flag occurrence (shipment id + `dismissed_at`), so inventory commits stock again"; §6.3 line 254 "`handed_over_at` = event time" → "= the carrier's `status_date` (processing time if absent)".
- `06-identity.md`: header decision records include LLD-ADR-013 if missing; §8 new row "Address change racing a delete → `UpsertAddress` re-checks `status` under the account row lock: `NOT_FOUND`".

## 5. Constraints

- AGENTS.md: every code choice traces to an ID in this document; `doc/` changes only as §4 lists.
- Stack: .NET 10, PostgreSQL, RabbitMQ 4.1 quorum queues, Redis; tests use Testcontainers via Colima (if Docker is down, stop and report).
- Existing guarantees and tests stay green, including PR #12's Review Focus 1–5.

## 5a. Builder-check rulings [my call]

- **4, lock key space.** The new lock is a single-`bigint` advisory key, a different key space from the allocator's existing two-int lock `(0x4F4C4B, hashtext(orderId))` (IN-1), which stays unchanged. Order: new order lock → SKU locks → allocator's order lock. Nothing holding the allocator's lock waits on the new one, so there is no cycle; update the IN-1 comment to say so. The new 02-inventory §7 row states this order (no other §7 edit). In `ReserveStock`, the lock and the tombstone check move **inside** each `CommitAsync` attempt's transaction.
- **R3, failure semantics.** One shared mechanism: async (`Func<Task>`) actions, run after commit with `CancellationToken.None`, each failure logged as a warning and swallowed (an action must never fail an already-committed unit of work and cause a redelivery); actions are cleared on rollback and when a new transaction begins. Inventory's propagate-on-throw goes away; its after-commit actions are metrics/logs only.
- **1, version and schema.** A return event that changes an `order_returns` row also bumps `orders.version` and `updated_at` (LLD-ADR-005); a no-op upsert doesn't. `order_returns.order_id` has a foreign key to `orders`. Drop `ck_orders_open_returns_non_negative` with the column, and update the `OpenReturns` tests (`AccountTests`, `FulfillmentEventTests`, `SchemaTests`).
- **R4, scope.** CHK-R20 applies to regular and launch checkouts alike (§6.2 step 4 is shared) and is checked on the final `total` (after discount, tax and shipping). Every order passes step 4, so the "no $0 refund" reasoning holds.
- **11, DLQ headers.** Broker-dead-lettered messages carry `x-death` instead of `x-failure-reason` / `x-failed-at`. The DLQ admin already treats missing headers as `""` (`DeadLetterAdminService.cs:132-133`), so list and replay work unchanged. No scope change.
- **7, skewed dates.** `handed_over_at` = `status_date` clamped to [the pack task's `created_at`, now]; the histogram uses the clamped value.
- **6, id collisions.** A re-flag always follows a dismissal, and two dismissals of one failure are separated by a flag, so `dismissed_at` always advances; no same-millisecond collision.

## 6. Left to the builder

Names of private helpers and test files; the order-id → advisory-key function; how to pick the primary server in `EdgeStore`; migration names; exact placement of the new rows in each doc section.

## 7. Still open after this project

- PR #12's deferred ledger minors (test-strength and logging nits).
- PR #12 commit hygiene (fixed by a squash-merge) and PR #10 (no content).
