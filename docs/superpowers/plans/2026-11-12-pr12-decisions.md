# PR #12 Decisions Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Put the owner's decisions on PR #12's parked items 1–12, the D27 doc edit and residuals R1–R4 into `doc/` and code, each with its test.

**Architecture:** Docs first, upstream first (product → HLD → LLD). Then code: Shared (R3 after-commit, item 11 DLX), then services in stack order: Gateway (R1, R2), Identity (5), Inventory (4, 9), Fulfillment (6, 7), Order (1, R4), Payment (R3 adoption), Launch (10). Each decision ships with the test that pins it; a behaviour fix's test fails before the fix.

**Tech Stack:** .NET 10, ASP.NET Core gRPC, EF Core 10 + Npgsql, RabbitMQ.Client (RabbitMQ 4.1 quorum queues), StackExchange.Redis, Bloom.Shared, xUnit v3 (Microsoft Testing Platform), Testcontainers, `FakeTimeProvider`.

**Spec:** `docs/superpowers/specs/2026-11-12-pr12-decisions-design.md` (approved 2026-11-12). Items are cited by spec ID (`1`…`12`, `D27`, `R1`…`R4`) and builder rulings by `§5a`. The spec row is the requirement: read it before you start. This plan adds file locations, signatures, test names and rulings.

## Global Constraints

- **AGENTS.md §9.** Under `doc/`, edit only what spec §4 lists. Spec approval is the owner's approval for exactly those edits. IDs are never renumbered; accepted ADRs are never rewritten.
- **Upstream first.** Tasks 1–2 (docs) run before any code task.
- **New IDs, exactly these:** rule `CHK-R20`, ADR `LLD-ADR-015`, error reason `ORDER_TOTAL_TOO_LOW`, metric result `order_total_too_low`, table `order_returns`. Nothing else new is visible outside a service.
- **Header stamp.** Every changed `doc/` document has `| **Status** | Approved (2026-11-12) |` (most already do; only the Status line is touched).
- **Commits.** Doc commits: one per layer for product and HLD, one per LLD document; LLD-ADR-015 goes in the commit of `00-reliability.md`. Code: one commit or more per item. Never mix `doc/` and code in one commit. Prefixes: `docs(product)`, `docs(hld)`, `docs(lld): <doc>`, `feat|fix|refactor|test(<svc>)`. Every commit ends with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.
- No new NuGet packages, no version changes.
- Schema changes are new EF migrations in their service (only Order, item 1). `SchemaTests` must still pass.
- Additive message fields: none in this plan. Messages are unchanged.
- `dotnet build -c Release` gives 0 warnings (`TreatWarningsAsErrors`).
- **Local runs (CLAUDE.md).** Never run `colima`, `dotnet run` or `dotnet watch`. One test project at a time: `dotnet test --project src/<Svc>/Bloom.<Svc>.Tests [--filter-class '*<Class>']`. If the Docker daemon is unreachable, stop and report. After each run, `docker ps -a` shows no leftover Testcontainers containers.
- Existing guarantees stay: no double apply, no oversell, no over-refund, no lost message (spec §2 anti-goals). PR #12's tests stay green.

## Review Focus

1. **`fulfillment.return-closed.v1` or `return-requested.v1` for an order this service doesn't know** (the existing test sends one). `order_returns.order_id` has a foreign key, so a blind insert throws, retries 5× and dead-letters. Expected: a no-op, as today. Test in Task 11.
2. **A `ReserveStock` replay for an order that was reserved, then released.** Its allocations still exist (status `released`), so the "same lines already held → ok" comparison would answer ok. Expected: `ORDER_RELEASED`; the tombstone check comes before the held comparison inside the attempt. Test in Task 7.
3. **A Shippo `status_date` in the future, or before the pack task was created** (carrier clock skew). Expected: clamped to [task `created_at`, now] (§5a 7); never a negative histogram sample. Test in Task 10.
4. **An after-commit action that throws, followed by another action** (Inventory used to propagate). Expected: warning logged, the next action still runs, the commit is not undone and the caller sees success. Test in Task 3.
5. **A broker-dead-lettered message in the DLQ** (headers `x-death`, no `x-failure-reason`). Expected: listed by the DLQ admin with an empty reason and replayable (§5a 11). Test in Task 4.

## Rulings in this plan (builder's calls the spec left open)

