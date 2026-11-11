# PR follow-ups: design decisions, doc backlog, defects and CI

| | |
|---|---|
| **Date** | 2026-11-12 |
| **Branch** | `fix/pr-followups` (from `feat/launch`; PR base `feat/launch`) |
| **Source** | Descriptions of PRs #1–#11, checked against the code on `feat/launch` at `e349de2` |
| **Status** | Approved (2026-11-12) |

## 1. Intent

PRs #1–#11 build Bloom's services as a stack. Each description ends with owner questions, residual findings and proposed doc edits. This project clears that list:

- The owner decided every design question in a session on 2026-11-12 (§3). Items marked **[default]** were not asked one by one: the owner approved the recommended answer for every remaining question in bulk ("I approve all of your proposal"). Strike any of them in review.
- The doc-edit backlog of every spec and plan is applied (§4), after the owner approves this document.
- Defects, flaky tests and test gaps named in the PR bodies are fixed (§5).
- CI goes green on every open PR, #2–#11 (§6).

**Why now.** Before the stack merges into `main`: CI is red, one PR carries a DESIGN CHANGE that blocks merge (LR-06), and several confirmed bugs touch money, stock and access.

## 2. Goals, non-goals, anti-goals

**Goals**
- Every decision in §3 is in `doc/` (upstream first, with an ADR where §3 names one) and in code, with a test.
- Every approved backlog edit in §4 is applied to `doc/`, and each changed document's header becomes `Approved (<approval date of this spec>)` (precedent: inventory, `253b629`).
- Every item in §5 is fixed or tested as stated.
- `dotnet build -c Release` has 0 warnings. Every test project passes on its own. CI is green on PRs #2–#11 and the new PR.

**Non-goals**
- No new features beyond §3. No refactors beyond §5.
- No change to the order of the PR stack and no merging of PRs. Branches below `feat/launch` change only as §6 says.
- No CI parallelism change. CI on Linux runs the whole solution green; the local Colima flake is a machine limit.

**Anti-goals** (it fails even if it "works")
- A doc edit under `doc/` that isn't listed in this document.
- A behaviour change visible outside a service that isn't listed in this document.
- Weakening an existing guarantee (no double apply, no oversell, no over-refund) to make a fix easier.

## 3. Design decisions

Each row: the decision, the docs to change, and what the code and tests must show. ADR numbers are the next free IDs in each layer.

### 3.1 Cross-cutting (Bloom.Shared, 00-reliability)

