# Audit follow-ups Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Merge PRs #16–#22 into `fix/audit-followups` and close every follow-up the spec lists (B–F), ready for one PR to `main`.

**Architecture:** Task 1 integrates the seven branches with merge commits. Tasks 2–6 are small code and test changes, each in one service or in Bloom.Shared. Task 7 is the doc-only edits. Doc commits never mix with code commits.

**Tech Stack:** .NET 10, EF Core + Npgsql, StackExchange.Redis, RabbitMQ.Client, xUnit v3 on Microsoft.Testing.Platform, Testcontainers.

**Spec:** `docs/superpowers/specs/2026-11-14-audit-followups-design.md` (approved 2026-11-14). Read it with this plan. Doc before/after strings are in the spec; this plan points to them and does not copy them.

## Global Constraints

- AGENTS.md: edit `doc/` only as the spec says (§B, §C, §D4, §F). No other doc change.
- No commit mixes `doc/` and code.
- No behaviour change beyond the spec. D1, D2, D3 and D5 do not change behaviour.
- Database-clock rule (00-reliability §10): stored times and time checks use PostgreSQL `now()`.
- Never commit, merge or push into `main`. Never run `gh pr merge`.
- Worktree: `/Users/datnguynx/Project/Bloom/Bloom_Server/.claude/worktrees/fix+audit-followups`. Use plain, separate git commands from inside it: no `git -C`, no `&&` chains of git commands.
- Tests: one project at a time, `dotnet test --project src/<Svc>/Bloom.<Svc>.Tests [--filter-class '*<Class>'] [--filter-method '*<Method>']`. Full suite: `dotnet build Bloom.slnx` then `dotnet test --no-build --max-parallel-test-modules 1` (about 6 min). Never `dotnet test Bloom.slnx`: it runs 0 tests.
- Docker runs on Colima and Testcontainers needs it. Never run `colima`. If the daemon is down, stop and report.
- After test runs, `docker ps -a` must show no leftover containers.
- Commit trailer on every commit:
  ```
  Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>
  Claude-Session: https://claude.ai/code/session_01FoK3gYBMpoqDeJGGi6Q7zV
  ```

## Review Focus

1. **Relay on a re-published row:** a row that failed once and is published on a later pass gets its insert time (`created_at`), not its publish time. Task 2's test sets `created_at` a day back, so it covers this case.
2. **Payload fidelity through the relay rewrite:** non-ASCII strings and nested objects must reach the broker with the same values. Task 2's test uses `"Zoë"` and a nested object.
3. **Refresh when the account row is missing:** the folded lock query returns no row. Behaviour must not change: the token is revoked and the call answers `REFRESH_INVALID`. Task 3 adds a test.
4. **Limit upsert with partial room:** an existing row with 1 unit left and a request for 2 must fail with `LIMIT_EXCEEDED`, `remaining = 1`, and leave the row unchanged. Task 4 adds a test.
5. **Two missing SKUs:** the error names the first one in `sku_id` order, whatever order the request used. Task 4 adds a test (spec E6).

---

### Task 1: Integrate PRs #16–#22 (spec §A)

**Files:** merge results only. The conflict files the spec expects:
- `doc/README.md`: the HLD line becomes `HLD-ADR-001 … 021`.
- `doc/high-level-design/high-level-design.md`: the header says `(HLD-ADR-001 … 021)`. The §10 table keeps both the 020 row and the 021 row, in that order. The §9.5 row from #17 and the QA-03 row from #18 both stay.
- `doc/high-level-design/adr/README.md`: both rows, 020 then 021.
- `src/Order/Bloom.Order/Services/CheckoutService.cs`: keep every side's change.

**Interfaces:**
- Produces: the integrated tree that every later task builds on. From #16 it brings `DatabaseFacade.NowAsync(ct)` (`Bloom.Shared.Persistence.DatabaseClock`) and the per-service test DB-now helpers in `Test<Svc>.cs`.

