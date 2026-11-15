# Audit follow-ups: one integration PR — design

Date: 2026-11-14 · Owner: project owner · Branch: `fix/audit-followups` (base `main`)

## Intent

The production-readiness audit produced seven open PRs (#16–#22). The owner wants them, and every item those PRs left open, delivered as **one PR to `main`** that the owner merges once (Claude may not merge into `main`). Afterwards every worktree is removed.

## Goals

1. One branch containing #16–#22, their overlaps resolved.
2. Every open item from those PRs resolved, in the groups the owner chose: the two design decisions, the code follow-ups, test hardening, doc notes.
3. All doc edits below — the owner approved editing the docs for these items ("edit all docs").

## Non-goals

- The "pre-existing behaviour" group (Kestrel port in code, worker survival when RabbitMQ is down at startup, probe tracing, Identity's repeated DB-now reads, `Views.cs` lazy coverage reads). Owner excluded it.
- Items already ruled "no action" in the PRs and not listed below (e.g. #18 logging-time and wall-margin minors, #19 load test, extra `SELECT now()` round trips other than the refresh one).
- Writing Kubernetes manifests or a launch runbook.

## Anti-goals

- Any behaviour change not listed here.
- Rewriting an accepted ADR that is already on `main` (HLD-ADR-013's consequence line stays; doc/README rule). ADR-020 is not on `main` yet, so it is edited in place.

## Constraints

- AGENTS.md: docs decide; every change cites an ID; doc edits only as written here.
- Workflow: no merge into `main`, no `gh pr merge`; the owner merges and closes #16–#22.
- Database-clock rule (00-reliability §10, PR #16): stored times and time comparisons use PostgreSQL `now()`.
- Tests: `dotnet test --project <proj>` per project or `dotnet test --no-build --max-parallel-test-modules 1` for the suite (Colima VM is small).

## Design

### A. Integration (merge order and conflicts)

From `main` (0218e5c), merge into `fix/audit-followups` with merge commits, in this order: `origin/fix/db-clock` (#16), `origin/fix/health-endpoints` (#17), `origin/fix/checkout-latency` (#18), `origin/fix/waiting-room-batch` (#19), `origin/fix/hold-round-trips` (#21, which contains #20), `origin/fix/checkout-round-trips` (#22, which contains #16).

Known overlaps and their resolution:
- #17 and #18 both add an HLD ADR (020, 021): the HLD header and `doc/README.md` say `HLD-ADR-001 … 021` (then 022 after §C); the HLD §10 table and `adr/README.md` keep both rows, in number order.
- #16, #18, #22 all edit `CheckoutService.cs`: already shown to merge cleanly (#22 review); resolve any textual conflict by keeping every side's change.
- The full suite must pass after the integration, before any follow-up.

### B. `OccurredAt` from the database (owner choice: DB insert time)

`OutboxWriter` (Bloom.Shared) stops being the source of the envelope's `occurredAt`. The relay sets `occurredAt` to the outbox row's `created_at` (database time at insert, already filled by the database) when it publishes. Payload, headers and every other envelope field are unchanged; message types and versions are unchanged. Consumers (Notification contacts, Catalog reviewer_names newest-wins keys) need no change.

Doc edit, `doc/low-level-design/00-reliability.md` §5 envelope table, row `occurredAt`:
- before: `| `occurredAt` | RFC 3339 UTC | ✓ | When the business change committed |`
- after: `| `occurredAt` | RFC 3339 UTC | ✓ | When the business change committed: the database time of the outbox row's insert (`outbox_messages.created_at`), set by the relay when it publishes (§10) |`

### C. Stripe rate limit (owner choice: document + raise the limit) — new HLD-ADR-022

New file `doc/high-level-design/adr/HLD-ADR-022-stripe-rate-limit.md` (HLD-ADR-019's structure):

- Title: `HLD-ADR-022: Stripe rate limit sized to the launch admission rate`
- Header: Status Accepted · Date 2026-11-14 · Deciders Project owner · Related: QA-05, HLD-ADR-012, HLD-ADR-013, HLD-ADR-018, R-11
- Context: QA-05 sustains 200 checkouts/s at a launch, and every checkout creates one Stripe Checkout Session synchronously (HLD-ADR-013, LLD-ADR-007). Stripe's default API rate limit (about 100 requests/s in live mode, 25 in test mode) is below that. Payment treats a 429 as transient: `CreatePayment` answers `UNAVAILABLE`, the order stays `pending_payment`, and the customer retries within the 15-minute checkout.
- Decision drivers: keep QA-05; no new code path on the checkout; no customer-visible change.
- Considered options: 1. Raise the account's Stripe rate limit before a launch, sized to the admission rate · 2. Cap the admission rate at the Stripe limit (lower QA-05) · 3. Create Checkout Sessions asynchronously.
- Decision: **1**. Before a launch, the operator confirms the Stripe account's rate limit covers the launch's configured admission rate (checkouts/s) with margin, and requests an increase from Stripe when it does not. The 429 handling is unchanged.
- Pros/cons: 1 ✅ keeps QA-05, no code ❌ an operational step, depends on Stripe granting it · 2 ✅ no dependency on Stripe ❌ lowers QA-05, slower launches · 3 ✅ decouples Stripe ❌ the client needs a second call or polling (rejected in HLD-ADR-021 / LLD-ADR-001).
- Consequences: ✅ QA-05 kept with no code change · ❌ a launch depends on a checked Stripe limit · ⚠️ the launch runbook (not yet written) must include the check; risk R-11 tracks it.
- Links: HLD-ADR-013, HLD-ADR-021, high-level-design.md §7.3, §11.

HLD edits (`doc/high-level-design/high-level-design.md`):
- Header: `Decision records … (HLD-ADR-001 … 022)`; Status `Approved (2026-11-14)`, Last updated `2026-11-14`.
- §10 ADR table: row `| [HLD-ADR-022](adr/HLD-ADR-022-stripe-rate-limit.md) | Stripe rate limit sized to the launch admission rate |`.
- §11 Risks: new row after R-09 (the last row): `| R-11 | Stripe's API rate limit (about 100 requests/s by default) is below QA-05's 200 checkouts/s; each checkout creates one Checkout Session. | Raise the account's limit before a launch, sized to the admission rate ([HLD-ADR-022](adr/HLD-ADR-022-stripe-rate-limit.md)); a 429 is transient and the customer retries. |`
- `adr/README.md` index row: `| [HLD-ADR-022](HLD-ADR-022-stripe-rate-limit.md) | Stripe rate limit sized to the launch admission rate | Accepted | 2026-11-14 |`; `doc/README.md` map: `HLD-ADR-001 … 022`.

### D. Code follow-ups

1. **Identity `AcceptStaffInvitation`** (#16): hash the password (Argon2id) before `BeginTransactionAsync`, so the pooled connection is not held during hashing. Behaviour unchanged.
2. **Identity refresh** (#16): fold the refresh path's separate database-now read into its account `FOR UPDATE` query (one round trip less). Behaviour unchanged.
3. **Launch `JoinWaitingRoom`** (#19): after the pre-`ZADD` pass check, issue the `ZADD`, the pass GET and the `ZRANK` together (no await between; Redis runs them in order on the connection), so the final status still reflects the `ZADD`. Join goes from three Redis round trips to two. The pass is still read twice: the second read is needed because admission can admit the customer between the `ZADD` and the rank (Claude's call: "read the pass once" would lose that case).
4. **Launch hold `limit_usage` upsert** (#21): §6.5 step 5's two statements become one upsert. Doc edit, `doc/low-level-design/01-launch.md` §6.5 step 5:
   - after: `5. For each product, **in ascending `product_id` order**: `INSERT INTO limit_usage (launch_id, account_id, product_id, held, sold) VALUES (@L, @A, @P, @n, 0) ON CONFLICT (launch_id, account_id, product_id) DO UPDATE SET held = limit_usage.held + @n WHERE limit_usage.held + limit_usage.sold + @n <= @limit`. 0 rows → `ROLLBACK`, `LIMIT_EXCEEDED`. A new row needs `@n <= @limit`, checked before the statement (R11).`
   - §7 table, row "Exceeding the limit": `Guarded update on limit_usage (§6.5 step 5)` → `Guarded upsert on limit_usage (§6.5 step 5)`.
   - Outcomes, `LIMIT_EXCEEDED` metadata (`productId`, `remaining`) and lock order unchanged.
5. **Launch hold comment** (#21): the step-3 comment says SKU membership and purchase limits are frozen from `preorder_open`; allocation can still rise, but step 3 does not read it.

### E. Test hardening

1. `WorkerTests.Worker_admits_and_schedules_on_its_own` (Launch) flaked once under a full run: find the root cause (systematic debugging) and fix it in the test or the code it reveals.
2. Fulfillment, Notification and Payment test seeds that still stamp rows with the fake host clock: stamp them with database time.
3. Identity `Invite_expires_exactly_at_72h`: pin the exact `>` boundary again, or rename it to what it checks.
4. Payment `RefundTests` D24_*: drop the dead `Clock.Advance` calls.
5. Launch `HoldTests`: `Concurrent_first_calls_of_one_order_with_other_passes_conflict` also asserts `InvariantAsync` for the second launch; the D5-style tests observe their call tasks when a poll times out.
6. Launch: a test naming the first missing SKU (in `sku_id` order) when two requested SKUs are missing (`SKU_NOT_IN_LAUNCH`).
7. Order: the PayAsync SQL-shape test matches `<= now()` without depending on EF's table alias.

### F. Doc notes

1. **HLD-ADR-020** (not yet on `main`, edited in place):
   - Decision drivers, first bullet: `A dependency blip must not pull every pod out of rotation at once (cascading failure, QA-01).`
   - Consequences: `✅ Shutdown drains API traffic.` → `✅ Shutdown drains API traffic, with the manifests' preStop wait (below).`
   - Consequences, last bullet → `⚠️ The probes are configured in the Kubernetes manifests (not yet written). On SIGTERM the host stops Kestrel first, so a terminating pod refuses new connections rather than answering 503: a full drain needs a preStop wait of a few seconds on API pods, terminationGracePeriodSeconds of at least that wait plus the 30 s shutdown timeout plus a margin, and Linkerd's wait-before-exit checked. Probes use HTTP/1.1 (not an h2c-only port), worker pods need a containerPort for /healthz, and Kestrel binds only after hosted services start, so use a startupProbe.`
2. **HLD §9.3** key signals: `SLO latency and error rate per route (QA-01 … QA-05)` → `SLO latency and error rate per route (QA-01 … QA-05; QA-03 is read from order's checkout_start_seconds, which leaves out the payment call, HLD-ADR-021)`.
3. **LR-05** (`01-launch.md`): `Hold p95 within the checkout budget (checkout start p95 < 300 ms, payment call excluded, HLD-ADR-021).` **IR-08** (`02-inventory.md`): `Reserve p95 fits the checkout budget (checkout start p95 < 300 ms, payment call excluded, HLD-ADR-021).` Both LLD headers: Status `Approved (2026-11-14)`, Last updated `2026-11-14`.
4. **07-api-gateway §10** alerts: append `; clock offset above 10 s on a gateway or database node (the 30 s future-iat check compares identity's database time with the gateway node's clock)`. Header dates 2026-11-14.

## Left to the builder

File layout, helper names, test names and structure; how the relay rewrites `occurredAt` (as long as only that field changes); the exact Identity call shapes.

## Success criteria

- One PR to `main` from `fix/audit-followups`; full suite green (`dotnet test --no-build --max-parallel-test-modules 1`), build with 0 warnings.
- Every item in B–F done or, if impossible without breaking a constraint, reported with the reason.
- All worktrees removed after the PR is pushed; branches stay on GitHub.

## Claude's calls (for the owner to check)

- Merge order in §A; ADR-020 edited in place because it is not on `main`.
- §B needs no ADR: it applies the existing database-clock rule (00-reliability §10) to one more field.
- §D3: the pass is still read twice in Join (correctness).
- 07-api-gateway alert wording and the 10 s threshold.
- HLD risk number R-11 (next free number; R-09 is the table's last row).
