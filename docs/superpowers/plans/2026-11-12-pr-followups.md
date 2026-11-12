# PR Follow-ups Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Clear every open item of PRs #1–#11: CI green on the stack, the owner's 41 decisions in `doc/` and code, the doc-edit backlog, and the named defects and test gaps.

**Architecture:** First the CI fix runs down the PR stack and the stack is rebased (spec §6). Then the docs change, upstream first: product, HLD, 00-reliability, then each service LLD. Then the code, service by service in stack order: Shared, Gateway, Identity, Catalog, Inventory, Fulfillment, Order, Payment, Notification, Launch, then compose. Each decision ships with the test that pins it. Each defect ships with a test that fails before the fix.

**Tech Stack:** .NET 10, ASP.NET Core gRPC + JSON transcoding, EF Core 10 + Npgsql, RabbitMQ.Client, StackExchange.Redis, Bloom.Shared, xUnit v3 (Microsoft Testing Platform), Testcontainers, `FakeTimeProvider`.

**Spec:** `docs/superpowers/specs/2026-11-12-pr-followups-design.md` (approved 2026-11-12, cited "spec §x" or by item ID: `D1`…`D41`, backlog `G1`…`S1`, defects `S-1`…`L-6`). Each task names its IDs. The spec row is the requirement: read it before you start. This plan adds only file locations, test names and rulings.

## Global Constraints

- **AGENTS.md §9.** Under `doc/`, edit only what spec §3 and §4 list. Spec approval is the owner's approval for those edits and nothing else. Accepted ADRs are superseded, never rewritten. IDs are never renumbered.
- **Upstream first.** Product design, then HLD, then LLD (Tasks 2–6 run before any code).
- **New ADRs, exactly these IDs:** PD-ADR-009, HLD-ADR-019, LLD-ADR-011, LLD-ADR-012, LLD-ADR-013, LLD-ADR-014. Each one is added to its layer's `adr/README.md` index.
- **Header stamp.** Every changed `doc/` document gets `| **Status** | Approved (2026-11-12) |`. Only the Status line changes, as in the precedent commit `253b629`.
- **Doc commits.** One commit per layer (product, HLD) and one per LLD document. A new ADR goes in the commit of the document that cites it first.
- **Code commits.** One commit or more per decision or per service defect group. Never mix `doc/` and code in one commit.
- No new NuGet packages. The only version change is the 10.0.14 → 10.0.15 bump in Task 1.
- No behaviour, message, field, reason, metric or route beyond spec §3 and §5. Additive message fields stay v1 (LLD-ADR-004).
- Every gRPC error uses the `BloomErrors.Create` shape. New reasons: only `ORDER_RELEASED` (D17).
- Schema changes are new EF migrations in their own service: D16 inventory, D26 payment, D35 fulfillment, D37 catalog. `SchemaTests` in each service must still pass.
- Timestamps inside a transaction use the database `now()`. Test clocks use `t.Clock` (`FakeTimeProvider`), never a raw `UtcNow`.
- `dotnet build -c Release` must give 0 warnings (`TreatWarningsAsErrors`).
- **Local runs (CLAUDE.md).** Never run `colima`. Never `dotnet run` or `dotnet watch`. Run one test project at a time: `dotnet test --project src/<Svc>/Bloom.<Svc>.Tests [--filter-class '*<Class>']`. If the Docker daemon is unreachable, stop and report. After each run, `docker ps -a` must show no leftover Testcontainers containers.
- Branch `fix/pr-followups`, base `feat/launch`. Never merge PRs. Force-push (`--force-with-lease`) only as Task 1 says.

## Review Focus

1. **A non-connection Npgsql error inside a handler** (a unique violation `23505`, a statement timeout `57014`, a serialization failure `40001`). The consumer must not pause. It counts an attempt, and after 5 it dead-letters as today. Only failures to reach the service's own database pause (D3). Test in Task 8.
2. **A duplicate `payment.refund-failed.v1` for one refund** (a redelivery with a new message id, or the same failure applied twice). `refunded_cents` is reduced once, never twice, and never below 0 (D35). Test in Task 17.
3. **A release command for an order that has no allocations** (released before `order.paid.v1` or a reserve ever arrived). It still writes the tombstone. A later paid creates nothing, and a later reserve gets `ORDER_RELEASED` (D16, D17). Test in Task 14.
4. **Two launch replicas ticking at the same instant** (0 ms apart, not only 500 ms). Only one pops; the other returns 0 (D4). Test in Task 25.
5. **A checkout resumed exactly at `deadline_at`** (`deadline_at = now()`). It returns `CHECKOUT_CLOSED`, never a continuation, and never crashes on a null `deadline_at` in `creating` (D21). Test in Task 19.

## Rulings in this plan (builder's calls the spec left open)