- [ ] **Step 1: Merge, in this order, with merge commits (`git merge --no-ff <ref>`, one command at a time):** `origin/fix/db-clock`, `origin/fix/health-endpoints`, `origin/fix/checkout-latency`, `origin/fix/waiting-room-batch`, `origin/fix/hold-round-trips`, `origin/fix/checkout-round-trips`. Run `git fetch origin` first. Resolve conflicts as listed above. Each merge commit message is `Merge <ref> (#NN) into fix/audit-followups`, plus the trailer.
- [ ] **Step 2: Check that no conflict markers are left.** Run `grep -rn '^<<<<<<<\|^>>>>>>>' src doc`. Expected: no output.
- [ ] **Step 3: Build.** Run `dotnet build Bloom.slnx -warnaserror`. Expected: `0 Warning(s)`, `0 Error(s)`.
- [ ] **Step 4: Run the full suite.** Run `dotnet test --no-build --max-parallel-test-modules 1`. Expected: every module passes. A failure here is an integration defect: fix it in this task in a separate commit (`fix: reconcile <x> after merging #NN`), and record the cause in the ledger.

### Task 2: `occurredAt` from the outbox row's `created_at` (spec §B)

**Files:**
- Modify: `src/Shared/Bloom.Shared/Messaging/OutboxRelay.cs` (`PublishAsync`)
- Modify: `src/Shared/Bloom.Shared/Messaging/OutboxWriter.cs:36`
- Modify: `src/Identity/Bloom.Identity.Tests/Infrastructure/Outbox.cs` (`LatestCodeAsync`)
- Test: `src/Shared/Bloom.Shared.Tests/OutboxRelayTests.cs`, `src/Shared/Bloom.Shared.Tests/OutboxWriterTests.cs`
- Doc (separate commit): `doc/low-level-design/00-reliability.md` §5.2, the `occurredAt` row, using the spec §B after-text.