- **P1 Workspace.** Execute in the main worktree `/Users/datnguynx/Project/Bloom/Bloom_Server` on the existing branch `fix/pr12-decisions` (it already holds the spec commit `49ed5a2`). PR base: `fix/pr-followups`.
- **P2 After-commit API (R3).** `Bloom.Shared/Persistence/AfterCommit.cs` holds three types: `public interface IAfterCommitContext { AfterCommitActions AfterCommit { get; } }`; `public sealed class AfterCommitActions` with `public void Add(Func<Task> action)` (one method, no `Action` overload); `public sealed class AfterCommitInterceptor(ILogger<AfterCommitInterceptor> logger) : DbTransactionInterceptor` overriding `TransactionStartedAsync` (clear), `TransactionCommittedAsync` (take and clear the list, then run each in order; actions take no token, so a client cancel after the commit never stops one (§5a R3); a throw → `LogWarning`, continue), `TransactionRolledBackAsync` (clear). Each app registers it as a singleton and adds it with `AddInterceptors(sp.GetRequiredService<AfterCommitInterceptor>())`.
- **P3 Order lock key (item 4).** `StockLocks.LockOrderAsync(Guid orderId, CancellationToken ct)` runs `SELECT pg_advisory_xact_lock({StockLocks.Key(orderId)})`. Same fold as the SKU key; a collision with a SKU key (2⁻⁶⁴) only over-serializes, and PostgreSQL's deadlock detector covers the crossed-order case. Mark it with a `ponytail:` comment.
- **P4 Primary Redis server (R2).** `EdgeStore` loads the script on `redis.GetServers().First(s => s.IsConnected && !s.IsReplica)`. The test asserts the chosen server is not a replica on the single-node test Redis (a primary+replica fixture is out of proportion; the `ponytail:` comment names that ceiling).
- **P5 Delivery-limit test (item 11).** The test applies a RabbitMQ policy `delivery-limit = 1` to its own consumer queue with `rabbitmqctl set_policy … --apply-to quorum_queues`, then NACK-requeues past it. If RabbitMQ 4.1 refuses the policy, keep only the arguments test (spec item 11) and record `Ruling:` in the ledger.
- **P6 Out-of-scope findings** seen during a task go to the ledger as `Parked: …`, not fixed.

---

## File Structure

New files only; everything else is a change to an existing file named in its task.

```
doc/low-level-design/adr/LLD-ADR-015-consumer-queue-dlx.md       # item 11
src/Shared/Bloom.Shared/Persistence/AfterCommit.cs              # R3 (P2)
src/Shared/Bloom.Shared.Tests/AfterCommitTests.cs               # R3
src/Order/Bloom.Order/Data/Migrations/<ts>_OrderReturns.cs      # item 1
```

Deleted: `src/Inventory/Bloom.Inventory/Data/AfterCommitInterceptor.cs`, `src/Notification/Bloom.Notification/Data/AfterCommitInterceptor.cs` (R3).

---

### Task 1: Docs — product, HLD, maps, 00-reliability, LLD-ADR-015

**Items:** 2, 11, 12, R4 (product rule), spec §4 product/HLD/LLD-index/00-reliability rows.

**Files:**
- Modify: `doc/product-design/00-overview.md` (≈ line 142), `doc/product-design/05-checkout-payment.md`
- Modify: `doc/high-level-design/high-level-design.md` (≈ line 401)
- Modify: `doc/README.md` (map lines 27/30/35), `doc/low-level-design/README.md` (line 11 and component index)
- Create: `doc/low-level-design/adr/LLD-ADR-015-consumer-queue-dlx.md`; Modify: `doc/low-level-design/adr/README.md`
- Modify: `doc/low-level-design/00-reliability.md` (header, §6 consumer-queue row, §7.3 step 8 line 233)

- [ ] **Step 1:** Product commit. 00-overview: PD-ADR-009 row in the ADR list (copy the format of the PD-ADR-008 row; title from `doc/product-design/adr/PD-ADR-009-*.md`). 05-checkout-payment: CHK-R20 with the exact text "An order's total must be at least $0.50, the card processor's minimum. Checkout refuses to start otherwise." next to the pricing/payment rules. Commit `docs(product): PD-ADR-009 index row, CHK-R20`.
- [ ] **Step 2:** HLD commit. HLD-ADR-019 row (format and title from its ADR file). Commit `docs(hld): HLD-ADR-019 index row`.
- [ ] **Step 3:** LLD index commit. `doc/README.md` map: `PD-ADR-001 … 009`, `HLD-ADR-001 … 019`, `LLD-ADR-001 … 015`. `low-level-design/README.md`: line 11 and the component index list LLD-ADR-011 … 015 against the components that cite them (011 reliability, 012 launch, 013 identity, 014 inventory, 015 reliability). Commit `docs(lld): README ADR lists`.
- [ ] **Step 4:** LLD-ADR-015 "Consumer queues dead-letter at the delivery limit", in the LLD-ADR-014 format: Status Accepted, Date 2026-11-12, Deciders Project owner, Related LLD-ADR-002, HLD-ADR-019, LLD-ADR-009, 00-reliability §6. Context: quorum queues drop a message after the default delivery limit (20) when no dead-letter target is set; a message redelivered during a pause (HLD-ADR-019) or after crashes is lost. Options: (1) leave as is, (2) raise/disable the limit, (3) dead-letter to `<queue>.dlq`. Decision: 3, limit stays 20; consequences: replayable via LLD-ADR-009, carries `x-death` instead of `x-failure-reason`/`x-failed-at` (§5a 11), existing queues must be re-declared. Add its row to `adr/README.md` (component `reliability`, Accepted, 2026-11-12).
- [ ] **Step 5:** 00-reliability: header decision records list includes 011 and 015; §6 "Consumer queue" arguments row gains `x-dead-letter-exchange = ""`, `x-dead-letter-routing-key = <queue>.dlq` and the LLD-ADR-015 link; §7.3 step 8: "The pause is per process" → "The pause is per consumer queue (each queue pauses on its own outage and probes on its own)". Commit Steps 4–5 together: `docs(lld): 00-reliability consumer DLX (LLD-ADR-015), pause scope`.
- [ ] **Step 6:** Verify: `git diff fix/pr-followups --stat -- doc/` lists only the files above; `grep -rn "LLD-ADR-015" doc | wc -l` ≥ 4.