| ID | Decision | Docs | Code and tests |
|---|---|---|---|
| D1 | **DLQ admin role is lowercase `admin`**, as identity issues it. | 00-reliability §7.7 line 311: change `x-bloom-role = Admin` to `admin`. No ADR (wording fix). | Today `DeadLetterAdminService.cs:123` compares `"Admin"`, and the tests send `"Admin"` (`Bloom.Shared.Tests/DeadLetterAdminTests.cs` and every service's DLQ test, including the audit `actorRole` assertion). Change all of them to `"admin"`. New test: `Admin` (capital) gets `ROLE_NOT_ALLOWED`. |
| D2 | **DLQ pass lock is transaction-scoped.** One transaction per pass: `pg_try_advisory_xact_lock(<same key>)`; false → `DLQ_BUSY`; `COMMIT` at the end releases it. This works through the PgBouncer transaction-mode pooler (HLD-ADR-004). | New **LLD-ADR-011** "DLQ pass lock is transaction-scoped", which supersedes LLD-ADR-010 (010's status → `Superseded by LLD-ADR-011`; ADR index updated). 00-reliability §7.7 wording. | `DeadLetterAdminService` holds an open transaction on its lock connection for the pass. Audit commits stay on their own connections. T-23 still passes, and a test shows the lock is released when the pass ends or throws. |
| D3 | **Consumers pause while their own database is unreachable.** When `BEGIN`/`COMMIT` (or the inbox insert) fails because the service's own PostgreSQL can't be reached, the host NACKs the delivery with requeue, does **not** count an attempt, stops consuming, and probes the DB every 1 s. Consuming resumes when the probe succeeds. Every other failure keeps the 5-attempt rule. | HLD-ADR-007 sets "5 attempts in total" for every transient failure, so this goes **upstream first**: new **HLD-ADR-019** "Consumers pause during their own database outage", which amends HLD-ADR-007. 007 gets one `Amended by HLD-ADR-019` line in its header and the HLD ADR index is updated. Then 00-reliability §7.3 step 8 gains the exception. | Consumer host in `Bloom.Shared`. Scope **[my call]**: per process, consumers only (the outbox relay keeps its own loop and pause). The probe is a `SELECT 1` on the service's normal connection string. Classifier: Npgsql connection-level failures (connection refused or reset, timeout opening a connection, `57P01`/`57P03` "shutting down / not accepting connections"). The builder defines the exact list in one place and tests it. Tests: with the DB container paused or stopped, messages are neither dead-lettered nor attempt-counted, and they are processed after recovery. With a handler throwing a normal exception, the 5-attempt rule is unchanged. |

### 3.2 Launch (01-launch)

| ID | Decision | Docs | Code and tests |
|---|---|---|---|
| D4 | **Admission is paced per launch, not per replica (LR-06).** The admission Lua script first does `SET wr:{L}:tick 1 NX PX 1000`. If the key exists, it pops nothing and returns 0. | New **LLD-ADR-012** "Admission is paced per launch". §4.2: new key `wr:{launchId}:tick`, string, 1000 ms. §6.4 step 7: the guard. §7 row "Two admission workers": advisory lock + tick key. §11 L-07: "two replicas ticking out of phase → admitted per second ≤ rate". This removes the "run one replica" note from #11. | Admission script. L-07 extended: two workers whose ticks are 500 ms apart admit ≤ `admission_rate` per second over 10 s. |
| D5 | **Hold vs close/release window closed.** HoldAllocation step 3 reads the `launches` row `FOR SHARE`, first, before the product and SKU locks. Room close (admission tick §6.4 step 5 and the settlement-side close) and every stage change (scheduler, admin) take the `launches` row `FOR UPDATE` before evaluating their conditions, in a statement after the lock (a fresh READ COMMITTED snapshot). | §6.5 step 3 and the lock-order sentence: launch row, then products, then SKUs. §7: new row "Hold vs room close / stage change → row lock on `launches`". | Forced-interleaving test: a hold blocked mid-transaction delays a concurrent close, and the close then sees the hold. |
| D6 | **Idempotent hold retry compares account and launch too.** Step 2: same account, launch and lines → return the stored hold; anything different → `HOLD_CONFLICT`. | §6.5 step 2 wording. | One test per mismatch (account, launch). |
| D7 **[default]** | **Less log noise.** While Redis is down, the admission worker logs its Error at most once per minute per launch (was every second). The stuck-hold monitor logs an Error when a hold first becomes stuck; afterwards the gauge carries it. | 01-launch §10, one sentence each. | Tests on the log counts with the fake clock. |

### 3.3 Identity (06-identity)

| ID | Decision | Docs | Code and tests |
|---|---|---|---|
| D8 | **Only `rotated` counts as refresh reuse.** `revoked` → `REFRESH_INVALID` with no side effects (no revoke-all, no `refresh_total{reuse}`). | New **LLD-ADR-013** "A revoked refresh token is not reuse". §6.3 table: split the `rotated` / `revoked` row. ID-03 unchanged. | `SessionService`. New test ID-T17: after `SignOutEverywhere`, an old device's refresh gets `REFRESH_INVALID` and the new session stays valid. |
| D9 | **`LAST_ADMIN` covers demote and deactivate only.** | §6.9 line 305 → "Demote or deactivate on the last active Admin → `LAST_ADMIN`". Doc only. | None. |
| D10 | **Google sign-in never replaces a linked Google identity.** If the email matches a customer whose `google_sub` is set to a different value → `EMAIL_TAKEN`. Linking happens only when `google_sub` is null. | §6.2 table: new row above "Email matches a customer". | One test. |
| D11 | **Default address is serialized on the account row.** `UpsertAddress(isDefault = true)` locks the account row (`FOR UPDATE`) before it clears the old default. `DeleteAccount` takes the account row first as well, so both lock in the order account → addresses. `DeleteAccount` re-checks `status` under that lock, so a second concurrent delete gets `NOT_FOUND` and publishes nothing (removes the duplicate `account-deleted` of R27). | §7: rows for "two default addresses" and "concurrent deletes". | Concurrency tests: two concurrent default upserts leave exactly one default; two concurrent deletes publish one `account-deleted`; delete and upsert don't deadlock. |

### 3.4 Gateway (07-api-gateway)

| ID | Decision | Docs | Code and tests |
|---|---|---|---|
| D12 | **Revocation stays fail-closed.** Revoked if `iat × 1000 < rev` (`iat` in s, `rev` in ms). A token issued in the same second as a revoke-all is treated as revoked, and the client recovers by refreshing. | §6.2 step 1 (backlog G1) plus that sentence. | None (already the behaviour). One test pins it. |
| D13 | **A non-Bearer `Authorization` header gets `TOKEN_INVALID`**, on every route that reads a token. | §5.3 error table and §6 auth step. | One test on a public-optional route (`/api/catalog/**`). |
| D14 | **`X-Forwarded-*` from an untrusted hop are replaced** with values from the actual connection. From a trusted proxy (`Gateway:TrustedProxies`), the existing processing applies. | §6 request hygiene (GW-06). | Test: a client-sent `X-Forwarded-For` doesn't reach the upstream. |
| D15 | **The Shippo webhook secret is redacted from gateway spans** (`url.path`, `http.target`, `url.full`) as `/webhooks/shippo/***`, as fulfillment already does. | §9 security, one line. | Test: no exported span contains the secret. |

### 3.5 Inventory (02-inventory)

| ID | Decision | Docs | Code and tests |
|---|---|---|---|
| D16 | **Release tombstone.** Every order-level release command inserts `released_orders(order_id)` (`ON CONFLICT DO NOTHING`). A later `order.paid.v1` for a tombstoned order creates no allocations and logs a warning. Tombstones are purged after 30 days **[default: 30 d]**. | New **LLD-ADR-014** "Release tombstone". §4: new table `released_orders (order_id uuid PK, released_at timestamptz)`. §6.5: the insert. §6 paid row: the check. §8 line 272: replaced by the tombstone rule. Backlog item I11 is superseded. | Test: release first, then paid → no backorders, warning logged. Purger test. |
| D17 | **ReserveStock refuses a released order.** For an order in `released_orders` it reserves nothing and returns new reason **`ORDER_RELEASED`** (`FAILED_PRECONDITION`). | Same ADR. §5.4 new row. 03-order §5.2: `ORDER_RELEASED` → the checkout answers `CHECKOUT_CLOSED` **[default]**. | Test in inventory. Test in order: the sweep releases, then a late reserve → `CHECKOUT_CLOSED`, no reservation left. |
| D18 | **Release by reship id.** `inventory.release-reservation.v1` gains an optional `reshipId` (additive, stays v1 per LLD-ADR-004). If it is set, inventory releases only the allocations with `ref_id = reshipId`, writes **no** tombstone, and still publishes `reservation-released {orderId}`. | §5.3 payload. §6.5: the `reshipId` branch. 05-fulfillment: see D27. | Inventory test. Order test: a `reservation-released` that matches no open saga step is a no-op (verify; add it if missing). |

### 3.6 Order (03-order)

| ID | Decision | Docs | Code and tests |
|---|---|---|---|
| D19 | **Fulfillment events may skip ahead.** `shipped` from `paid | packing`. `delivered` / `delivery_failed` from `paid | packing | shipped` **[default: `paid` included, for both earlier events late]**. A late earlier event finds the order further on and is a no-op. Each applied transition writes one history row (from → to). | §6.1 diagram: the new edges. §8 "Reordered messages": one row for this. | Consumer tests for each new edge, and for the late earlier event being a no-op. |
| D20 | **Refund saga closes on a late success.** `refund-completed` removes `refund` **or** `refund_failed` from the open saga; when empty → completed. | §5.3 consumer row (with backlog O6). | Test: failed, then completed → saga completed, `sagas_stuck` clears. |
| D21 | **Resume in rare states.** Resume by status: `creating` → continue at step 5. `pending_payment` → `CHECKOUT_CLOSED` if `deadline_at` has passed, else continue at step 7. `paid`, `awaiting_stock` or later → the normal response with `payment_url = ""`. `cancelled` → `CHECKOUT_CLOSED`. | §6.2: a resume table. | One test per row. No `DeadlineAt!` crash. |
| D22 | **The customer's view hides staff details.** For a customer caller, `GetOrder` history keeps `from`, `to`, `at` and `actor_type`. It drops `actor_id` unless it is the customer, and drops `reason` on rows with `actor_type = staff` **[my call: told apart by `actor_type`]**. System rows keep their reason codes (for example `checkout_expired`), and customer rows keep theirs. Support and Admin see everything. | §5.1 `GetOrder` row. | Test for both caller types. |
| D23 | **Cart write followed by a catalog outage is documented.** The write is kept, the answer is `UNAVAILABLE`, and a retry is safe (absolute set / remove). | §8 new row. Doc only. | None. |

### 3.7 Payment (04-payment)

| ID | Decision | Docs | Code and tests |
|---|---|---|---|
| D24 | **Refunds can't be created twice.** Stripe `409` is transient. Once `refunds.create` returns a refund object, create is never called again for that refund: known statuses apply as in backlog P1; an **unknown** status → `submitted`, store `stripe_refund_id`, error log; the webhook and reconciler settle it. Other 4xx stay permanent. | §6.3 executor text (merged with backlog P1). R10 reason list. | Tests: 409 then success → one refund; unknown status → `submitted`, no second create; reconciler settles it. |
| D25 | **Disputes: newest event wins.** `disputes.updated_at` stores the Stripe event's `created`. The upsert applies only `WHERE disputes.updated_at <= EXCLUDED.updated_at`. | §6.2 webhook row and §8. | Test: `closed`, then a late `created` → stays `won`/`lost`, and the gauge is right. |
| D26 | **Nightly comparison runs once per night.** New table `job_runs (name text PK, last_run_date date)`. A replica runs only if `UPDATE job_runs SET last_run_date = <UTC today> WHERE name = 'nightly_compare' AND last_run_date < <UTC today>` hits 1 row (the row is seeded by the migration). | §4: table. §6 nightly comparison: claim step. | Test: two workers on the same night → one run. |
| D27a | **CreatePayment retry contract.** A retry for the same `order_id` must carry identical `display_lines` (order derives them from the stored order). | §5.1 `CreatePayment` row (next to backlog P8). Doc only. | None. |

### 3.8 Fulfillment (05-fulfillment) and product rule RET-R15

| ID | Decision | Docs | Code and tests |
|---|---|---|---|
| D27 | **Reship compensation.** In ConfirmLost(reship), if the apply transaction finds the failure no longer confirmed, it writes `inventory.release-reservation.v1 {orderId, reshipId}` to the outbox, **commits**, and then returns `INVALID_STATE`. | §6 ConfirmLost(reship) row. | Test: the failure is resolved between CommitForReship and apply → `INVALID_STATE` and the release is sent. |
| D28 | **`RequestReturn` requires an `Idempotency-Key`** (product and plan-only), through the shared guard, single-transaction form. | §5.1 row. §8 "Duplicate return …" row. 00-reliability §7.4 "Applies to": add return requests. | Tests: a replay returns the same request; a missing key → `IDEMPOTENCY_KEY_REQUIRED`. |
| D29 | **UnassignSerial with no local record.** If the task is open and fulfillment has no record of the serial, it still calls inventory `UnassignSerial` for that order, and inventory's own check decides. | UnassignSerial row. | Test. |
| D30 | **Coverage end is derived at read.** `GetCoverage` returns `expired` when `status = active` and `ends_at ≤ now`. Nothing stored changes. | GetCoverage row and a §4 note. The status enum in the response gains `expired`. | Test. |
| D31 | **Account deletion closes waiting return requests.** The `account-deleted` consumer moves the account's return requests in `requested` to `expired` and publishes `return-closed (reason expired)`. | §6 account-deleted row. | Test: the label worker never picks them up. |
| D32 | **A carrier scan implies handover.** `TRANSIT` or `DELIVERED` on a `label_ready` outbound shipment first applies the HandOver step (task → `handed_over`, publish `shipment-shipped`, `handed_over_at` = event time), then the normal transition. | §6.3 table. | Test for both scans. |
| D33 | **An expired return can still be received.** `ReceiveReturn` also accepts `expired` (→ `received`), then normal inspection and decision; Support may reject. | **Upstream first:** 07-returns-refunds RET-R15 gains "If the parcel reaches the warehouse anyway, Warehouse receives it and it is inspected and decided as usual." Then 05-fulfillment §6.4 and the RET-R15 note. No ADR **[default]**. | Test. The order side gets a second `return-closed` (`decided` after `expired`); verify order handles it, and fix it if not. |
| D34 | **A lost parcel delivered later is recorded and alerted.** `DELIVERED` on a `lost` shipment sets `delivered_at`, keeps status `lost`, and logs an Error (alert → Support). No stock or money moves. | §6.3 table. | Test. |
| D35 | **A failed refund gives back the refundable amount.** On `payment.refund-failed`, fulfillment subtracts that refund's per-line amounts from `order_line_accounts.refunded_cents`, and resets `shipping_refunded` if that refund set it, under the `order_accounts` lock. The saga stays failed for Support. Coverages it cancelled stay cancelled (Error log names them). | §4.3 `fulfillment_sagas`: add `refund_lines jsonb` and `refunded_shipping bool`. §5.3 and §6 refund-failed row. | Test: after a failure the same refund can be requested again up to the paid amount. |

### 3.9 Catalog (08-catalog)

| ID | Decision | Docs | Code and tests |
|---|---|---|---|
| D36 | **Archived is final for launch events.** The launch consumer never changes an `archived` product; only staff can un-archive. | §6 launch-consumer row, §8. | Test. |
| D37 | **Reviewer names: newest event wins.** `reviewer_names.updated_at` is set from the event's `OccurredAt`; an upsert applies only if it is newer. | §4.1 column, §6 consumer rows. | Test: `contact-changed` applied before `customer-registered` → the newer name stays. |

### 3.10 Notification (09-notification)

| ID | Decision | Docs | Code and tests |
|---|---|---|---|
| D38 | **Ops re-queue resets attempts.** | §8 line 216 → "set `pending`, `attempts = 0`, `next_attempt_at = now()`". Doc only. | None. |
| D39 | **Resend 429 honours `Retry-After` and isn't counted.** The next attempt is at `max(schedule step, Retry-After)`; without the header, the schedule step applies. There is no cap on uncounted 429s **[default]**; the `event_to_email_seconds` alert covers a long throttle. | §6 sender steps. | Tests with the fake provider. |
| D40 **[default]** | **`Notification:Email:From` is validated at startup.** A malformed address fails start-up and names the key, never the value. | None (internal config validation). | Test. |

### 3.11 Local stack

| ID | Decision | Docs | Code and tests |
|---|---|---|---|
| D41 | **MinIO in dev compose.** Pinned `alpine/minio:RELEASE.2025-10-15T17-29-55Z` (the image the catalog tests use), plus a one-shot bucket-init container, using catalog's bucket config names. | None in `doc/` (HLD §9.5 already says "MinIO locally"). | `docker compose -f deploy/compose/docker-compose.yml config --quiet` exits 0. The stack is not started. |

## 4. Doc backlog (approve or strike per line)

All items below are approved by the owner's bulk approval unless struck in review. **Approving this document is the AGENTS.md §9 approval for every `doc/` edit in §3 and §4**; nothing under `doc/` is touched before that. The header stamp date is that approval date.

**Upstream first:** product-design edits, then HLD, then LLD. The exact wording sits in the source files named: specs and plans under `docs/superpowers/`, and PR bodies (read with `gh pr view <n>`) for G5, F15–F18, O15, O16, P15, N6 and L10. The builder copies the wording and adjusts it only to merge with §3. Every item is to-apply except where marked; for I8–I10 the builder checks that `253b629` applied them and applies any that are missing.

| ID | Source | Item(s) | Notes |
|---|---|---|---|
| G1–G4 | gateway spec §9 | 1–4 | G1 merged with D12. |
| G5 | PR #2 owner item 2 | 07 §6.2 sliding window: the trim is inclusive, window `(now − W, now]` | |
| ID1–ID6 | identity spec §9 | 1–6 | |
| C1–C12 | catalog spec §9 | 1–12 | |
| I1–I7 | inventory spec §9 | already applied in `253b629` | Listed only for completeness. |
| I8–I10 | inventory plan | 8–10 | Already applied in `253b629`; check. |
| I11 | inventory plan | 11 | **Superseded by D16.** |
| F1–F9 | fulfillment spec §9 | 1–9 | **F8 is superseded by F11.** F4 and D28 both add to 00-reliability §7.4 "Applies to". |
| F10–F14 | fulfillment plan | 10–14 | |
| F15–F18 | PR #6 | 15–18 | |
| O1 | order spec §9 | 1 | **DESIGN CHANGE:** new **PD-ADR-009** "No price-change flag in the cart"; CHK-R18 edit; 03-order §5.1 and §8. |
| O2 | order spec §9 | 2 | **Product rule:** new CHK-R19; 03-order §6.4. |
| O3–O9 | order spec §9 | 3–9 | O6 merged with D20. O7: a doc edit to 00-reliability §7.4 and 03-order, **no ADR [default]**. |
| O10–O14 | order plan | 10–14 | O10 is the reason D17 and D21 use. |
| O15 | PR #7 | `IDEMPOTENCY_KEY_REUSED` on a resumed checkout with a different request; the launch `buyable` rule; the release loop runs until an empty batch; zero-cent refund lines accepted | |
| O16 | PR #7 | Input checks: negative `version` and `parent_line_no` on non-plans → `VALIDATION_FAILED` | |
| P1–P9 | payment spec §9 | 1–9 | P1 merged with D24. |
| P10–P14 | payment plan | 10–14 | |
| P15 | PR #8 | 04-payment §4: FKs `refunds.payment_id` and `disputes.payment_id` → `payments` (Restrict) | |
| N1–N4 | notification spec §9 | 1–4 | |
| N5 | notification plan | 5 | |
| N6 **[default]** | PR #9 | 09-notification §5.2 note: producers write UTC `OccurredAt` | |
| L1–L6 | launch spec §9 | 1–6 | |
| L7–L9 | launch plan | 7–9 | |
| L10 | PR #11 | 01-launch §4.1: index `ix_launch_skus_launch_id_product_id` for the composite FK | |
| S1 | PR #1 | `docs/superpowers` only: shared-library spec lines 134 and 144 and plan Review Focus 1 / Task 6 describe the old relay and interceptor | Not under `doc/`. |

New ADRs in this project: PD-ADR-009, HLD-ADR-019, LLD-ADR-011 … 014. Every ADR index is updated.

## 5. Defects, flaky tests and test gaps (from the PR bodies)

All of these are internal, so no doc edit is needed unless one is stated. Each fix comes with a test that fails before it, unless the item is itself a test.

**Bloom.Shared (#1)**
- S-1 Idempotency re-takeover: call `ChangeTracker.Clear()` before the re-run `apply`.
- S-2 Relay: one confirm deadline per pass (not per row). A failed channel re-open reports a re-open error, not "no confirm". The R7 1 s pause rule is unchanged (owner rule).
- S-3 Tests: remove the lower-bound timing risk in `Loop_pauses_1s`; give the `MetricNamesTests` timeout a specific message.

**Gateway (#2)**
- GW-1 `Authorization_and_cookie_are_never_logged`: capture Trace level for that test.
- GW-2 F3 reconnect test: tell the state before the broker drop from the state after.
- GW-3 Remove the unused usings in AuthTests, EdgeLimitTests, RedisOutageTests and RevocationWorkerTests.
- GW-4 Bad route `Auth`/`Policy` metadata fails at startup. A missing `Gateway:Redis` or `Gateway:RabbitMq` fails at build time.
- GW-5 Load the Lua script once and call it with EVALSHA (`LuaScript.Prepare`/`Load`). Cache `TokenValidationParameters` and rebuild it only when the JWKS changes.
- GW-6 The degraded gauge also updates on a timer while the pod is idle. Log once on entering and once on leaving degraded mode. A `RedisServerException` (for example a Lua error) is a 500, not an outage.
- GW-7 Worker delivery handler: any other exception NACKs with requeue after a 1 s delay, logged, so the channel never stalls.

**Identity (#3)**
- ID-1 The stale comment at `Sessions/SessionService.cs:53`.
- ID-2 Concurrent revoke-alls: make `Version++` an atomic `UPDATE … SET version = version + 1`.
- ID-3 Tests: the exact 72 h invite expiry; `alg: none` and HS256 Google tokens; a tie split across an audit page boundary; TryRotate vs SignOut overlap; the untested metric labels.

**Catalog (#4)**
- C-1 Category rename refreshes its products in ascending id order (no deadlock with the launch consumer).
- C-2 Tests: parallel writes on the race paths named in the catalog review ledger; the plan-lifecycle cache test; the single-branch cases listed there.

**Inventory (#5, #10)**
- IN-1 Order locks: take them once, in ascending order-id order, after the last `RunAsync` in the transaction (not per call).
- IN-2 Stock-monitor test: `StopAsync` the hosted monitor and drive `RunOnceAsync` by hand.
- IN-3 Reserve shortage followed by a PK race → mapped, not a raw `DbUpdateException`.
- IN-4 Concurrent invalid receipts with crossed IMEIs: validate the IMEI uniqueness in a fixed order → `RECEIPT_INVALID`, not a deadlock 500.
- IN-5 `ledger_entries_total` and the backorder metrics count after commit.
- IN-6 Release's second `LockAsync` keeps ascending order (lock the union).
- IN-7 A second, distinct non-serialized lost-in-transit command for the same shipment units doesn't double-count `lost` (guard on the allocation's lost quantity).
- IN-8 Tests: reship `ref_type` on fulfillment ledgers; a wrong-SKU serial in a customer return; a refused serialized decrease leaves the serial `in_stock`; a populated IMEI in `GetSerial`; the packing edge asserts.
- IN-9 (#10) Point the Inventory and Notification test seeds that write a raw `UtcNow` at `t.Clock`.

**Fulfillment (#6)**
- F-1 `ListLabelsAsync` parses list entries leniently and validates only the matched label.
- F-2 Label worker: a DB failure during rollback or failure bookkeeping moves the row back in line (next attempt) and never pins it first.
- F-3 Compute the label-retry delay from the time after the Shippo call.
- F-4 Verify that the webhook secret is absent from the "Request starting/finished" logs (`Microsoft.AspNetCore` is set to Warning); add a test. Fix it if the test fails.
- F-5 Tests: `RETURNED` on a `possibly_lost` shipment; `ShippoClient` pagination and its stop rule; forwarding of `x-bloom-user-type` / `x-bloom-role`; pass-through of non-bloom inventory errors (`UNAVAILABLE`); per-row isolation in the sweeps.

**Order (#7)**
- O-1 `Pricing.Compute`: when the eligible `sum = 0`, the discount is 0 for every line (no division).
- O-2 One poison order no longer rolls back its sweep batch: each order runs in its own savepoint, and a failing order is logged and skipped until the next tick.
- O-3 `Upstreams.IsRejection` reuses the guard's stored-status list.
- O-4 Consumer tests serialize with `BloomJson`. `AccountTests` pins line order.

**Payment (#8)**
- P-1 Executor logs: no "retrying" when the guarded update hit 0 rows; the 5th-failure log carries the exception.
- P-2 `Domain/Transitions` no longer imports `Worker` (move `Backoff`).
- P-3 Review Focus 2 and 5 tests force the lock-wait interleaving.
- P-4 Transition metrics and error logs are recorded after commit.
- P-5 Reconciler: call Stripe before opening the transaction; batch cap 100 **[default]**.
- P-6 `PaymentOptions` (and the same check in order): reject a relative `/v1` that parses as an absolute file URL on Unix.
- P-7 `StripeClient`: an empty page with `has_more`, an out-of-range timestamp or a null id → `ProviderException`.
- P-8 `CreatePayment`: the lock release and Tx 2 use a non-cancelled token.
- P-9 A `succeeded` payment whose Tx 2 never ran returns its stored checkout URL if it has one (else `""`, documented in the code).
- P-10 Webhook: read the body through a pooled buffer, not a new 256 KB array per request.
- P-11 Tests: the `http_<status>` fallback, network errors, `canceled`/`requires_action` refunds, failed nightly fetches, and the indirect cancel no-op.

**Notification (#9)**
- N-1 The stale comment at `Worker/EmailSender.cs:113`.
- N-2 Test: the lease is lost during the job's own send.
- N-3 URL checks accept `http`/`https` only.
- N-4 Redis connects at startup, not synchronously on first use.
- N-5 A test for the SMTP timeout path.
- N-6 A `notifications` purge failure no longer skips the `email_jobs` purge.
- N-7 WorkerTests: replace the wall-clock budgets with polling on the fake clock.

**Launch (#11)**
- L-1 `PassPrefix` constant in `RedisKeys`, used by the admission script.
- L-2 Move `Moved` and `HoldCounters` out of `OrderPaidConsumer.cs`; one shared `Locked` record.
- L-3 Comment wording: "except a 5 s cache miss"; the stale `TestLaunch` comment.
- L-4 The admin view after commit is read in one read-only transaction (one snapshot).
- L-5 Tests: audit details for adds; the `AddLaunchSku` stage re-check; the list tie-break; join and status over HTTP; a Redis failure at the pop; forced interleaving in `Raising_allocation…`; the 29-minute stuck-hold gauge asserted directly.
- L-6 Test nits: `CommandCounter` sync overrides; widen the catalog-timeout range; replace the two vacuous `InRange` assertions.

## 6. CI fix: every open PR green

Two causes:
- **Test-clock precision** (#3–#8 red). PR #10's fix sits above them in the stack.
- **Package advisory** (#9 and #11 red). `Microsoft.AspNetCore.SignalR.StackExchangeRedis` 10.0.14 has GHSA-wr8v-9x5q-ggcm (patched in 10.0.15), and NuGet audit fails restore (`NU1903` as error). The vulnerable package exists only from `feat/notification` upward.

Owner-approved approach: move each fix down to the branch that introduced the code, then rebase the stack.

| Branch (PR) | New commit |
|---|---|
| `feat/identity` (#3) | PR #10's seed change for Identity (host and `ShellTests`) and Gateway. `feat/api-gateway` (#2) is green and not touched. |
| `feat/catalog` (#4) | Catalog seed. |
| `feat/inventory` (#5) | Inventory seed and the `OrderPaidTests.cs:87` change. |
| `feat/fulfillment` (#6) | Fulfillment seed. |
| `feat/order` (#7) | Order seed. |
| `feat/payment` (#8) | Payment seed. |
| `feat/notification` (#9) | Notification seed; bump every `10.0.14` package in `Directory.Packages.props` to `10.0.15` (six packages, all on nuget.org). |

- Each commit is applied on top of its branch, which is then built and its tests run (§8 rule). Each branch above is rebased onto the one below, in stack order: `feat/catalog` … `feat/notification`, `fix/test-clock-precision`, `feat/launch`, `fix/pr-followups`. Each is pushed with `--force-with-lease` (owner-approved for `feat/identity` up to `fix/pr-followups`).
- When `fix/test-clock-precision` is rebased, its commits become empty and are dropped. #10 is then left with no diff. The PR is not closed (owner's call). A note is added to #10's body saying where its changes moved **[my call]**.
- **Order of work [my call]:** this runs first in execution, because every later commit would otherwise have to be rebased too.
- Acceptance: CI is green on #2–#11 and on the new PR. A remaining red that the clock or package cause doesn't explain is a stop-and-report.

## 7. Out of scope (stays recorded)

- Catalog read-then-SET race and cleanup-vs-confirm race: accepted by 08-catalog §7 and §6.7.
- Notification deleted-account cases: the owner chose "no tombstone" in the notification plan.
- SMTP TLS: SMTP is Mailpit-only locally; production uses Resend (HLD §7.3).
- Identity timing side channels R15 and R19: within ID-05 as tested.
- Catalog search thumbnail: returns no SKU, so no breach.
- Launch: stateless validation before `NOT_FOUND` stays. The `StateCache` drop after cleanup stays (the brief mandated it).
- Gateway G-11 load test: cluster only.
- Items that are only in the review ledgers and not named in a PR body (for example order's ~60 polish notes).
- **New finding, parked:** an order whose total is 0 (100 % coupon and free shipping) can't be paid (Stripe's minimum amount), and a $0 cancellation refund is rejected by payment (`AmountCents <= 0`). This needs a product decision.

## 8. Constraints

- AGENTS.md: nothing under `doc/` is edited beyond §3 and §4. Upstream first. Accepted ADRs are superseded, not rewritten. IDs are never renumbered.
- CLAUDE.md: don't run apps or servers. Docker only for Testcontainers. Clean up containers after test runs. Never run `colima`.
- Run tests one project at a time, or with `--max-parallel-test-modules 1`, locally.
- Branch `fix/pr-followups`, PR base `feat/launch`. Never merge PRs.
- **One PR** for §3–§5 **[my call]**. `doc/` edits go in their own commits, one per layer (product, HLD) and per LLD document, so the owner can review or revert a line of §4 alone. Code for each decision or service is in its own commits.

## 9. Left to the builder

- File layout, helper names and test structure.
- Migration names. Every schema change (D16, D26, D35, D37) is a new EF migration in its service.
- The exact Npgsql classifier list for D3, kept in one place in `Bloom.Shared`.
- The task order inside the plan, after §6 runs first.