**Interfaces:**
- Consumes: `OutboxMessage.CreatedAt` (a `DateTime` with UTC kind, read by the relay's `SELECT *`), and `Envelope.Parse` / `Envelope.ToUtf8Bytes`.
- Produces: the published body equals the stored envelope with `OccurredAt = row.CreatedAt`. Nothing else changes, in the body or in the properties.

- [ ] **Step 1: Write the failing test** `Published_occurredAt_is_the_rows_created_at` in `OutboxRelayTests`. Write one event with payload `new { name = "Zoë", nested = new { a = 1 } }`. Then run `UPDATE outbox_messages SET created_at = now() - interval '1 day'`. Run `RunOnceAsync` and `BasicGetAsync` the message. Then assert:
  ```csharp
  var stored = Envelope.Parse(Encoding.UTF8.GetBytes(row.Payload));
  var sent = Envelope.Parse(got.Body.Span);
  Assert.Equal(row.CreatedAt, sent.OccurredAt);
  Assert.Equal((stored.MessageId, stored.MessageType, stored.Kind, stored.Producer, stored.CorrelationId, stored.CausationId),
               (sent.MessageId, sent.MessageType, sent.Kind, sent.Producer, sent.CorrelationId, sent.CausationId));
  Assert.Equal("Zoë", sent.Payload.GetProperty("name").GetString());
  Assert.Equal(1, sent.Payload.GetProperty("nested").GetProperty("a").GetInt32());
  ```
- [ ] **Step 2: Run it.** Run `dotnet test --project src/Shared/Bloom.Shared.Tests --filter-method '*Published_occurredAt_is_the_rows_created_at'`. Expected: FAIL, because `OccurredAt` is the writer's clock.
- [ ] **Step 3: Implement.** In `PublishAsync`, send `(Envelope.Parse(Encoding.UTF8.GetBytes(row.Payload)) with { OccurredAt = row.CreatedAt }).ToUtf8Bytes()` instead of the raw payload bytes. Add a comment that cites 00-reliability §5.2 and §10. In `OutboxWriter`, pass `default` for `OccurredAt`, with the comment `// set by the relay from created_at (00-reliability §5.2)`. `DateTime.UtcNow` is then gone from the writer.
- [ ] **Step 4: Update the existing assertions.**
  - `T02_confirmed_row_marked_sent`: replace `Assert.Equal(row.Payload, …body…)` with the same field-by-field comparison as Step 1, plus `OccurredAt == row.CreatedAt`.
  - `Committed_event_row_has_envelope_and_columns`: add `Assert.Equal(default, env.OccurredAt);`.
  - Identity `LatestCodeAsync`: drop `.OrderBy(x => x.OccurredAt)`. `EnvelopesAsync` is already ordered by id (UUIDv7), so `.Last()` stays newest.
- [ ] **Step 5: Run the affected tests.** Run `dotnet test --project src/Shared/Bloom.Shared.Tests`, then `dotnet test --project src/Identity/Bloom.Identity.Tests`, then `dotnet test --project src/Notification/Bloom.Notification.Tests`, then `dotnet test --project src/Catalog/Bloom.Catalog.Tests`. The last two hold the newest-wins consumers. Expected: all PASS.
- [ ] **Step 6: Commit the code** as `fix(shared): set the envelope's occurredAt from the outbox row's insert time`.
- [ ] **Step 7: Commit the doc** in a separate commit: the 00-reliability §5.2 row, as `docs(lld): occurredAt is the outbox row's database insert time`.

### Task 3: Identity round trips (spec §D1, §D2)

**Files:**
- Modify: `src/Identity/Bloom.Identity/Services/StaffService.cs` (`AcceptStaffInvitation`)
- Modify: `src/Identity/Bloom.Identity/Sessions/SessionService.cs` (`TryRotateAsync`, `LockAccountAsync`)
- Test: `src/Identity/Bloom.Identity.Tests/SessionTests.cs`

**Interfaces:**
- Produces: `private async Task<DateTimeOffset?> LockAccountAsync(Guid accountId)`. It runs `SELECT now() AS "Value" FROM accounts WHERE id = {accountId} FOR UPDATE` and returns null when no account row exists. The other caller (the family revoke, about line 115) ignores the result.

- [ ] **Step 1: Write the guard test** `Refresh_of_a_token_whose_account_row_is_gone_is_REFRESH_INVALID_and_revokes_it` in `SessionTests`. Sign in, `DELETE FROM accounts WHERE id = …` through SQL, then refresh. Expect `REFRESH_INVALID`, and the token row's status to be `revoked`. This pins today's behaviour (the `account is null` branch) before the refactor.
- [ ] **Step 2: Run it.** Run `dotnet test --project src/Identity/Bloom.Identity.Tests --filter-method '*Refresh_of_a_token_whose_account_row_is_gone*'`. Expected: PASS on the current code. It is a guard: if it fails, record in the ledger what it shows, and change the test's assertions to match today's behaviour.
- [ ] **Step 3: D2.** In `TryRotateAsync`, remove `var now = await db.Database.NowAsync();` and take `now` from `LockAccountAsync`. When that returns null, fall back to `await db.Database.NowAsync()`. This keeps the existing `account is null` branch: it is rare, and only that path pays the extra round trip. Inside the transaction the value is the same `now()` as before.
- [ ] **Step 4: D1.** In `AcceptStaffInvitation`, compute `var passwordHash = passwordHasher.Hash(password);` before `BeginTransactionAsync`, after `VerifyFirstCodeAsync`. Use `passwordHash` in the `Account` initializer.
- [ ] **Step 5: Run the Identity tests.** Run `dotnet test --project src/Identity/Bloom.Identity.Tests`. Expected: all PASS.
- [ ] **Step 6: Commit** as `perf(identity): hash before the invitation transaction; read now() with the refresh account lock`.

### Task 4: Launch join and hold (spec §D3, §D4, §D5, §E5, §E6)

**Files:**
- Modify: `src/Launch/Bloom.Launch/Services/CustomerService.cs` (`JoinWaitingRoom`, lines 48–51 after #19)
- Modify: `src/Launch/Bloom.Launch/Services/InternalService.cs` (the step-3 comment; step 5)
- Test: `src/Launch/Bloom.Launch.Tests/HoldTests.cs`
- Doc (separate commit): `doc/low-level-design/01-launch.md` §6.5 step 5 and the §7 row "Exceeding the limit", using the spec §D4 after-text.

**Interfaces:**
- Produces: `private static async Task<T> ObservedPollAsync<T>(Task<T> call, Func<Task<bool>> condition, string what)` in `HoldTests`. It polls like `Poll.UntilAsync(condition, 15, what)`. If the poll times out and `call` has completed, it awaits `call` first, so the call's own exception is what the test reports. Every `Poll.UntilAsync(() => WaiterAsync(...))` in the D5 tests and in `Concurrent_first_calls_of_one_order_with_other_passes_conflict` uses it.

- [ ] **Step 1: Write the new tests.**
  - `Two_missing_skus_name_the_first_in_sku_id_order`: take two random SKU ids `x < y` (ordinal `ToString("D")`), not in the launch. Send them in the order `(y, x)`. Assert `Rpc.Info(e).Reason == SKU_NOT_IN_LAUNCH` and `e.Status.Detail == $"SKU {x:D} is not in this launch."`, where `e` comes from `Assert.ThrowsAsync<RpcException>`.
  - `Limit_with_one_unit_left_refuses_two_and_reports_remaining_1`: product limit 3. One hold of 2 commits. A second hold of 2, with a new pass, fails with `LIMIT_EXCEEDED`, metadata `remaining = "1"` and `productId` = the product. The `limit_usage` row stays `held = 2`. End with `Holds.InvariantAsync`.
  - In `Concurrent_first_calls_of_one_order_with_other_passes_conflict`, add `await Holds.InvariantAsync(t, second.Id);`.
- [ ] **Step 2: Run them.** Run `dotnet test --project src/Launch/Bloom.Launch.Tests --filter-class '*HoldTests'`. Expected: PASS. These pin current behaviour; the code paths already exist.
- [ ] **Step 3: Add `ObservedPollAsync` and switch the five lock-wait polls to it** (2 in `D5_hold_blocked_mid_transaction…`, 1 in `D5_hold_waiting_behind…`, 2 in `Concurrent_first_calls…`). For the `close` poll, observe `close`; for the others, observe the hold call that is expected to wait.
- [ ] **Step 4: D4.** Replace step 5's `INSERT … ON CONFLICT DO NOTHING` and `UPDATE` with one statement:
  ```sql
  INSERT INTO limit_usage (launch_id, account_id, product_id, held, sold) VALUES ({launchId}, {caller.UserId}, {p.Product}, {p.Sum}, 0)
  ON CONFLICT (launch_id, account_id, product_id) DO UPDATE SET held = limit_usage.held + {p.Sum}
  WHERE limit_usage.held::bigint + limit_usage.sold + {p.Sum} <= {p.Limit}
  ```
  `moved == 1` continues. The `p.Sum <= p.Limit` pre-check (R11), the rollback, the `remaining` read and the product order all stay.
- [ ] **Step 5: D5.** Reword the step-3 comment's last sentence: SKU membership and purchase limits are frozen from `preorder_open` (§6.7, LCH-R25); allocation can still rise, but step 3 does not read it.
- [ ] **Step 6: D3.** In `JoinWaitingRoom`, start the `SortedSetAddAsync(…, When.NotExists)`, the pass `StringGetAsync` and the `SortedSetRankAsync`, in that order, with no `await` between them. Then `await Task.WhenAll(add, pass, rank)`. Replace the comment with one that says: the multiplexer sends them in order on one connection, so the rank reflects the ZADD (two round trips in total), and the pass is read again because admission can admit the customer between the ZADD and the rank.
- [ ] **Step 7: Run the Launch tests.** Run `dotnet test --project src/Launch/Bloom.Launch.Tests`. Expected: all PASS.
- [ ] **Step 8: Commit the code** as `perf(launch): one limit_usage upsert in the hold and a pipelined join` (tests in the same commit).
- [ ] **Step 9: Commit the doc** in a separate commit: 01-launch §6.5 step 5 and the §7 row, as `docs(launch): the hold's limit check is one upsert (§6.5)`.

### Task 5: Launch worker test flake (spec §E1)

**Files:**
- Test: `src/Launch/Bloom.Launch.Tests/WorkerTests.cs` (`Worker_admits_and_schedules_on_its_own`, about line 137)
- Modify: whatever the root cause points to. Code changes are allowed only if they stay within 01-launch's design. If the cause needs a design change, stop and report it as a blocker.

- [ ] **Step 1: Use superpowers:systematic-debugging.** Read the test, `Seed.LaunchAsync` (`launchIn` is relative to `t.Clock`), and how the worker's scheduler and admission loop read time and Redis. Write the hypothesis in the ledger before changing anything. Candidates to rule in or out: the 2 s `launchIn` racing the join under load, the API host's fake clock against the worker's clock, and the 15 s poll budgets against the scheduler and admission tick periods.
- [ ] **Step 2: Reproduce it under load.** Run `dotnet test --project src/Launch/Bloom.Launch.Tests --filter-method '*Worker_admits_and_schedules_on_its_own'` 20 times, sequentially, while another Launch test class runs in parallel. Record the failure rate, or "not reproduced" with the evidence.
- [ ] **Step 3: Fix the root cause** (the test's timing assumptions, or the code), with the smallest change that removes the race.
- [ ] **Step 4: Verify.** Run the same loop 20 times. Expected: 20/20 PASS. Then run `dotnet test --project src/Launch/Bloom.Launch.Tests`: all PASS.
- [ ] **Step 5: Commit** as `test(launch): <root cause in a few words>` or `fix(launch): …`. The commit body states the root cause.

### Task 6: Test hygiene (spec §E2, §E3, §E4, §E7)

**Files:**
- Modify: `src/Fulfillment/Bloom.Fulfillment.Tests/**`, `src/Notification/Bloom.Notification.Tests/**` (`Infrastructure/Seed.cs` lines 17, 26, 49, 66, 73 on `main`), `src/Payment/Bloom.Payment.Tests/**`
- Modify: `src/Identity/Bloom.Identity.Tests/InvitationTests.cs` (`Invite_expires_exactly_at_72h`)
- Modify: `src/Payment/Bloom.Payment.Tests/RefundTests.cs` (the `D24_*` tests)
- Modify: `src/Order/Bloom.Order.Tests/CheckoutTests.cs` (`The_payment_read_is_one_statement_that_compares_the_deadline_with_the_database_clock`)

**Interfaces:**
- Consumes: each service's test DB-now helper from #16, in `Infrastructure/Test<Svc>.cs`. Use the existing name; don't add a second helper.

- [ ] **Step 1: E2.** In the three test projects, find every place that writes a database row stamped with `t.Clock.GetUtcNow()` (or the fake clock's value). Run `grep -rn 'Clock.GetUtcNow' src/{Fulfillment,Notification,Payment}/*.Tests`. Stamp those rows with the DB-now helper instead. Leave alone values that are not stored rows: webhook signature times, FakeStripe/FakeShippo provider times, envelope `occurredAt` that a test passes on purpose. List in the ledger which of these you left, with the reason.
- [ ] **Step 2: E3.** Rename `Invite_expires_exactly_at_72h` to `Invite_is_accepted_a_minute_before_expiry_and_refused_once_expired`. Ruling: the exact `>` boundary cannot be pinned against the database clock without controlling `now()`, so the name changes, not the test. Update any comment that claims "exactly".
- [ ] **Step 3: E4.** Delete the `Clock.Advance` calls in the `D24_*` tests of `RefundTests`, and any local variable they leave unused.
- [ ] **Step 4: E7.** Replace `Assert.Contains("o.deadline_at <= now()", sql)` with `Assert.Matches(@"(\w+\.)?""?deadline_at""? <= now\(\)", sql);`. Keep the comment.
- [ ] **Step 5: Run the affected projects, one at a time.** Run `dotnet test --project src/Fulfillment/Bloom.Fulfillment.Tests`, then Notification, Payment, Identity and Order the same way. Expected: all PASS.
- [ ] **Step 6: Commit,** one commit per service: `test(<svc>): …`.

### Task 7: Doc edits (spec §C, §F)

**Files (doc only, no code):**
- Create: `doc/high-level-design/adr/HLD-ADR-022-stripe-rate-limit.md`, with the structure of `HLD-ADR-019-consumers-pause-during-own-db-outage.md` and the content of spec §C.
- Modify: `doc/high-level-design/high-level-design.md`. The header becomes `HLD-ADR-001 … 022` (Status and Last updated are already 2026-11-14 after Task 1). Add the §10 row for 022, and the §11 row R-11 after R-09 (the last row), as in spec §C. In §9.3 key signals, apply the spec §F2 change.
- Modify: `doc/high-level-design/adr/README.md` (add the 022 row) and `doc/README.md` (`HLD-ADR-001 … 022`).
- Modify: `doc/high-level-design/adr/HLD-ADR-020-health-endpoints.md`: the three edits of spec §F1.
- Modify: `doc/low-level-design/01-launch.md` (LR-05) and `doc/low-level-design/02-inventory.md` (IR-08), as in spec §F3. In both headers, Status becomes `Approved (2026-11-14)` and Last updated `2026-11-14`.
- Modify: `doc/low-level-design/07-api-gateway.md` §10 Alerts: append the spec §F4 clause before the final period. Header: Status `Approved (2026-11-14)`, Last updated `2026-11-14`.

- [ ] **Step 1: Make the edits,** using the spec's strings word for word.
- [ ] **Step 2: Check the links and IDs.** Run `grep -rn 'HLD-ADR-022' doc`. Expected: the ADR file, the HLD §10 and §11 rows, `adr/README.md`, and the `doc/README.md` range. Run `grep -n 'R-11' doc/high-level-design/high-level-design.md`. Expected: one row. Every relative link in the new ADR resolves: `ls` each target.
- [ ] **Step 3: Commit,** in upstream-first order: `docs(hld): HLD-ADR-022 Stripe rate limit and R-11`, then `docs(hld): HLD-ADR-020 drain and probe notes; QA-03 signal source`, then `docs(lld): LR-05, IR-08 budget wording; gateway clock-offset alert`.

---

## After the last task

Final review per the workflow: one `sp-final-reviewer` and one `sp-ponytail-reviewer` in parallel, one `sp-implementer` fix wave, one scoped re-review. Then the full suite (`dotnet build Bloom.slnx -warnaserror`, then `dotnet test --no-build --max-parallel-test-modules 1`), push, and `gh pr create --base main`. The PR body says that #16–#22 are included and that the owner closes them after merging. After the push, remove all worktrees (owner request; spec success criteria).

## Builder rulings (for the owner to check)

- P1: `OutboxWriter` stores `occurredAt = default` (0001-01-01). The relay overwrites it at publish time, so the stored row never carries the app clock.
- P2: when the account row is missing, the D2 refresh falls back to one `NowAsync` read. This keeps today's revoke-on-missing-account behaviour.
- P3: E3 renames the test rather than pinning `>` exactly, because the database clock can't be frozen.
- P4: D1, D2 and D3 get no new round-trip-count test. They don't change behaviour, and the existing suites cover them.