### Task 2: Docs — service LLDs

**Items:** 1, 3, 4, 5, 6, 7, 8, 9, D27, R4 (spec §4 LLD rows for 01, 02, 03, 05, 06).

**Files:** `doc/low-level-design/01-launch.md`, `02-inventory.md`, `03-order.md`, `05-fulfillment.md`, `06-identity.md`.

- [ ] **Step 1:** One commit per document, each exactly as spec §4 words it:
  - `01-launch.md`: header decision records include LLD-ADR-012 if missing; line 307 drop "and the settlement-side close". Commit `docs(lld): 01-launch`.
  - `02-inventory.md`: header includes LLD-ADR-014 if missing; IR-05 new text (spec §4 verbatim); §7 new row (spec §4 text plus the lock order of §5a 4: order lock → SKU locks → allocator's order lock); §8 new row "Repeated lost-in-transit for a serialized ref → no-op once its units are counted". Commit `docs(lld): 02-inventory`.
  - `03-order.md`: §4 drop `open_returns`, add `order_returns(order_id, return_id, closed_at timestamptz null)`, PK `(order_id, return_id)`, FK to `orders`; §5 row (≈ line 208) → insert / mark closed per `returnId`; error table row `ORDER_TOTAL_TOO_LOW` | `FAILED_PRECONDITION` | total below 50 cents (CHK-R20); §6.2 step 4 and §6.3 step 8 cite CHK-R20; line 376 "`open_returns > 0`" → "an `order_returns` row with `closed_at` null". Commit `docs(lld): 03-order`.
  - `05-fulfillment.md`: line 286 D27 wording (spec §4); §8 new reship row; §6.3 line 254 handover time = carrier's `status_date` (processing time if absent). Commit `docs(lld): 05-fulfillment`.
  - `06-identity.md`: header includes LLD-ADR-013 if missing; §8 new address-vs-delete row. Commit `docs(lld): 06-identity`.
- [ ] **Step 2:** Verify: `grep -n "open_returns" doc/low-level-design/03-order.md` returns nothing; `grep -rn "CHK-R20" doc | wc -l` ≥ 3; `git diff fix/pr-followups --stat -- doc/` lists only Task 1 + Task 2 files.

### Task 3: Shared after-commit mechanism; Inventory and Notification adopt it (R3)

**Files:**
- Create: `src/Shared/Bloom.Shared/Persistence/AfterCommit.cs`, `src/Shared/Bloom.Shared.Tests/AfterCommitTests.cs`
- Delete: `src/Inventory/Bloom.Inventory/Data/AfterCommitInterceptor.cs`, `src/Notification/Bloom.Notification/Data/AfterCommitInterceptor.cs`
- Modify: `src/Inventory/Bloom.Inventory/Data/InventoryDbContext.cs:20-31`, `InventoryApp.cs:39`, `Domain/Allocator.cs:59`, `Domain/Ledger.cs:24`
- Modify: `src/Notification/Bloom.Notification/Data/NotificationDbContext.cs:16`, `NotificationApp.cs:43,51`, `Consumers/EventConsumer.cs:140,175,193`

**Interfaces:**
- Produces (P2): `IAfterCommitContext`, `AfterCommitActions.Add(Func<Task>)`, `AfterCommitInterceptor` in namespace `Bloom.Shared.Persistence`. Task 13 uses them for Payment.

- [ ] **Step 1: Failing tests** in `AfterCommitTests` (Postgres from the existing Shared test containers; a tiny test `DbContext` implementing `IAfterCommitContext`, interceptor added):
  - `Runs_after_commit` — an action added inside a transaction has not run before `CommitAsync`, has run once after.
  - `Dropped_after_rollback` — added, `RollbackAsync` → never runs.
  - `Later_transaction_does_not_run_rolled_back_actions` — tx A adds X, disposed without commit; tx B adds Y, commits → only Y ran.
  - `Throwing_action_is_logged_and_the_next_still_runs` (Review Focus 4) — actions [throw, Y]; `CommitAsync` completes without throwing; Y ran; one Warning log.
- [ ] **Step 2:** `dotnet test --project src/Shared/Bloom.Shared.Tests --filter-class '*AfterCommitTests'` → fails to compile (types missing).
- [ ] **Step 3:** Implement `AfterCommit.cs` per P2. Run Step 2 → PASS.
- [ ] **Step 4:** Inventory: `InventoryDbContext : IAfterCommitContext` with `public AfterCommitActions AfterCommit { get; } = new();` (removes `_afterCommit`, `AfterCommit(Action)`, `RunAfterCommit`, `DropAfterCommit`); the two call sites become `db.AfterCommit.Add(() => { metrics.…; return Task.CompletedTask; })`; `InventoryApp` registers the shared interceptor (P2). Notification: `NotificationDbContext : IAfterCommitContext`, `AfterCommit` becomes `AfterCommitActions`; `NotificationApp` uses the shared type. Delete both service interceptors. Inventory's propagate-on-throw is gone (§5a R3).
- [ ] **Step 5:** `dotnet build -c Release` → 0 warnings; `dotnet test --project src/Inventory/Bloom.Inventory.Tests` and `dotnet test --project src/Notification/Bloom.Notification.Tests` → PASS.
- [ ] **Step 6:** Commit `refactor(shared): one after-commit mechanism; inventory, notification use it (R3)`.

### Task 4: Consumer queues dead-letter at the delivery limit (item 11)

**Files:**
- Modify: `src/Shared/Bloom.Shared/Messaging/TopologyInitializer.cs:10,35`
- Test: `src/Shared/Bloom.Shared.Tests/TopologyTests.cs`

**Interfaces:**
- Produces: `internal static IDictionary<string, object?> ConsumerQueueArguments(string consumerQueue)` → `{ "x-queue-type": "quorum", "x-dead-letter-exchange": "", "x-dead-letter-routing-key": MessageNames.DeadLetterQueue(consumerQueue) }`. Retry and DLQ queues keep their arguments.

- [ ] **Step 1: Failing tests:**
  - `Consumer_queue_arguments_match_section_6` — `ConsumerQueueArguments("s.h")` equals the dictionary above with routing key `MessageNames.DeadLetterQueue("s.h")`.
  - In `T07_topology_declares_retry_queues_with_ttl_and_dlq`: the consumer queue's `x-dead-letter-exchange` = `""` and `x-dead-letter-routing-key` = its DLQ name.
  - `Message_past_delivery_limit_lands_in_dlq` (P5) — policy `delivery-limit 1` on the test queue; publish one message; `BasicGet` + `BasicNack(requeue: true)` until it's gone; the DLQ holds exactly that message.
  - `Broker_dead_lettered_message_is_listed_and_replayable` (Review Focus 5) — the message from the previous setup is listed by the DLQ admin (as in `DeadLetterAdminTests`) with reason `""`, and replay puts it back on the consumer queue.
- [ ] **Step 2:** `dotnet test --project src/Shared/Bloom.Shared.Tests --filter-class '*TopologyTests'` → the new assertions FAIL.
- [ ] **Step 3:** Declare consumer queues with `ConsumerQueueArguments(consumer.Queue)`. Run Step 2 → PASS; then the full `Bloom.Shared.Tests` → PASS.
- [ ] **Step 4:** Commit `feat(shared): consumer queues dead-letter at the delivery limit (item 11, LLD-ADR-015)`.

### Task 5: Gateway Redis probe and outage predicate (R1, R2)

**Files:**
- Modify: `src/Gateway/Bloom.Gateway/Observability/RedisDegradedProbe.cs:20`, `Edge/EdgeMiddleware.cs:133`, `Limits/EdgeStore.cs:57,64`
- Test: `src/Gateway/Bloom.Gateway.Tests/RedisOutagePredicateTests.cs`, `RedisOutageTests.cs`, `EdgeLimitTests.cs`

**Interfaces:**
- Produces: `internal static IServer Primary(IConnectionMultiplexer redis)` in `EdgeStore` (P4).

- [ ] **Step 1: Failing tests:**
  - `RedisOutagePredicateTests.Cases` add `MISCONF …`, `OOM …`, `NOREPLICAS …` → `true`; add `NOPERM …` → `false`.
  - `Probe_treats_a_non_outage_server_error_as_up` (R1) — a Redis ACL user without `+ping` (`ACL SETUSER probe on >pw ~* +@all -ping`); the probe connected as that user ticks (advance the fake clock past 5 s): `gateway_redis_degraded` = 0 and the probe's `ExecuteTask` is not faulted.
  - `Script_loads_on_the_primary` (R2, P4) — `EdgeStore.Primary(redis).IsReplica` is false.
- [ ] **Step 2:** `dotnet test --project src/Gateway/Bloom.Gateway.Tests` → the new cases FAIL.
- [ ] **Step 3:** `OutageCodes` gains `"MISCONF"`, `"OOM"`, `"NOREPLICAS"`. Probe: after the outage catch, `catch (RedisServerException) { metrics.SetRedisDegraded(false); }`. `EdgeStore` loads with `Primary(redis)`.
- [ ] **Step 4:** Step 2 → PASS. Commit `fix(gateway): probe and predicate Redis errors, script on the primary (R1, R2)`.

### Task 6: Address upsert racing an account delete (item 5)

**Files:**
- Modify: `src/Identity/Bloom.Identity/Services/AccountService.cs:138`
- Test: `src/Identity/Bloom.Identity.Tests/AddressTests.cs`

- [ ] **Step 1: Failing test** `Upsert_waiting_behind_delete_is_not_found` — a test transaction locks the account row (`SELECT … FOR UPDATE`); start a create `UpsertAddress` (not awaited); wait until `pg_locks` shows an ungranted lock; in the test transaction set `status = 'deleted'` and delete the account's addresses, commit; the upsert fails `NOT_FOUND` and the account has 0 address rows. Same with an update that sets `is_default`.
- [ ] **Step 2:** `dotnet test --project src/Identity/Bloom.Identity.Tests --filter-class '*AddressTests'` → FAIL (an address row exists).
- [ ] **Step 3:** Where the account row is locked, read `status` in the same statement (`SELECT status AS "Value" FROM accounts WHERE id = … FOR UPDATE`); `deleted` or no row → `IdentityErrors.Error(IdentityErrors.NotFound, "No such account.")`.
- [ ] **Step 4:** Step 2 → PASS; full Identity tests PASS. Commit `fix(identity): address upsert re-checks the account under its lock (item 5)`.

### Task 7: Per-order advisory lock: release vs reserve / paid (item 4)

**Files:**
- Modify: `src/Inventory/Bloom.Inventory/Domain/StockLocks.cs`, `Services/StockService.cs:14-35,73-78`, `Consumers/ReleaseReservationConsumer.cs:20-30`, `Consumers/OrderPaidConsumer.cs:18-25`, `Domain/Allocator.cs:16-17` (IN-1 comment, §5a 4)
- Test: `src/Inventory/Bloom.Inventory.Tests/ReleaseTests.cs`

**Interfaces:**
- Produces (P3): `public Task LockOrderAsync(Guid orderId, CancellationToken ct)` on `StockLocks`. `StockService` gains a `StockLocks` constructor parameter.

- [ ] **Step 1: Failing tests** (each: a test transaction takes `pg_advisory_xact_lock(StockLocks.Key(orderId))` and inserts the tombstone, as a release blocked after its insert; start the call; wait until `pg_locks` shows an ungranted advisory lock; commit the test transaction):
  - `Reserve_racing_release_waits_and_gets_order_released` — `ReserveStock` → `ORDER_RELEASED`; no allocation and `reserved` unchanged.
  - `Paid_racing_release_waits_and_creates_nothing` — `order.paid.v1` (regular and pre-order cases) → no allocation.
  - `Reserve_replay_after_release_is_order_released` (Review Focus 2) — reserve ok, release, reserve again with the same lines → `ORDER_RELEASED`.
- [ ] **Step 2:** `dotnet test --project src/Inventory/Bloom.Inventory.Tests --filter-class '*ReleaseTests'` → the race tests FAIL.
- [ ] **Step 3:** Implement:
  - `StockLocks.LockOrderAsync` per P3.
  - `ReleaseReservationConsumer`: `if (reshipId is null) await locks.LockOrderAsync(orderId, ct);` as the handler's first statement, **before** `CreateSavepointAsync`, so rolling back to the savepoint never frees it.
  - `OrderPaidConsumer`: `LockOrderAsync` before the tombstone check.
  - `StockService`: remove the tombstone check from `ReserveStock`; inside `CommitAsync`, right after each attempt's `BeginTransactionAsync` and only when `kind == AllocationKinds.Regular`: `LockOrderAsync`, then the tombstone check (`ORDER_RELEASED`), then the held comparison (§5a 4). `CommitForReship` doesn't take it.
  - `Allocator` IN-1 comment: the new single-bigint order lock is taken before any SKU lock; nothing holding the allocator's two-int lock waits on it, so no cycle.
- [ ] **Step 4:** Step 2 → PASS; full Inventory tests PASS. Commit `fix(inventory): per-order lock orders release against reserve and paid (item 4)`.

### Task 8: Repeated lost-in-transit for a serialized ref (item 9)

**Files:**
- Modify: `src/Inventory/Bloom.Inventory/Consumers/LostInTransitConsumer.cs:42-43`
- Test: `src/Inventory/Bloom.Inventory.Tests/FulfillmentConsumerTests.cs`

- [ ] **Step 1: Failing tests:**
  - `Second_lost_in_transit_for_serialized_ref_is_noop` — ship a serialized ref, consume `mark-lost-in-transit` twice with distinct message ids; the second completes (not dead-lettered); `lost`, serial statuses and ledger row count are unchanged by it.
  - `Lost_in_transit_serialized_never_counted_still_fails` — serialized ref with a shipped allocation but no shipped serial and no ledger entry → `PermanentFailureException`.
- [ ] **Step 2:** `dotnet test --project src/Inventory/Bloom.Inventory.Tests --filter-class '*FulfillmentConsumerTests'` → the first FAILS.
- [ ] **Step 3:** Serialized branch: no shipped serial and `counted.ContainsKey(a.SkuId)` → skip; otherwise throw as now. Update the class summary.
- [ ] **Step 4:** Step 2 → PASS. Commit `fix(inventory): repeated lost-in-transit for a serialized ref is a no-op (item 9)`.

### Task 9: Reship id per flag occurrence (item 6)

**Files:**
- Modify: `src/Fulfillment/Bloom.Fulfillment/Domain/Ids.cs:12-13`, `Services/SupportService.cs:201-208` (and the D27 doc comment at 213-217)
- Test: `src/Fulfillment/Bloom.Fulfillment.Tests/DeliveryFailureTests.cs` (existing uses at 259, 346, 425, 522-524)

**Interfaces:**
- Produces: `public static Guid ReshipId(Guid shipmentId, DateTimeOffset? dismissedAt)` = `NameBased(ReshipNamespace, $"{shipmentId:D}:{dismissedAt?.ToUnixTimeMilliseconds()}")` (empty after the colon when null).

- [ ] **Step 1: Failing tests:**
  - Update the id unit test: same inputs → same id; `null` vs a time → different; two times 1 ms apart → different; version 5.
  - `Reship_after_released_reship_commits_again_under_new_id` — flag lost; `ConfirmLost(reship)` whose inventory commit succeeds but the failure is dismissed before apply (D27 release sent for id A); re-flag; `ConfirmLost(reship)` → inventory sees a commit for id B ≠ A, the reship pack task exists with `RefId = B`, failure `resolved_reship` with `reship_id = B`.
- [ ] **Step 2:** `dotnet test --project src/Fulfillment/Bloom.Fulfillment.Tests --filter-class '*DeliveryFailureTests'` → compile failure, then FAIL.
- [ ] **Step 3:** `CommitReshipAsync` keeps the failure row it reads and passes its `DismissedAt` to `Ids.ReshipId`; `ReshipAsync` unchanged (gets the id from the guard). Update the existing call sites in tests to pass the failure's `DismissedAt` (null where never dismissed).
- [ ] **Step 4:** Step 2 → PASS; full Fulfillment tests PASS. Commit `fix(fulfillment): reship id per flag occurrence (item 6)`.

### Task 10: Scan-implied handover uses the carrier's date (item 7)

**Files:**
- Modify: `src/Fulfillment/Bloom.Fulfillment/Shippo/IShippo.cs:15`, `Shippo/ShippoClient.cs:62-64`, `Services/ShippoWebhook.cs:72-73,112-130`, `Domain/Handover.cs:18-27`
- Test: `src/Fulfillment/Bloom.Fulfillment.Tests/TrackingTests.cs`, `Infrastructure/FakeShippo.cs:58`

**Interfaces:**
- Produces: `public sealed record ShippoTracking(string Status, DateTimeOffset? StatusDate);` in `IShippo.cs`; `Task<ShippoTracking> GetTrackingStatusAsync(string trackingNumber, CancellationToken ct)`; `Handover.ApplyAsync(Shipment shipment, PackTask? task, DateTimeOffset now, DateTimeOffset handedOverAt, CancellationToken ct)` (`handed_over_at = handedOverAt`, everything else `now`). `FakeShippo.SetTracking(string number, string status, DateTimeOffset? statusDate = null)`.

- [ ] **Step 1: Failing tests** in `TrackingTests`:
  - `D32_scan_with_date_hands_over_at_that_date` — label_ready, `TRANSIT` with `status_date` = task `created_at` + 2 h, processed at + 3 h → `handed_over_at` = + 2 h; `time_to_handover_seconds` count 1, sum 7200.
  - `D32_scan_without_date_hands_over_at_processing_time` — no / unparsable `status_date` → `handed_over_at` = processing time; one sample.
  - `D32_scan_date_is_clamped` (Review Focus 3) — a future date → now; a date before the task's `created_at` → `created_at` (sample 0, never negative).
- [ ] **Step 2:** `dotnet test --project src/Fulfillment/Bloom.Fulfillment.Tests --filter-class '*TrackingTests'` → FAIL.
- [ ] **Step 3:** `ShippoClient` parses `tracking_status.status_date` (missing, null or unparsable → null). The webhook passes the tracking to `ApplyAsync`; the D32 branch computes `handedOverAt = clamp(StatusDate ?? now, task.CreatedAt, now)`, calls `handover.ApplyAsync(s, task, now, handedOverAt, ct)` and, for an outbound task, `metrics.HandedOver(s.Service, (handedOverAt - task.CreatedAt).TotalSeconds)` after it wins (as `WarehouseService.Handover.cs:61`). The warehouse HandOver passes `now` twice.
- [ ] **Step 4:** Step 2 → PASS; full Fulfillment tests PASS. Commit `fix(fulfillment): scan-implied handover at the carrier's status date (item 7)`.

### Task 11: Order tracks returns per return (item 1)

**Files:**
- Modify: `src/Order/Bloom.Order/Data/Entities.cs:135`, `Data/OrderDbContext.cs:41,46`, `Consumers/FulfillmentConsumers.cs:64-89`, `Services/InternalService.cs:22-23`
- Create: migration `OrderReturns` (`dotnet ef migrations add OrderReturns` in the Order project)
- Test: `FulfillmentEventTests.cs:227-289`, `AccountTests.cs:13-20,83-101`, `SchemaTests.cs:120`

**Interfaces:**
- Produces: entity `OrderReturn { Guid OrderId; Guid ReturnId; DateTimeOffset? ClosedAt; }`, `DbSet<OrderReturn> OrderReturns`, table `order_returns`, PK `(order_id, return_id)`, FK `order_id → orders(id)`. `OrderRow.OpenReturns` and `ck_orders_open_returns_non_negative` removed.

- [ ] **Step 1: Failing tests** (replace `Open_returns_count_up_and_down` and the D33 test's `OpenReturns` asserts with a helper `OpenReturnsAsync(t, orderId)` = count of rows with `closed_at IS NULL`):
  - `D33_requested_closed_closed_leaves_no_open_return` — requested → closed(expired) → closed(decided): 0 open; the decided still moves the status (existing assertions kept).
  - `Two_returns_one_closed_twice_leaves_one_open`.
  - `Closed_before_requested_ends_closed` — closed then requested (same id) → 0 open, one row with `closed_at` set.
  - `Return_events_bump_version_only_when_they_change_a_row` (§5a 1) — requested bumps `version`; a duplicate requested doesn't; first close bumps; second close doesn't.
  - `Return_events_for_unknown_order_are_noops` (Review Focus 1) — requested and closed for an unknown order complete without exception and insert nothing.
  - `AccountTests.Unsettled_statuses_and_open_returns`: seed an `order_returns` row (open) instead of `openReturns: 1` → unsettled; closed row only → settled.
  - `SchemaTests`: drop `o.open_returns` from the probe; assert `order_returns` exists with its PK and FK.
- [ ] **Step 2:** `dotnet test --project src/Order/Bloom.Order.Tests --filter-class '*FulfillmentEventTests'` → compile failure, then FAIL.
- [ ] **Step 3:** Implement:
  - Requested: `INSERT INTO order_returns (order_id, return_id) SELECT id, @r FROM orders WHERE id = @o ON CONFLICT DO NOTHING`; when 1 row inserted, bump `orders.version` and `updated_at`.
  - Closed: `INSERT INTO order_returns (order_id, return_id, closed_at) SELECT id, @r, @now FROM orders WHERE id = @o ON CONFLICT (order_id, return_id) DO UPDATE SET closed_at = EXCLUDED.closed_at WHERE order_returns.closed_at IS NULL`; when 1 row affected, bump `version` and `updated_at`.
  - `HasUnsettledOrders`: `o.OpenReturns > 0` → `db.OrderReturns.Any(r => r.OrderId == o.Id && r.ClosedAt == null)`.
  - Migration drops the column and the check constraint, creates the table. No backfill.
- [ ] **Step 4:** Full `dotnet test --project src/Order/Bloom.Order.Tests` → PASS. Commit `feat(order): track returns per return in order_returns (item 1)`.

### Task 12: CHK-R20 minimum order total (R4)

**Files:**
- Modify: `src/Order/Bloom.Order/Errors/OrderErrors.cs`, `Services/CheckoutService.cs:167-168`, `Observability/OrderMetrics.cs` (its `Results` set)
- Test: `src/Order/Bloom.Order.Tests/CheckoutTests.cs`, `ShellTests.cs` (reason → status table, if it lists them)

**Interfaces:**
- Produces: `OrderErrors.OrderTotalTooLow = "ORDER_TOTAL_TOO_LOW"` → `StatusCode.FailedPrecondition`.

- [ ] **Step 1: Failing tests:**
  - `CHK_R20_full_coupon_with_standard_shipping_is_refused` — 100 % coupon, Standard shipping → `ORDER_TOTAL_TOO_LOW`; order `cancelled` / `checkout_failed`; no reserve call reached inventory; error stored on the idempotency key (a replay returns the same error); `checkouts` metric result `order_total_too_low`.
  - `CHK_R20_total_of_exactly_50_succeeds` and `CHK_R20_total_of_49_is_refused` (prices/coupon chosen so the final `total_cents` is 50 / 49).
- [ ] **Step 2:** `dotnet test --project src/Order/Bloom.Order.Tests --filter-class '*CheckoutTests'` → FAIL.
- [ ] **Step 3:** In `PriceAsync`, after `Pricing.Compute`: `TotalCents < 50` → `throw OrderErrors.Error(OrderErrors.OrderTotalTooLow, "The order total must be at least $0.50.")`. It is a rejection inside `PlaceAsync`'s try, so the existing cancel path applies. Add `order_total_too_low` to the metric's known results.
- [ ] **Step 4:** Full Order tests PASS. Commit `feat(order): refuse checkout below the $0.50 minimum (CHK-R20)`.

### Task 13: Payment uses the shared after-commit mechanism (R3)

**Files:**
- Modify: `src/Payment/Bloom.Payment/Data/PaymentDbContext.cs`, `PaymentApp.cs:38-39`, `Domain/Transitions.cs:16-23` and every `_afterCommit.Add`, `Worker/ProviderExecutor.cs:79,121,151`, `Worker/Reconciler.cs:83`, `Services/StripeWebhook.cs:88`

**Interfaces:**
- Consumes: Task 3's `IAfterCommitContext`, `AfterCommitActions.Add(Func<Task>)`, `AfterCommitInterceptor`.

- [ ] **Step 1:** `PaymentDbContext : IAfterCommitContext`; register the interceptor (P2); `Transitions` drops `_afterCommit` and `Emit()` and adds to `db.AfterCommit`; remove the 5 `transitions.Emit()` calls. `alert?.Invoke()` and `metrics.ReconcileFix` stay where they are.
- [ ] **Step 2:** `dotnet build -c Release` → 0 warnings; `dotnet test --project src/Payment/Bloom.Payment.Tests` → PASS, including `P4_transition_metric_not_recorded_on_rollback` and `P4_dispute_alert_not_logged_on_rollback`.
- [ ] **Step 3:** Commit `refactor(payment): transitions use the shared after-commit mechanism (R3)`.

### Task 14: L-6 early joiners' positions (item 10)

**Files:** Modify: `src/Launch/Bloom.Launch.Tests/CustomerTests.cs:201`

- [ ] **Step 1:** Replace the per-account `Assert.InRange(…, 1, 3)` with: collect the early joiners' positions and `Assert.Equal([1, 2, 3], positions.Order())`. Line 177 unchanged.
- [ ] **Step 2:** `dotnet test --project src/Launch/Bloom.Launch.Tests --filter-class '*CustomerTests'` → PASS.
- [ ] **Step 3:** Commit `test(launch): early joiners hold positions 1-3 (item 10)`.

### Task 15: Whole-branch verification

- [ ] **Step 1:** `dotnet build -c Release` → 0 warnings, 0 errors.
- [ ] **Step 2:** Each test project in turn (`Shared`, `Gateway`, `Identity`, `Catalog`, `Inventory`, `Fulfillment`, `Order`, `Payment`, `Notification`, `Launch`): `dotnet test --project src/<Svc>/Bloom.<Svc>.Tests` → PASS. `docker ps -a` empty of Testcontainers after each.
- [ ] **Step 3:** `git log --name-only fix/pr-followups..HEAD` shows no commit touching both `doc/` and `src/`.
- [ ] **Step 4:** Record in the ledger, for the PR body: item 11 requires re-declaring existing queues (local dev: remove the RabbitMQ volume).