- **R1 Workspace.** Execute in the main worktree `/Users/datnguynx/Project/Bloom/Bloom_Server`, on the existing branch `fix/pr-followups`. Task 1 rewrites refs that other worktrees would pin, so there is no separate worktree. Every task after Task 1 starts and ends on `fix/pr-followups` with a clean tree.
- **R2 Task 1 testing.** On each stack branch, Task 1 runs `dotnet build -c Release` plus the test project(s) whose files the new commit touches. It doesn't run the full suite on every branch. CI on push runs the full suite, and that result is the acceptance.
- **R3 "Verify" items** (D18 order side, D33 order side, F-4). First write the test. If it passes, commit it as a pinning test. If it fails, fix the code in the same task.
- **R4 D3 test mechanics.** Pause the service's PostgreSQL container with Testcontainers `PauseAsync`/`UnpauseAsync`. The test connection string sets `Timeout=2`, so opening a connection fails fast. The classifier lives in one place: `Bloom.Shared/Messaging/DatabaseOutage.cs`, `static bool IsOutage(Exception ex)`. It walks inner exceptions. It is true for `NpgsqlException` whose `InnerException` is `SocketException` or `TimeoutException`, for `PostgresException` with `SqlState` `57P01` or `57P03`, and for an `NpgsqlException` raised while opening a connection (`ConnectionState` not open). The builder may tighten the list, but must keep it in that file with a table-driven test.
- **R5 D16 purge.** A new inventory worker job `Worker/TombstonePurger.cs`. It runs daily and deletes `released_orders` rows with `released_at < now() - interval '30 days'`, 1,000 rows per statement, until a statement deletes fewer. This follows payment's `NightlyJob` purge pattern; the shared purger's rules are fixed.
- **R6 D26 seed.** The migration inserts `('nightly_compare', DATE '2000-01-01')`. The claim uses the UTC date from the database (`(now() AT TIME ZONE 'UTC')::date`).
- **R7 D35 guard.** The subtraction runs in the same transaction and guarded update that moves the saga to failed: `… WHERE id = @saga AND status <> 'failed'` (or the saga's current guard). If 0 rows are hit, nothing is subtracted. `refunded_cents` is floored at 0 with `GREATEST(…, 0)` and an Error log if it would go negative. A saga created before the migration (`refund_lines IS NULL`) subtracts nothing and logs an Error naming the saga.
- **R8 D37 backfill.** `reviewer_names.updated_at timestamptz NOT NULL`. Existing rows are set to `'1970-01-01Z'`.
- **R9 D21 deadline.** "Has passed" means `deadline_at <= now()` (database clock).
- **R10 Out-of-scope findings** seen during a task go to the ledger as `Parked: …`. They are not fixed.
- **R11 Commit prefixes.** `docs(product)`, `docs(hld)`, `docs(lld): <doc>`, `docs(spec)` for `docs/superpowers`. Code uses `fix(<svc>)`, `feat(<svc>)`, `refactor(<svc>)`, `test(<svc>)`, with `<svc>` = `shared`, `gateway`, `identity`, `catalog`, `inventory`, `fulfillment`, `order`, `payment`, `notification`, `launch` or `compose`. Every commit ends with the attribution line `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.

---

## File Structure

New files only. Everything else is a change to an existing file, named per task.

```
doc/product-design/adr/PD-ADR-009-no-price-change-flag-in-cart.md
doc/high-level-design/adr/HLD-ADR-019-consumers-pause-during-own-db-outage.md
doc/low-level-design/adr/LLD-ADR-011-dlq-pass-lock-transaction-scoped.md
doc/low-level-design/adr/LLD-ADR-012-admission-paced-per-launch.md
doc/low-level-design/adr/LLD-ADR-013-revoked-refresh-not-reuse.md
doc/low-level-design/adr/LLD-ADR-014-release-tombstone.md
src/Shared/Bloom.Shared/Messaging/DatabaseOutage.cs        # D3 classifier (R4)
src/Shared/Bloom.Shared.Tests/ConsumerPauseTests.cs         # D3
src/Inventory/Bloom.Inventory/Worker/TombstonePurger.cs     # D16 (R5)
src/*/Data/Migrations/<new migration>                       # D16, D26, D35, D37
```

---

### Task 1: CI fix down the stack (spec §6)

**Files:**
- Commits on `feat/identity`, `feat/catalog`, `feat/inventory`, `feat/fulfillment`, `feat/order`, `feat/payment` and `feat/notification`. Each takes the matching part of PR #10's commit `f27976b`.
- Modify: `Directory.Packages.props` lines 13–18 (on `feat/notification` only).

| Branch | Paths taken from `f27976b` |
|---|---|
| `feat/identity` | `src/Identity/Bloom.Identity.Tests/Infrastructure/TestIdentity.cs`, `src/Identity/Bloom.Identity.Tests/ShellTests.cs`, `src/Gateway/Bloom.Gateway.Tests/Infrastructure/TestGateway.cs` |
| `feat/catalog` | `src/Catalog/Bloom.Catalog.Tests/Infrastructure/TestCatalog.cs` |
| `feat/inventory` | `src/Inventory/Bloom.Inventory.Tests/Infrastructure/TestInventory.cs`, `src/Inventory/Bloom.Inventory.Tests/OrderPaidTests.cs` |
| `feat/fulfillment` | `src/Fulfillment/Bloom.Fulfillment.Tests/Infrastructure/TestFulfillment.cs` |
| `feat/order` | `src/Order/Bloom.Order.Tests/Infrastructure/TestOrder.cs` |
| `feat/payment` | `src/Payment/Bloom.Payment.Tests/Infrastructure/TestPayment.cs` |
| `feat/notification` | `src/Notification/Bloom.Notification.Tests/Infrastructure/TestNotification.cs`, plus all six `10.0.14` → `10.0.15` in `Directory.Packages.props` |

- [ ] **Step 1: Record the old tips.** Write `git rev-parse` for every branch from `feat/api-gateway` to `fix/pr-followups` into the ledger. They are the rollback point.
- [ ] **Step 2: Bottom-up loop.** Work through the table in order. For each branch: `git switch <branch>`. If it isn't `feat/identity`, run `git rebase <branch below>`. Then run `git diff f27976b~1 f27976b -- <paths> | git apply --3way`. Apply the package bump where the table says. Run `dotnet build -c Release` (expect 0 warnings) and the touched test project(s) (R2). Then commit with `test(<svc>): seed test clocks at a whole microsecond (PostgreSQL precision on Linux CI)`. The `feat/notification` commit message adds `; bump 10.0.14 packages to 10.0.15 (GHSA-wr8v-9x5q-ggcm)`.
- [ ] **Step 3: Rebase the rest.** Run `git switch fix/test-clock-precision && git rebase feat/notification`. Its commit becomes empty and is dropped (the default `--empty=drop`). Then `git switch feat/launch && git rebase fix/test-clock-precision`, then `git switch fix/pr-followups && git rebase feat/launch`. If a conflict isn't a mechanical overlap of the seed lines, `git rebase --abort` and stop-and-report.
- [ ] **Step 4: Verify the top.** On `fix/pr-followups`, run `dotnet restore` and expect no `NU1903`. Then `dotnet build -c Release` with 0 warnings, and `git log --oneline feat/launch..fix/pr-followups`, which must show only the spec commit (`docs(spec): PR follow-ups design…`) and the plan commit (`docs(plan): PR follow-ups implementation plan`).
- [ ] **Step 5: Push.** Run `git push --force-with-lease origin feat/identity feat/catalog feat/inventory feat/fulfillment feat/order feat/payment feat/notification fix/test-clock-precision feat/launch`, then `git push -u --force-with-lease origin fix/pr-followups`. Never push `feat/api-gateway`.
- [ ] **Step 6: Note on #10.** `gh pr view 10 --json body -q .body`, then `gh pr edit 10 --body` with the old body plus a section `## Moved` saying the seed changes now sit on the branch of each service (list the table) and that this PR has no remaining diff.
- [ ] **Step 7: CI green.** Run `gh pr checks <n> --watch` for each `n` in 2…11. Every check must pass. A red check that the clock or package causes don't explain is a stop-and-report (spec §6).

---

### Task 2: Product and HLD docs

**IDs:** O1 (PD-ADR-009, CHK-R18), O2 (CHK-R19), D33 (RET-R15), C9 (the 09-reviews line), D3 (HLD-ADR-019, HLD-ADR-007 header).

**Files:**
- Create: `doc/product-design/adr/PD-ADR-009-no-price-change-flag-in-cart.md`, `doc/high-level-design/adr/HLD-ADR-019-consumers-pause-during-own-db-outage.md`
- Modify: `doc/product-design/05-checkout-payment.md`, `07-returns-refunds.md`, `09-reviews.md`, `doc/product-design/adr/README.md`, `doc/high-level-design/adr/HLD-ADR-007-inbox-retries-dlq.md` (header line `Amended by HLD-ADR-019` only), `doc/high-level-design/adr/README.md`

- [ ] **Step 1: Product layer.** Copy the wording of order spec §9 items 1–2 and catalog spec §9 item 9 (09-reviews part), and add the RET-R15 sentence from spec D33. Write PD-ADR-009 in the format of PD-ADR-008 (context, decision, consequences). Stamp each changed document. Commit `docs(product): PD-ADR-009, CHK-R18, CHK-R19, RET-R15, reviews of plans`.
- [ ] **Step 2: HLD layer.** Write HLD-ADR-019 in the format of HLD-ADR-018, stating the D3 rule and that it amends HLD-ADR-007. Don't edit 007 beyond its header line. Update the index. Commit `docs(hld): HLD-ADR-019 consumers pause during their own database outage`.
- [ ] **Step 3: Verify.** Run `grep -n 'PD-ADR-009' doc/product-design/adr/README.md && grep -n 'HLD-ADR-019' doc/high-level-design/adr/README.md doc/high-level-design/adr/HLD-ADR-007-inbox-retries-dlq.md`. Every grep must hit. `git diff --stat HEAD~2 -- doc` must list only the files above.

---

### Task 3: 00-reliability, LLD-ADR-011, spec S1

**IDs:** D1, D2 (LLD-ADR-011 supersedes LLD-ADR-010), D3 (§7.3 step 8), §7.4 "Applies to" (F4, D28, O7; check that I7 is already there), S1.

**Files:**
- Create: `doc/low-level-design/adr/LLD-ADR-011-dlq-pass-lock-transaction-scoped.md`
- Modify: `doc/low-level-design/00-reliability.md` (§7.3, §7.4, §7.7), `doc/low-level-design/adr/LLD-ADR-010-dlq-busy-advisory-lock.md` (status line only), `doc/low-level-design/adr/README.md`
- Modify (not `doc/`): `docs/superpowers/specs/2026-10-31-shared-library-design.md` lines 134 and 144, and `docs/superpowers/plans/2026-10-31-shared-library.md` Review Focus 1 and Task 6. They must describe the relay and interceptor as built (read `OutboxRelay.cs` and `OutboxWriter.cs`).

- [ ] **Step 1:** Edit 00-reliability per D1, D2, D3, F4, D28 and O7 (O7: the third execution form, worded as order spec §9 item 7; no ADR). Write LLD-ADR-011. Set 010's status to `Superseded by LLD-ADR-011`. Update the index and stamp the document. Commit `docs(lld): 00-reliability and LLD-ADR-011`.
- [ ] **Step 2:** Make the S1 edits. Commit `docs(spec): shared-library spec and plan describe the relay as built`.
- [ ] **Step 3: Verify.** `grep -n "x-bloom-role = admin" doc/low-level-design/00-reliability.md` hits. `grep -n 'Superseded by LLD-ADR-011' doc/low-level-design/adr/LLD-ADR-010-*.md` hits.

---

### Task 4: LLD docs: identity, gateway, catalog

**IDs:** 06-identity: D8 (LLD-ADR-013), D9, D10, D11, ID1–ID6, G3 (the 06-identity part). 07-api-gateway: D12 + G1, G2, G4, G5, D13, D14, D15, C1 (the gateway route). 08-catalog: D36, D37, C1 (the catalog part) through C12.

**Files:**
- Create: `doc/low-level-design/adr/LLD-ADR-013-revoked-refresh-not-reuse.md`
- Modify: `doc/low-level-design/06-identity.md`, `07-api-gateway.md`, `08-catalog.md`, `adr/README.md`

- [ ] **Step 1:** 06-identity, with LLD-ADR-013 and its index row. Commit `docs(lld): 06-identity`.
- [ ] **Step 2:** 07-api-gateway. G5 wording comes from `gh pr view 2`, owner item 2. Commit `docs(lld): 07-api-gateway`.
- [ ] **Step 3:** 08-catalog. Commit `docs(lld): 08-catalog`.
- [ ] **Step 4: Verify.** All three are stamped `Approved (2026-11-12)`. `grep -c 'LLD-ADR-013' doc/low-level-design/06-identity.md` is ≥ 1.

---

### Task 5: LLD docs: inventory, fulfillment, order

**IDs:** 02-inventory: D16, D17, D18 (LLD-ADR-014); check that I8–I10 are applied; I11 is superseded (the §8 line 272 sentence is replaced by the tombstone rule). 05-fulfillment: D18 (sender side), D27–D35, F1–F7, F9–F18 (F8 is superseded by F11). 03-order: D17 (`ORDER_RELEASED` → `CHECKOUT_CLOSED`), D19–D23, O1 and O2 (the 03-order parts), O3–O16, F1 (the 03-order part).

**Files:**
- Create: `doc/low-level-design/adr/LLD-ADR-014-release-tombstone.md`
- Modify: `doc/low-level-design/02-inventory.md`, `05-fulfillment.md`, `03-order.md`, `adr/README.md`

- [ ] **Step 1:** 02-inventory, with LLD-ADR-014. Commit `docs(lld): 02-inventory and LLD-ADR-014`.
- [ ] **Step 2:** 05-fulfillment. F15–F18 come from `gh pr view 6`. Commit `docs(lld): 05-fulfillment`.
- [ ] **Step 3:** 03-order. O15 and O16 come from `gh pr view 7`. Commit `docs(lld): 03-order`.
- [ ] **Step 4: Verify.** All three are stamped. `grep -n 'released_orders' doc/low-level-design/02-inventory.md` hits. `grep -n 'ORDER_RELEASED' doc/low-level-design/02-inventory.md doc/low-level-design/03-order.md` hits in both.

---

### Task 6: LLD docs: payment, notification, launch

**IDs:** 04-payment: D24 + P1, D25, D26, D27a, P2–P15 (P15 from `gh pr view 8`). 09-notification: D38, D39, N1–N5, N6 (`gh pr view 9`). 01-launch: D4 (LLD-ADR-012), D5, D6, D7, L1–L9, L10 (`gh pr view 11`).

**Files:**
- Create: `doc/low-level-design/adr/LLD-ADR-012-admission-paced-per-launch.md`
- Modify: `doc/low-level-design/04-payment.md`, `09-notification.md`, `01-launch.md`, `adr/README.md`

- [ ] **Step 1:** 04-payment. Commit `docs(lld): 04-payment`.
- [ ] **Step 2:** 09-notification. Commit `docs(lld): 09-notification`.
- [ ] **Step 3:** 01-launch, with LLD-ADR-012. Commit `docs(lld): 01-launch and LLD-ADR-012`.
- [ ] **Step 4: Verify.** All three are stamped. `ls doc/low-level-design/adr/LLD-ADR-01{1,2,3,4}-*.md` lists four files, and each is linked from `adr/README.md`. `git diff --stat feat/launch -- doc` lists only the files named in Tasks 2–6.

---

### Task 7: Shared DLQ admin: lowercase role and transaction-scoped lock (D1, D2)

**Files:**
- Modify: `src/Shared/Bloom.Shared/DeadLetters/DeadLetterAdminService.cs` (`RunLockedAsync` lines 66–100; role check at line 123)
- Modify tests: `src/Shared/Bloom.Shared.Tests/DeadLetterAdminTests.cs`. Also every `"Admin"` DLQ role in `src/{Order,Notification,Launch,Payment,Catalog,Fulfillment,Inventory}/*.Tests/WorkerTests.cs`, `src/Identity/Bloom.Identity.Tests/AuditQueryTests.cs:139` (and its stale comment) and `src/Notification/Bloom.Notification.Tests/ShellTests.cs:115`. Change role values and `actorRole` assertions only. Catalog `ReviewTests.cs:156` is an author name: leave it.

- [ ] **Step 1: Write the failing tests** in `DeadLetterAdminTests`:
  - `Role_Admin_capitalized_is_rejected`: `x-bloom-role: Admin` → `PermissionDenied`, reason `ROLE_NOT_ALLOWED`.
  - `Role_admin_lowercase_is_allowed`: a list call succeeds.
  - `Lock_is_released_when_pass_ends`: after a replay pass, `SELECT count(*) FROM pg_locks WHERE locktype = 'advisory'` on another connection = 0.
  - `Lock_is_released_when_pass_throws`: the pass action throws (for example, the broker channel closed mid-pass). The count is 0 afterwards, and a second pass isn't `DLQ_BUSY`.
  - The existing T-23 test (`DLQ_BUSY` for a concurrent pass) is unchanged.
- [ ] **Step 2: Run them.** `dotnet test --project src/Shared/Bloom.Shared.Tests --filter-class '*DeadLetterAdminTests'`. Expect FAIL on the role tests.
- [ ] **Step 3: Implement.** The role compare becomes `"admin"`, and the message says "admin role". In `RunLockedAsync`, open the lock connection, `BEGIN`, `SELECT pg_try_advisory_xact_lock(hashtextextended('bloom.dlq:' || @queue, 0))` (same key), false → `DLQ_BUSY`, run the pass, then `COMMIT` in `finally` (or rollback on throw). Remove `pg_advisory_unlock`. Audit writes keep their own connections.
- [ ] **Step 4: Update the service tests** to `"admin"`. Run the Shared tests, then each changed service's `WorkerTests` (one project at a time). Expect PASS.
- [ ] **Step 5: Commit.** `fix(shared): DLQ admin role is lowercase admin (D1)` and `fix(shared): DLQ pass lock is transaction-scoped (D2, LLD-ADR-011)`.

---

### Task 8: Consumers pause during their own database outage (D3)

**Interfaces:**
- Produces: `internal static class DatabaseOutage { public static bool IsOutage(Exception ex); }` in `Bloom.Shared/Messaging/DatabaseOutage.cs` (R4).

**Files:**
- Create: `src/Shared/Bloom.Shared/Messaging/DatabaseOutage.cs`, `src/Shared/Bloom.Shared.Tests/ConsumerPauseTests.cs`
- Modify: `src/Shared/Bloom.Shared/Messaging/ConsumerHost.cs` (`ProcessAsync` catch at line 141, `RunSessionAsync`)

- [ ] **Step 1: Write the failing tests** in `ConsumerPauseTests`:
  - `IsOutage_classifies` (table-driven): a socket-refused `NpgsqlException`, a connect timeout, `57P01` and `57P03` → true. `23505`, `57014`, `40001`, `InvalidOperationException` and a `PermanentFailureException` → false.
  - `Db_paused_messages_are_not_counted_or_dead_lettered`: publish 3 messages, `PauseAsync` the Postgres container for 5 s, then `UnpauseAsync`. All 3 are handled once. No message reached the retry or DLQ queues. No `x-attempt` above 1 was seen.
  - `Consuming_stops_while_db_is_down`: while paused, the queue's consumer count is 0 (or the message stays ready), and it resumes within 3 s after unpause.
  - `Handler_exception_keeps_five_attempt_rule`: a handler that throws `InvalidOperationException` dead-letters after 5 attempts (existing behaviour).
  - Review Focus 1: `Handler_postgres_error_is_counted`: a handler that throws `PostgresException` `23505` is retried and attempt-counted, with no pause.
- [ ] **Step 2: Run them.** `dotnet test --project src/Shared/Bloom.Shared.Tests --filter-class '*ConsumerPauseTests'`. Expect FAIL.
- [ ] **Step 3: Implement.** In `ProcessAsync`, when `DatabaseOutage.IsOutage(ex)`, NACK with `requeue: true`, skip the attempt logic and signal the session to pause. The session cancels its consumer (`BasicCancelAsync`), probes `SELECT 1` through a scoped `DbContext` every 1 s, then re-consumes. Scope is per process and consumers only; the outbox relay is unchanged.
- [ ] **Step 4: Run them.** The whole Shared project. Expect PASS.
- [ ] **Step 5: Commit.** `feat(shared): consumers pause while their own database is unreachable (D3, HLD-ADR-019)`.

---

### Task 9: Shared defects (S-1, S-2, S-3)

**Files:** `src/Shared/Bloom.Shared/Idempotency/IdempotencyGuard.cs`, `src/Shared/Bloom.Shared/Messaging/OutboxRelay.cs`. Tests: `IdempotencyTests.cs`, `OutboxRelayTests.cs`, `MetricNamesTests.cs`.

- [ ] **Step 1: Write the failing tests.**
  - `Retakeover_rerun_starts_with_clean_change_tracker`: the first `apply` adds an entity and then fails after the claim. On re-takeover, the second `apply` saves exactly one row.
  - `Relay_uses_one_confirm_deadline_per_pass`: 3 rows, and the broker delays confirms. The total wait is ≤ one deadline, not 3×.
  - `Relay_reopen_failure_reports_reopen_error`: channel re-open throws. The log or metric names the re-open, not "no confirm".
- [ ] **Step 2: Run them.** Expect FAIL.
- [ ] **Step 3: Implement.** S-1: `ChangeTracker.Clear()` before the re-run. S-2: one deadline computed per pass, and a distinct re-open error path. The R7 1 s pause is unchanged.
- [ ] **Step 4: S-3.** In `Loop_pauses_1s`, assert only the upper bound and that a pause happened, with no lower-bound wall-clock check. Give the `MetricNamesTests` timeout a message naming the metric it waited for.
- [ ] **Step 5: Run and commit.** Run the whole Shared project and expect PASS. Commit `fix(shared): idempotency re-takeover and relay confirm deadline (S-1, S-2)` and `test(shared): timing-safe relay pause and metric timeout message (S-3)`.

---

### Task 10: Gateway decisions and test hygiene (D12–D15, GW-1–GW-3)

**Files:** `src/Gateway/Bloom.Gateway/Auth/TokenValidator.cs`, `Edge/RequestHygiene.cs`, `Edge/EdgeMiddleware.cs`, `Observability/GatewayMetrics.cs` or wherever span tags are set (find `url.path`). Tests: `AuthTests.cs`, `RoutingTests.cs`, `ShellTests.cs`, plus GW-1/GW-2/GW-3 in `AuthTests`, `EdgeLimitTests`, `RedisOutageTests` and `RevocationWorkerTests`.

- [ ] **Step 1: Write the failing tests.**
  - `D12_token_issued_same_second_as_revoke_all_is_revoked`: `iat = 1000`, `rev = 1000500` → `TOKEN_REVOKED`.
  - `D13_non_bearer_authorization_is_token_invalid`: `Authorization: Basic abc` on `GET /api/catalog/products` → 401 `TOKEN_INVALID`.
  - `D14_client_x_forwarded_for_is_replaced`: the client sends `X-Forwarded-For: 6.6.6.6`. The upstream sees the connection's IP, not `6.6.6.6`. A trusted-proxy hop keeps today's processing.
  - `D15_shippo_secret_absent_from_spans`: `POST /webhooks/shippo/<secret>`. No exported activity has a tag value containing `<secret>`. `url.path` = `/webhooks/shippo/***`.
- [ ] **Step 2: Run them.** `dotnet test --project src/Gateway/Bloom.Gateway.Tests`. Expect FAIL on D13–D15. D12 may already pass; then it is a pin (R3).
- [ ] **Step 3: Implement** D13 (auth step), D14 (request hygiene) and D15 (span redaction).
- [ ] **Step 4: GW-1–GW-3.** GW-1: `Authorization_and_cookie_are_never_logged` captures at Trace. GW-2: the F3 reconnect test asserts a state before the broker drop and a different state after it. GW-3: remove the unused usings.
- [ ] **Step 5: Run and commit.** Run the project and expect PASS. Commit `fix(gateway): non-Bearer auth, forwarded headers, shippo span redaction (D12–D15)` and `test(gateway): log capture, reconnect state, unused usings (GW-1–GW-3)`.

---

### Task 11: Gateway runtime defects (GW-4–GW-7)

**Files:** `GatewayApp.cs`, `Edge/EdgeRoute.cs`, `Limits/EdgeStore.cs`, `Auth/TokenValidator.cs`, `Auth/JwksCache.cs`, `Observability/GatewayMetrics.cs`, `Worker/RevocationConsumer.cs`. Tests: `ShellTests`, `EdgeLimitTests`, `RedisOutageTests`, `RevocationWorkerTests`.

- [ ] **Step 1: Write the failing tests.**
  - `Bad_route_auth_metadata_fails_startup` and `Missing_redis_config_fails_build`: building the app throws, and the message names the route or key.
  - `Lua_script_loaded_once`: N limited requests → one `SCRIPT LOAD`, then `EVALSHA` only (count with Redis `INFO commandstats`).
  - `Validation_parameters_rebuilt_only_on_jwks_change`.
  - `Degraded_gauge_updates_while_idle`: Redis down, no requests, advance the fake clock → the gauge = 1.
  - `Degraded_logs_once_on_enter_and_leave`.
  - `Redis_server_error_is_500_not_outage`: a Lua error → 500, and the degraded gauge stays 0.
  - `Worker_unexpected_exception_nacks_and_requeues_after_1s`.
- [ ] **Step 2: Run them.** Expect FAIL.
- [ ] **Step 3: Implement** GW-4 to GW-7 as spec §5 says (`LuaScript.Prepare` + `Load`, a cached `TokenValidationParameters` keyed by the JWKS version, a gauge timer, state-transition logs, a `RedisServerException` split, and NACK with requeue after `Task.Delay(1 s, timeProvider)`).
- [ ] **Step 4: Run and commit.** Run the project and expect PASS. Commit `fix(gateway): startup validation, EVALSHA, degraded-mode gauge and logs, worker requeue (GW-4–GW-7)`.

---

### Task 12: Identity (D8, D10, D11, ID-1–ID-3)

**Files:** `Sessions/SessionService.cs`, `Services/AuthService.cs` (Google sign-in), `Services/AccountService.cs` (UpsertAddress, DeleteAccount), `Observability/IdentityMetrics.cs`. Tests: `SessionTests`, `GoogleTests`, `AddressTests`, `DeletionTests`, `InvitationTests`, `SignInTests`, `AuditQueryTests`.

- [ ] **Step 1: Write the failing tests.**
  - `ID_T17_revoked_refresh_after_sign_out_everywhere_is_invalid_without_side_effects`: sign in on two devices, run `SignOutEverywhere`, then sign in again (new session). The old device's refresh → `REFRESH_INVALID`. The new session still refreshes. `refresh_total{result=reuse}` is unchanged.
  - `D10_google_sign_in_with_different_linked_sub_is_email_taken`.
  - `D11_two_concurrent_default_upserts_leave_one_default`.
  - `D11_two_concurrent_deletes_publish_one_account_deleted`: the second one gets `NOT_FOUND`.
  - `D11_delete_and_upsert_do_not_deadlock`.
  - `ID2_concurrent_revoke_alls_increment_version_twice`.
  - ID-3: `Invite_expires_exactly_at_72h`, `Google_alg_none_rejected`, `Google_hs256_rejected`, `Audit_tie_split_across_page_boundary`, `TryRotate_vs_SignOut_overlap`, and one test per metric label not yet asserted (list them from `IdentityMetrics.cs`).
- [ ] **Step 2: Run them.** `dotnet test --project src/Identity/Bloom.Identity.Tests`. Expect FAIL on D8, D10, D11 and ID-2.
- [ ] **Step 3: Implement.** D8: split `rotated` from `revoked`. D10: the `google_sub` check. D11: `FOR UPDATE` on the account row first in both paths, and `DeleteAccount` re-checks `status`. ID-2: `UPDATE … SET version = version + 1`. ID-1: fix the comment at `SessionService.cs:53`.
- [ ] **Step 4: Run and commit.** Run the project and expect PASS. Commit per decision: `fix(identity): revoked refresh is not reuse (D8, LLD-ADR-013)`, `fix(identity): google sign-in never replaces a linked identity (D10)`, `fix(identity): serialize default address and delete on the account row (D11)`, `fix(identity): atomic revoke-all version, stale comment, test gaps (ID-1–ID-3)`.

---

### Task 13: Catalog (D36, D37, C-1, C-2)

**Files:** `Consumers/LaunchStageChangedConsumer.cs`, `Consumers/ReviewerNameConsumer.cs`, `Data/Entities.cs` + a new migration (D37, R8), `Services/AdminService.Categories.cs`. Tests: `ConsumerTests`, `CategoryTests`, plus the C-2 tests where the catalog review ledger names them.

- [ ] **Step 1: Write the failing tests.**
  - `D36_launch_event_never_changes_archived_product`.
  - `D37_older_registered_event_does_not_overwrite_newer_contact_change`: `contact-changed` at T2 is applied, then `customer-registered` at T1 < T2. The T2 name stays.
  - `C1_category_rename_refreshes_products_in_ascending_id_order`: assert the lock order, or run a rename concurrently with a launch event and show no deadlock.
  - C-2: the parallel-write race tests, the plan-lifecycle cache test and the single-branch cases. Find them with `grep -rn 'C-2\|parallel\|single-branch' .superpowers/sdd/*catalog*/progress.md`. If no ledger is found, take the list from `gh pr view 4`.
- [ ] **Step 2: Run them.** `dotnet test --project src/Catalog/Bloom.Catalog.Tests`. Expect FAIL on D36, D37 and C-1.
- [ ] **Step 3: Implement.** The D36 guard. D37: the column, the migration, and an upsert `WHERE reviewer_names.updated_at < EXCLUDED.updated_at`. C-1: `ORDER BY id`.
- [ ] **Step 4: Run and commit.** Run the project and expect PASS (`SchemaTests` included). Commit `fix(catalog): archived is final for launch events (D36)`, `fix(catalog): reviewer names newest event wins (D37)`, `fix(catalog): category rename lock order and race tests (C-1, C-2)`.

---

### Task 14: Inventory decisions (D16, D17, D18)

**Interfaces:**
- Produces: the reason `ORDER_RELEASED` (`FAILED_PRECONDITION`) on `ReserveStock`, in `Errors/InventoryErrors.cs` and the contracts reason list if there is one. Task 19 relies on it.
- Produces: an optional `ReshipId` (`Guid?`, JSON `reshipId`) on the `inventory.release-reservation.v1` record in `Bloom.Inventory.Contracts`. Task 16 sends it.

**Files:** `Consumers/ReleaseReservationConsumer.cs`, `Consumers/OrderPaidConsumer.cs`, `Services/StockService.cs` (ReserveStock), `Data/Entities.cs` + migration `released_orders`, `Worker/TombstonePurger.cs` (R5), `InventoryApp.cs` (worker registration). Tests: `ReleaseTests`, `OrderPaidTests`, `StockApiTests`, `WorkerTests`.

- [ ] **Step 1: Write the failing tests.**
  - `D16_release_then_paid_creates_no_allocations_and_warns`.
  - Review Focus 3: `D16_release_without_allocations_still_tombstones`. Release an order that never reserved. The row exists. A later paid creates nothing, and a later `ReserveStock` gets `ORDER_RELEASED`.
  - `D16_duplicate_release_is_idempotent`: `ON CONFLICT DO NOTHING`, one row.
  - `D16_purger_deletes_only_older_than_30_days`: rows at now−31 d and now−29 d → only the first is deleted.
  - `D17_reserve_for_released_order_returns_ORDER_RELEASED`: nothing is reserved.
  - `D18_release_by_reship_id_releases_only_that_reship`: allocations for the order and for reship R. Release `{orderId, reshipId: R}` → only `ref_id = R` is released, no tombstone, and `reservation-released {orderId}` is published.
- [ ] **Step 2: Run them.** `dotnet test --project src/Inventory/Bloom.Inventory.Tests`. Expect FAIL.
- [ ] **Step 3: Implement** as spec D16–D18 and LLD-ADR-014.
- [ ] **Step 4: Run and commit.** Run the project and expect PASS. Commit `feat(inventory): release tombstone and ORDER_RELEASED (D16, D17, LLD-ADR-014)` and `feat(inventory): release by reship id (D18)`.

---

### Task 15: Inventory defects (IN-1–IN-9)

**Files:** `Domain/StockLocks.cs`, `Services/StockService.cs`, `Services/WarehouseService.Receipts.cs`, `Services/PackingService.cs`, `Consumers/LostInTransitConsumer.cs`, `Observability/InventoryMetrics.cs`, `Worker/StockMonitor.cs`. Tests: per item, below.

- [ ] **Step 1: Write the failing tests.**
  - `IN1_order_locks_taken_once_in_ascending_order` (StockApiTests or ReleaseTests).
  - `IN3_reserve_shortage_then_pk_race_is_mapped`.
  - `IN4_concurrent_invalid_receipts_with_crossed_imeis_get_RECEIPT_INVALID`.
  - `IN5_ledger_metric_not_counted_on_rollback`.
  - `IN6_release_second_lock_keeps_ascending_order`.
  - `IN7_second_lost_in_transit_command_does_not_double_count_lost`.
  - IN-8, one test each: reship `ref_type` on fulfillment ledgers, a wrong-SKU serial in a customer return, a refused serialized decrease leaves the serial `in_stock`, a populated IMEI in `GetSerial`, and the packing edge asserts.
- [ ] **Step 2: Test fixes.** IN-2: the stock-monitor test calls `StopAsync` on the hosted monitor and drives `RunOnceAsync` by hand. IN-9: test seeds that write `DateTime.UtcNow` / `DateTimeOffset.UtcNow` use `t.Clock` (`grep -rn 'UtcNow' src/Inventory/Bloom.Inventory.Tests`).
- [ ] **Step 3: Run them.** Expect FAIL on IN-1 and IN-3 to IN-7.
- [ ] **Step 4: Implement** IN-1 and IN-3 to IN-7 as spec §5.
- [ ] **Step 5: Run and commit.** Run the project and expect PASS. Commit `fix(inventory): lock order, race mapping, post-commit metrics, lost guard (IN-1, IN-3–IN-7)` and `test(inventory): monitor driving, test-clock seeds, coverage gaps (IN-2, IN-8, IN-9)`.

---

### Task 16: Fulfillment decisions, part 1 (D27–D31)

**Interfaces:**
- Consumes: `ReshipId` on the release-reservation message (Task 14).

**Files:** `Services/SupportService.cs` (ConfirmLost), `Services/CustomerService.cs` (RequestReturn), `Services/WarehouseService.Packing.cs` (UnassignSerial), `Services/InternalService.cs` or `CustomerService.cs` (GetCoverage), `Consumers/AccountDeletedConsumer.cs`, the fulfillment proto or contracts for the `expired` coverage status. Tests: `DeliveryFailureTests`, `ReturnTests`, `PackingTests`, `CoverageTests`.

- [ ] **Step 1: Write the failing tests.**
  - `D27_failure_resolved_before_apply_sends_reship_release_and_returns_INVALID_STATE`: the outbox holds `inventory.release-reservation.v1 {orderId, reshipId}`, committed.
  - `D28_request_return_replay_returns_same_request` and `D28_request_return_without_key_is_IDEMPOTENCY_KEY_REQUIRED`.
  - `D29_unassign_serial_without_local_record_calls_inventory`: the fake inventory sees the call, and its error passes through.
  - `D30_coverage_past_end_reads_expired`: the stored status stays `active`.
  - `D31_account_deleted_expires_requested_returns`: `return-closed (reason expired)` is published, and the label worker skips them.
- [ ] **Step 2: Run them.** `dotnet test --project src/Fulfillment/Bloom.Fulfillment.Tests`. Expect FAIL.
- [ ] **Step 3: Implement** as spec D27–D31. D28 uses the shared `IdempotencyGuard` in its single-transaction form.
- [ ] **Step 4: Run and commit.** Run the project and expect PASS. One commit per decision: `fix(fulfillment): <summary> (D2x)`.

---

### Task 17: Fulfillment decisions, part 2 (D32–D35)

**Files:** `Services/ShippoWebhook.cs` and `Domain/Failures.cs` (carrier scans), `Services/WarehouseService.Returns.cs` (ReceiveReturn), `Consumers/RefundFailedConsumer.cs`, `Domain/RefundEngine.cs` (record `refund_lines` and `refunded_shipping` when the saga starts), `Data/Entities.cs` + migration (D35, R7). Tests: `TrackingTests`, `HandoverTests`, `ReturnTests`, `RefundTests`.

- [ ] **Step 1: Write the failing tests.**
  - `D32_transit_scan_on_label_ready_hands_over_first` and `D32_delivered_scan_on_label_ready_hands_over_first`: the task → `handed_over`, one `shipment-shipped`, `handed_over_at` = the event time, then the normal transition.
  - `D33_expired_return_can_be_received`: then inspection and decision as usual.
  - `D34_delivered_on_lost_sets_delivered_at_keeps_lost_logs_error`: no outbox stock or money message.
  - `D35_refund_failed_restores_refundable_amount`: after the failure, the same refund can be requested again up to the paid amount, and `shipping_refunded` is reset if this refund set it.
  - Review Focus 2: `D35_duplicate_refund_failed_subtracts_once`.
- [ ] **Step 2: Run them.** Expect FAIL.
- [ ] **Step 3: Implement** as spec D32–D35 and R7. Coverages the refund cancelled stay cancelled, with an Error log naming them.
- [ ] **Step 4: Run and commit.** Run the project and expect PASS. One commit per decision.

---

### Task 18: Fulfillment defects (F-1–F-5)

**Files:** `Shippo/ShippoClient.cs`, `Worker/LabelWorker.cs`, `Worker/Sweeps.cs`, `appsettings.json` (read only, for F-4). Tests: `LabelTests`, `WorkerTests`, `ShellTests`, `TrackingTests`, `PackingTests`.

- [ ] **Step 1: Write the failing tests.**
  - `F1_list_labels_tolerates_malformed_unrelated_entry`.
  - `F2_db_failure_during_rollback_moves_row_back_in_line`: `next_attempt_at` advances, and the row is never pinned first.
  - `F3_retry_delay_measured_after_shippo_call`: a fake Shippo that advances the clock 10 s → `next_attempt_at` = after-call time + the delay.
  - `F4_webhook_secret_absent_from_request_logs` (R3).
  - F-5: `Returned_on_possibly_lost_shipment`, `Shippo_pagination_follows_next_and_stops`, `Forwards_user_type_and_role_headers`, `Non_bloom_inventory_UNAVAILABLE_passes_through`, `Sweep_row_failure_does_not_stop_others`.
- [ ] **Step 2: Run them.** Expect FAIL on F-1 to F-3.
- [ ] **Step 3: Implement** F-1 to F-3, and F-4 only if its test fails.
- [ ] **Step 4: Run and commit.** Run the project and expect PASS. Commit `fix(fulfillment): lenient label list, label-worker bookkeeping, retry delay (F-1–F-3)` and `test(fulfillment): log hygiene and coverage gaps (F-4, F-5)`.

---

### Task 19: Order decisions (D17 order side, D18 verify, D19–D22, D33 verify)

**Interfaces:**
- Consumes: `ORDER_RELEASED` from inventory `ReserveStock` (Task 14).

**Files:** `Domain/Upstreams.cs` (map `ORDER_RELEASED` → `CHECKOUT_CLOSED`), `Services/CheckoutService.cs` (resume table), `Consumers/FulfillmentConsumers.cs`, `Domain/Transitions.cs`, `Consumers/PaymentConsumers.cs` / `Domain/Sagas.cs` (D20), `Consumers/SagaConsumers.cs` (D18), `Services/CustomerService.cs` and `Domain/Views.cs` (D22). Tests: `CheckoutTests`, `SweepTests`, `FulfillmentEventTests`, `PaymentTests`, `OrderTests`.

- [ ] **Step 1: Write the failing tests.**
  - `D17_sweep_release_then_late_reserve_is_CHECKOUT_CLOSED`: the fake inventory answers `ORDER_RELEASED`, and no reservation is left.
  - `D18_unmatched_reservation_released_is_noop` (R3).
  - D19, one test per new edge: `shipped` from `paid`; `delivered` from `paid` and from `packing`; `delivery_failed` from `paid` and from `packing`. Plus `D19_late_earlier_event_is_noop` (`shipped` after `delivered`). Each applied edge writes one history row.
  - `D20_refund_failed_then_completed_closes_saga`: the saga is completed, and `sagas_stuck` drops.
  - D21, one test per row: `creating` → continue at step 5; `pending_payment` before the deadline → continue at step 7; Review Focus 5: `pending_payment` with `deadline_at = now()` → `CHECKOUT_CLOSED`; `paid` / `awaiting_stock` → `payment_url = ""`; `cancelled` → `CHECKOUT_CLOSED`.
  - `D22_customer_history_hides_staff_actor_and_reason` and `D22_support_sees_everything`.
  - `D33_return_closed_decided_after_expired_is_handled` (R3).
- [ ] **Step 2: Run them.** `dotnet test --project src/Order/Bloom.Order.Tests`. Expect FAIL on D17 and D19–D22.
- [ ] **Step 3: Implement** as spec D17 and D19–D22, with R9 for D21.
- [ ] **Step 4: Run and commit.** Run the project and expect PASS. One commit per decision.

---

### Task 20: Order defects (O-1–O-4)

**Files:** `Domain/Pricing.cs`, `Worker/Sweeps.cs`, `Domain/Upstreams.cs`, the shared guard's stored-status list (reuse it, don't copy). Tests: `PricingTests`, `SweepTests`, consumer test files, `AccountTests`.

- [ ] **Step 1: Write the failing tests.**
  - `O1_zero_eligible_sum_gives_zero_discount`.
  - `O2_poison_order_skipped_rest_of_batch_commits`: one order makes its step throw. The others in the batch are processed, and the poison order is logged and retried next tick.
  - `O3_is_rejection_matches_guard_stored_statuses`.
- [ ] **Step 2: Run them.** Expect FAIL on O-1 and O-2.
- [ ] **Step 3: Implement.** O-1. O-2 with a savepoint per order (`CreateSavepointAsync` / `RollbackToSavepointAsync`). O-3.
- [ ] **Step 4: O-4.** Consumer tests serialize payloads with `BloomJson`. `AccountTests` pins line order.
- [ ] **Step 5: Run and commit.** Run the project and expect PASS. Commit `fix(order): zero-sum discount, per-order savepoint in sweeps, shared rejection list (O-1–O-3)` and `test(order): BloomJson payloads and pinned line order (O-4)`.

---

### Task 21: Payment decisions (D24–D26)

**Files:** `Worker/ProviderExecutor.cs`, `Stripe/StripeClient.cs` (409 → transient), `Services/StripeWebhook.cs` and `Domain/Transitions.cs` (disputes), `Worker/NightlyJob.cs` (claim), `Data/Entities.cs` + migration `job_runs` (R6). Tests: `RefundTests`, `ReconcileTests`, `WebhookTests`, `WorkerTests`.

- [ ] **Step 1: Write the failing tests.**
  - `D24_409_then_success_creates_one_refund`.
  - `D24_unknown_status_goes_submitted_no_second_create`: `stripe_refund_id` is stored, and an Error is logged.
  - `D24_reconciler_settles_submitted_unknown_refund`.
  - `D24_other_4xx_still_permanent`.
  - `D25_late_created_after_closed_keeps_final_status`: the status stays `won`/`lost`, and the disputes gauge is right.
  - `D26_two_workers_same_night_one_run`.
- [ ] **Step 2: Run them.** `dotnet test --project src/Payment/Bloom.Payment.Tests`. Expect FAIL.
- [ ] **Step 3: Implement** as spec D24–D26, with P1's status mapping merged into D24.
- [ ] **Step 4: Run and commit.** Run the project and expect PASS. One commit per decision.

---

### Task 22: Payment worker defects (P-1–P-5)

**Files:** `Worker/ProviderExecutor.cs`, `Worker/Reconciler.cs`, `Domain/Transitions.cs`, `Observability/PaymentMetrics.cs`, and `Backoff`'s new home (outside `Worker/`, for example `Domain/Backoff.cs`). Tests: `RefundTests`, `ReconcileTests`, `WorkerTests`, `CreatePaymentTests`.

- [ ] **Step 1: Write the failing tests.**
  - `P1_no_retrying_log_when_guarded_update_hits_zero_rows` and `P1_fifth_failure_log_has_exception`.
  - `P4_transition_metric_not_recorded_on_rollback`.
  - `P5_reconciler_calls_stripe_outside_transaction`: no open transaction during the fake's call. `P5_reconciler_batch_cap_100`.
  - P-3: the Review Focus 2 and 5 tests of the payment plan are rewritten to force the lock-wait interleaving (block one transaction on a row lock, then start the second).
- [ ] **Step 2: Run them.** Expect FAIL on P-1, P-4 and P-5.
- [ ] **Step 3: Implement** P-1, P-2 (move `Backoff`; `Domain/Transitions` no longer imports `Bloom.Payment.Worker`), P-4 and P-5.
- [ ] **Step 4: Run and commit.** Run the project and expect PASS. Commit `fix(payment): executor logs, post-commit metrics, reconciler outside tx (P-1, P-2, P-4, P-5)` and `test(payment): forced lock-wait interleavings (P-3)`.

---

### Task 23: Payment API and client defects (P-6–P-11)

**Files:** `PaymentOptions.cs` and `src/Order/Bloom.Order/OrderOptions.cs` (the same check), `Stripe/StripeClient.cs`, `Services/InternalService.cs` (CreatePayment), `Services/StripeWebhook.cs`. Tests: `ShellTests` (both services), `StripeClientTests`, `CreatePaymentTests`, `WebhookTests`, `RefundTests`, `CancelTests`.

- [ ] **Step 1: Write the failing tests.**
  - `P6_relative_v1_base_url_rejected` in payment and order: `/v1` fails startup and names the key.
  - `P7_empty_page_with_has_more_throws_ProviderException`, `P7_out_of_range_timestamp_throws`, `P7_null_id_throws`.
  - `P8_cancelled_request_still_releases_lock_and_runs_tx2`.
  - `P9_succeeded_without_tx2_returns_stored_checkout_url`.
  - `P10_webhook_body_uses_pooled_buffer`: assert behaviour stays the same (a 256 KB body accepted and a 256 KB + 1 body → 413). The pooled buffer is a code-review item, not a test.
  - P-11: `Http_status_fallback_reason`, `Network_error_is_transient`, `Canceled_refund_status`, `Requires_action_refund_status`, `Failed_nightly_fetch_logged_and_skipped`, `Indirect_cancel_noop`.
- [ ] **Step 2: Run them.** Expect FAIL on P-6 to P-9.
- [ ] **Step 3: Implement** P-6 to P-10. For P-8, use `CancellationToken.None` (or a fresh timeout token) for the lock release and Tx 2. For P-9, add a code comment for the `""` case. For P-10, use `ArrayPool<byte>.Shared`.
- [ ] **Step 4: Run and commit.** Run Payment, then Order. Expect PASS. Commit `fix(payment): options, Stripe client guards, CreatePayment token and URL, pooled webhook buffer (P-6–P-10)` and `test(payment): client and refund status coverage (P-11)`.

---

### Task 24: Notification (D39, D40, N-1–N-7, IN-9 notification part)

**Files:** `Worker/EmailSender.cs`, `Email/ResendEmailProvider.cs` (`Retry-After`), `NotificationOptions.cs` (D40), `Templates/TemplateRenderer.cs` or wherever URLs are checked (N-3), `NotificationApp.cs` (N-4), `Worker/DailyPurge.cs` (N-6). Tests: `SenderTests`, `EmailProviderTests`, `ShellTests`, `TemplateTests`, `PurgeTests`, `WorkerTests`.

- [ ] **Step 1: Write the failing tests.**
  - `D39_429_with_retry_after_is_not_counted_and_waits_max`: `Retry-After: 120`, schedule step 30 s → `next_attempt_at` = now + 120 s, and `attempts` is unchanged.
  - `D39_429_without_header_uses_schedule_step`.
  - `D40_malformed_from_fails_startup_naming_key_not_value`.
  - `N2_lease_lost_during_own_send`.
  - `N3_non_http_url_rejected`: `javascript:`, `ftp:` and `file:` are rejected.
  - `N4_redis_connects_at_startup`.
  - `N5_smtp_timeout_path`.
  - `N6_notifications_purge_failure_still_purges_email_jobs`.
- [ ] **Step 2: Test fixes.** N-7: replace the wall-clock budgets in `WorkerTests` with polling on the fake clock. IN-9: notification test seeds use `t.Clock`.
- [ ] **Step 3: Run them.** `dotnet test --project src/Notification/Bloom.Notification.Tests`. Expect FAIL on D39, D40, N-3, N-4 and N-6.
- [ ] **Step 4: Implement** D39, D40, N-3, N-4 and N-6. N-1: fix the comment at `EmailSender.cs:113`.
- [ ] **Step 5: Run and commit.** Run the project and expect PASS. Commit `feat(notification): honour Retry-After on 429 (D39)`, `feat(notification): validate Email:From at startup (D40)`, `fix(notification): url scheme, eager Redis, independent purges (N-1, N-3, N-4, N-6)`, `test(notification): lease, SMTP timeout, fake-clock polling, seeds (N-2, N-5, N-7, IN-9)`.

---

### Task 25: Launch decisions (D4–D7)

**Files:** `WaitingRoom/AdmissionScript.cs` (D4: the tick key first; L-1's `PassPrefix` is Task 26), `WaitingRoom/RedisKeys.cs` (`Tick(launchId)` = `wr:{launchId}:tick`), `Services/InternalService.cs` (HoldAllocation steps 2 and 3), `Worker/AdmissionWorker.cs` (close, D7 log throttle), `Worker/StageScheduler.cs`, `Services/AdminService.cs` (stage changes `FOR UPDATE`), `Consumers/*` (settlement-side close), `Worker/StuckHoldMonitor.cs` (D7). Tests: `AdmissionTests`, `HoldTests`, `SettlementTests`, `WorkerTests`.

- [ ] **Step 1: Write the failing tests.**
  - `L07_two_workers_500ms_apart_admit_at_most_rate_per_second`: over 10 s of fake time, total admitted ≤ 10 × `admission_rate`.
  - Review Focus 4: `L07_two_workers_same_instant_one_pops`.
  - `D5_hold_blocked_mid_transaction_delays_close_and_close_sees_hold`: forced interleaving. The hold holds the `launches` row `FOR SHARE`, and the close's `FOR UPDATE` waits, then evaluates with the hold counted.
  - `D6_retry_with_different_account_is_HOLD_CONFLICT` and `D6_retry_with_different_launch_is_HOLD_CONFLICT`.
  - `D7_redis_down_logs_error_once_per_minute_per_launch`: 120 ticks → 2 Errors.
  - `D7_stuck_hold_logs_error_once_when_first_stuck`.
- [ ] **Step 2: Run them.** `dotnet test --project src/Launch/Bloom.Launch.Tests`. Expect FAIL.
- [ ] **Step 3: Implement** as spec D4–D7. The lock order is: `launches`, then products, then SKUs.
- [ ] **Step 4: Run and commit.** Run the project and expect PASS. Commit `fix(launch): admission paced per launch (D4, LLD-ADR-012)`, `fix(launch): hold vs close/stage change row lock (D5)`, `fix(launch): idempotent hold compares account and launch (D6)`, `fix(launch): throttle admission and stuck-hold errors (D7)`.

---

### Task 26: Launch defects (L-1–L-6)

**Files:** `WaitingRoom/RedisKeys.cs`, `WaitingRoom/AdmissionScript.cs`, `Consumers/OrderPaidConsumer.cs` (move `Moved` and `HoldCounters` out, to `Domain/` with one shared `Locked` record), `Services/AdminService.cs` (L-4), comments named in L-3. Tests: `AdminSetupTests`, `AdminEditTests`, `CustomerTests`, `AdmissionTests`, `SettlementTests`, `WorkerTests`, `Infrastructure/*` (L-6).

- [ ] **Step 1: Write the failing test.** `L4_admin_view_read_in_one_snapshot`: a concurrent hold commits between the reads. The returned view is internally consistent, from one read-only transaction.
- [ ] **Step 2: Run it.** Expect FAIL.
- [ ] **Step 3: Implement** L-1 to L-4, with no behaviour change except L-4.
- [ ] **Step 4: Tests, L-5:** `Audit_details_for_adds`, `AddLaunchSku_stage_recheck`, `List_tie_break`, `Join_and_status_over_http`, `Redis_failure_at_pop`, a forced interleaving in `Raising_allocation…`, and the 29-minute stuck-hold gauge asserted directly. **L-6:** `CommandCounter` sync overrides, a wider catalog-timeout range, and the two vacuous `InRange` assertions replaced with exact or bounded ones.
- [ ] **Step 5: Run and commit.** Run the project and expect PASS. Commit `refactor(launch): key constants, shared records, one-snapshot admin view (L-1–L-4)` and `test(launch): coverage gaps and test nits (L-5, L-6)`.

---

### Task 27: MinIO in dev compose (D41)

**Files:** `deploy/compose/docker-compose.yml`. Read the catalog `appsettings.json` and `CatalogOptions.cs` for the bucket config names first.

- [ ] **Step 1:** Add the service `minio` (`alpine/minio:RELEASE.2025-10-15T17-29-55Z`) and a one-shot `minio-init` that creates the bucket(s) named in catalog's config. Set the catalog environment to point at it, using catalog's config keys. Change nothing else in the file.
- [ ] **Step 2: Verify.** `docker compose -f deploy/compose/docker-compose.yml config --quiet` exits 0. Don't start the stack.
- [ ] **Step 3: Commit.** `feat(compose): MinIO and bucket init for catalog images (D41)`.

---

### Task 28: Full verification

- [ ] **Step 1:** `dotnet build -c Release` → 0 warnings, 0 errors.
- [ ] **Step 2:** Run each test project on its own, in this order: Shared, Gateway, Identity, Catalog, Inventory, Fulfillment, Order, Payment, Notification, Launch. All pass. Record the pass counts in the ledger.
- [ ] **Step 3:** `docker ps -a` shows no Testcontainers leftovers. `git status` is clean.
- [ ] **Step 4:** `git diff --stat feat/launch -- doc` lists only the files of Tasks 2–6. Each listed LLD and product document has `Approved (2026-11-12)`.
- [ ] **Step 5:** Every ID of spec §3 (D1–D41), §4 and §5 is ticked in the ledger with its commit SHA, or marked superseded (I11, F8). A missing ID is a stop.
