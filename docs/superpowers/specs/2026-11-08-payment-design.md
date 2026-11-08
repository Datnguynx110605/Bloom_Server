# Payment service: design

| | |
|---|---|
| **Status** | Approved (2026-11-08) |
| **Date** | 2026-11-08 |
| **Branch / PR base** | `feat/payment` → `feat/order` |
| **Implements** | [04-payment](../../../doc/low-level-design/04-payment.md) (all sections), CHK-R10 … R17 (payment side), RET-R11 … R14 (payment side), BI-05, QA-09, HLD §5, §8.2, HLD-ADR-008, HLD-ADR-014 (API/worker split), HLD-ADR-018, LLD-ADR-001, LLD-ADR-007 |
| **Builds on** | `Bloom.Shared` ([shared-library spec](2026-10-31-shared-library-design.md)), gateway routes already in place ([api-gateway spec](2026-11-01-api-gateway-design.md): `/api/admin/payments/**`, `/api/admin/dlq/payment/**`, `POST /webhooks/stripe`), the existing `Bloom.Payment.Contracts` (`payment_internal.proto` with `CreatePayment`, and every message record of 04-payment §5.2) |

## 1. Intent

`payment` takes money for orders and gives it back through Stripe. It never charges twice (BI-05), and card data never touches Bloom (QA-09). It turns Stripe's callbacks into Bloom events and keeps its records in step with Stripe (04-payment §1).

04-payment is the approved design, and this spec doesn't restate it. It records only the owner decisions, and the builder calls the LLD leaves open. Where this spec is silent, 04-payment decides.

## 2. Goals

1. Payment **API** deployment: `CreatePayment` (internal, for order), `GetPayment` (Support, Admin), the plain-HTTP `POST /webhooks/stripe`, and the shared DLQ admin service (`MapBloomDeadLetterAdmin`, LLD-ADR-009).
2. Payment **worker** deployment:
   - the outbox relay
   - the consumers of `payment.refund-payment.v1` and `payment.cancel-payment.v1`
   - the provider executor (refunds and cancellations)
   - the 5-minute and nightly reconcilers
   - the shared retention purger, plus the `provider_events` 90-day purge (§7)
3. Every published message of 04-payment §5.2 goes out through the outbox, using the existing records in `Bloom.Payment.Contracts`.
4. Automated tests for P-01 … P-11 and every edge case of 04-payment §8.

**Success criteria:** `dotnet build` passes with warnings as errors. `dotnet test` passes locally (Colima running) and in the existing CI workflow. Each test case P-01 … P-11 has at least one test named after its ID.

## 3. Non-goals and anti-goals

**Non-goals:**
- **Infrastructure:** a Dockerfile, Compose entries, Kubernetes manifests, Linkerd rules or alert rules (alerts are metrics + error logs here).
- **Cross-service tests:** end-to-end tests across services. Order and fulfillment are stood in for by tests that publish their commands to the broker.
- **Other projects:** any change to `Bloom.Shared`, order, fulfillment, identity or the gateway (every payment route already exists, LLD 07 §5.1). Any change to the message records in `Bloom.Payment.Contracts`.
- **Account deletion:** no `identity.account-deleted.v1` consumer. 04-payment lists none, and payment stores no personal data (PR-05).
- **`doc/`:** any change under it (the proposed edits are in §9).

**Anti-goals** (a failure even if it technically works):
- Any method, message, field or error reason that neither 04-payment nor this spec lists (AGENTS.md §1).
- Two Stripe Checkout Sessions for one order, or two successful charges (PR-01).
- A Stripe call that moves money without its idempotency key (PR-02).
- A webhook processed without a valid signature, or applied twice for one Stripe event ID (PR-03).
- `refunded_cents > amount_cents`, even under concurrent refund commands (PR-04).
- Card data, customer email or address sent to Stripe or stored (PR-05).
- `payment.refund-completed.v1` or `payment.refund-failed.v1` published twice for one refund, or both for the same refund.
- A test that reaches the internet.

## 4. Owner decisions

| Topic | Decision |
|---|---|
| Scope | All of 04-payment, and nothing more except the items in this table. Code and tests only. |
| Stripe client | A hand-written client: `HttpClient`, `application/x-www-form-urlencoded` requests (Stripe's nested `a[b][c]` keys), `System.Text.Json` responses. It sits behind one `PaymentProvider` interface (HLD-ADR-018), with the API key and base URL from config. The webhook signature check is hand-written too (§7). No new NuGet packages. Tests use an in-process fake Stripe, and no test reaches the internet. |
| Refund completion | The executor applies the status that `refunds.create` returns, in the same transaction that stores `stripe_refund_id`. **`succeeded`:** refund `succeeded`, publish `payment.refund-completed.v1`. **`pending` / `requires_action`:** `submitted`, completed later by webhook or reconciliation, as 04-payment §6.3 says. **`failed` / `canceled`:** `failed`, the amount is released, publish `payment.refund-failed.v1`, error log. A later webhook for the same refund is a no-op (status guard). Doc edit §9.1. |
| Session expiry and resume | Stripe rejects a reused idempotency key when the parameters differ, so the create request is **frozen at the first attempt**. Tx 1 stores `session_expires_at = Tx 1 time + 47 min` (the 15-min checkout, plus Stripe's 30-min minimum, plus a 2-min margin). Every attempt, first or resumed, sends that same `expires_at` and an identical body (§7). Tx 2 overwrites it with the value Stripe returns. As a result, a session may stay payable for up to 47 min, not 30, if the cancel command never reaches Stripe. Any payment made then is still refunded as a late payment. Doc edit §9.8. |
| Testing across services | Tests publish `payment.refund-payment.v1` and `payment.cancel-payment.v1` to the broker and read payment's events off it. They call `CreatePayment` over gRPC, `GetPayment` over transcoded HTTP, and post signed events to `/webhooks/stripe`. |
| Branch | `feat/payment` off `feat/order`, with the PR into `feat/order`. |

## 5. Layout and dependencies (builder's call)

```
src/Payment/
  Bloom.Payment/            # one ASP.NET Core app; runs as payment-api or payment-worker by config
  Bloom.Payment.Contracts/  # existing: payment_internal.proto also generates server stubs; no record changes
  Bloom.Payment.Tests/      # xUnit v3 + Testcontainers (PostgreSQL, RabbitMQ) + in-process fake Stripe
```

- One app with a mode setting (`api` / `worker`), like identity, catalog, inventory, fulfillment and order (HLD-ADR-014).
- `AddBloom<PaymentDbContext>` provides the outbox, inbox, consumer host, retention purger, error mapping and metrics. `AddBloomTelemetry` provides OTel.
- EF Core migrations: the four tables of 04-payment §4 exactly, plus `AddBloomReliabilityTables`. Indexes: `payments(status, created_at)` for the reconciler, and `refunds(status, next_attempt_at)` for the executor.
- Staff-facing `GetPayment` lives in the service's own proto (package `bloom.payment.v1`). `CreatePayment` uses the existing `payment_internal.proto`.
- No new NuGet packages. New projects are added to `Bloom.slnx`.

## 6. Routes (builder's call)

| Surface | Methods → path |
|---|---|
| `/api/admin/payments/**` (Support, Admin) | `GetPayment` `GET /api/admin/payments/{order_id}`: gRPC transcoded to JSON |
| `/api/admin/dlq/payment/**` | Shared DLQ admin |
| `POST /webhooks/stripe` | Plain HTTP endpoint, raw body (not transcoded) |
| internal, not routed | `CreatePayment` (plain gRPC, existing `payment_internal.proto`) |

- **Roles** come from the trusted context (LLD 07 §4.3) and are checked again in payment. A refusal returns `PERMISSION_DENIED` / `ROLE_NOT_ALLOWED`.
- **`GetPayment`:** an unknown order returns `NOT_FOUND`. The response is the payment row (without `locked_until`), its refunds (with `lines`) and its disputes.
- **`CreatePayment` validation:** `amount_cents` > 0, at least one display line, every line amount ≥ 0, the line amounts sum to `amount_cents` (04-payment §9), and valid UUIDs. Anything else returns `VALIDATION_FAILED` (`INVALID_ARGUMENT`, with the field name in the `ErrorInfo` metadata). Doc edit §9.5.

## 7. Implementation notes (builder's calls unless marked)

### Stripe client and webhooks

- **Stripe client:**
  - Bearer API key, with a pinned `Stripe-Version` header.
  - `Idempotency-Key` on `checkout.sessions.create` (`bloom:payment.create:<order_id>`) and `refunds.create` (`bloom:refund:<refundId>`), exactly as 04-payment says.
  - Error classes: HTTP 5xx, 429, timeouts and network errors are **transient**. Any other 4xx is **permanent**, with Stripe's `error.code` (or `error.type`) kept as the reason.
  - Request timeout: 4 s for `CreatePayment`, so the answer fits order's 5 s deadline. 10 s elsewhere.
- **Session create** (04-payment §6.1 step 3):
  - **Line items:** one `price_data` line item per display line (`currency = usd`, `unit_amount`, `quantity = 1`, `product_data.name`).
  - **Metadata:** `{order_id, payment_id}` on the session, and the same on `payment_intent_data.metadata`, so `payment_intent.payment_failed` and disputes can find the payment.
  - **URLs:** `success_url` and `cancel_url` = `<StorefrontBaseUrl>/orders/<order_id>` (config).
  - **Expiry:** `expires_at` = the stored `session_expires_at` (§4).
  - **Identical body:** the body is built only from the request's `display_lines`, the row (`order_id`, `payment_id`, `order_number`, `session_expires_at`) and config, with fields in a fixed order, so a resume sends exactly the same request. The 24-hour lifetime of Stripe's key doesn't matter, because resumes happen only within order's 15-min checkout.
  - Nothing that identifies the customer is sent. Doc edit §9.2.
- **`CreatePayment` on other statuses** (04-payment §6.1 step 1 names only `open`, `succeeded` and `creating`): `expired` and `canceled` are treated like `open`. The same amount returns the row as it is, and a different amount returns `PAYMENT_EXISTS_DIFFERENT_AMOUNT`. No new session is created (PR-01), and order judges by its own status. An `open` row past `session_expires_at` is also returned as it is. Doc edit §9.9.
- **Tx 2 vs an early webhook** (04-payment §8): Tx 2 always stores the session ID, URL and expiry. It sets `status = open` only from `creating`, so a payment that the `completed` webhook already moved to `succeeded` stays `succeeded`, and its URL is still stored.
- **Refund create:** `payment_intent`, `amount`, `metadata = {refund_id, order_id}`.
- **Webhook signature:** parse `Stripe-Signature` (`t=…`, one or more `v1=…`). HMAC-SHA256 of `"{t}.{raw body}"` with the endpoint secret, compared in constant time against every `v1`. `|now − t| ≤ 5 min`. A missing or bad header returns `400`, and nothing is stored. Body ≤ 256 KB (04-payment §9).
- **Finding the record for an event** (any miss → stored only, `200`):
  - **Session events:** by `metadata.payment_id`.
  - **`payment_intent.payment_failed`:** by `metadata.payment_id` on the intent. `failureCode` = `last_payment_error.decline_code` ?? `last_payment_error.code` ?? `unknown`.
  - **Refund events:** by `stripe_refund_id`, or else by `metadata.refund_id`. A matched refund also stores `stripe_refund_id` if it's missing.
  - **Disputes:** by `payment_intent` = `stripe_payment_intent_id`.
- **Refund status guards:** completion and failure apply only from `pending` / `submitted`. A Stripe status that contradicts an already final refund (for example `succeeded` → `failed`) leaves the row unchanged and publishes nothing. It produces an error log (the alert). Doc edit §9.3.

### Provider executor

- **Executor loop:** every 5 s, `FOR UPDATE SKIP LOCKED`, batch 50. Both kinds of work run in the same loop.
- **Refund retries:** a transient error adds 1 to `attempts`, and `next_attempt_at` follows 1 s / 5 s / 30 s / 2 min. On the 5th failure the refund goes to `failed`, the amount is released, `refund-failed` is published, and an error is logged. A permanent error goes to `failed` straight away, with the reason (04-payment §6.3).
- **Cancellations:** the executor picks `open` payments with `cancel_requested` and `session_expires_at > now` (so there's no attempts column), and calls `checkout.sessions.expire`.
  - **Success:** `canceled`, guarded on `open`.
  - **Transient error:** retried on the next tick.
  - **Permanent error** (the session is no longer open): the executor retrieves the session and applies its state the way the reconciler does. A completed session waits for the `completed` transition. An expired session goes to `expired`.
  - **Past the session's expiry:** the 5-minute reconciler takes over.
  - A cancel command for a payment that is not `open` (including `creating`) is a no-op, as written.
  - Doc edit §9.4.

### Consumers, reconcilers and metrics

- **Guarded updates** follow 04-payment §6–7: single-statement conditional `UPDATE` on `status` (and the `refunded_cents` guard), an affected-row check, and `version + 1`, `updated_at = now()` on every write (00-reliability §7.5).
- **Refund command, missing payment:** no payment for `orderId` is the same permanent failure as a payment that isn't `succeeded` (DLQ + error log, 04-payment §6.3 step 2).
- **Consumers** use the shared inbox. Queue names and bindings follow 00-reliability §6, with permanent failures sent to the DLQ.
- **Reconcilers** use the same transition code as the webhook (04-payment §6.5).
  - **Every 5 min:** `open` payments created more than 15 min ago, and `submitted` refunds last updated more than 10 min ago. Each fix adds 1 to `reconcile_fixes_total{kind}`.
  - **Nightly at 02:00 UTC:** covers the previous UTC day.
    - The Stripe side is listed by `created` for PaymentIntents with `status = succeeded` and refunds, across every page.
    - Bloom's `succeeded` payments and `succeeded` refunds of that day that are missing from the list are fetched by ID before they count as a mismatch, so a day boundary isn't reported as drift.
    - Mismatches (Stripe-only, Bloom-only, different amount) are logged as an error with the list and set `reconcile_mismatches`.
    - Nothing is corrected.
- **`provider_events` retention:** the shared purger's rules are fixed (00-reliability §13), so payment runs its own daily batch delete (1,000 rows) of rows whose `received_at` is older than 90 days. Doc edit §9.6.
- **Metrics** of 04-payment §10 go on meter `Bloom.Payment`. `payments_succeeded_total{late}` is `true` when the success is applied to a payment that is `expired` or `canceled`, or that has `cancel_requested`. `webhook_lag_seconds` measures from the event's `created` to the commit. `disputes_open` counts disputes whose `status` isn't one of Stripe's closed statuses (`won`, `lost`, `warning_closed`).
- **Alerts** (04-payment §10) are error logs plus metrics: `refund-failed`, a mismatch, a dispute, bad signatures, and processing errors.

## 8. Testing

- xUnit v3 + Testcontainers: real PostgreSQL and RabbitMQ (one container of each per run). The app runs in-process (`WebApplicationFactory`) in both modes.
- **Fake Stripe:** an in-process HTTP server for sessions create / expire / retrieve, refunds create / retrieve / list, and PaymentIntents list.
  - It honours idempotency keys the way Stripe does. The same key with the same parameters returns the same object. The same key with different parameters returns `400 idempotency_error`, so P-02 fails if the resumed body changes.
  - It enforces Stripe's minimum session expiry (`expires_at` at least 30 min after the call).
  - It records every request.
  - It can be scripted per test with errors (transient, permanent), delays, timeouts and returned statuses.
  - Tests build signed webhook events with the test endpoint secret.
- **Concurrency tests** run real parallel work against Postgres: concurrent `CreatePayment` for one order, and concurrent refund commands that together exceed the amount.
- `FakeTimeProvider` drives the lock window, the reconciler ages, the 02:00 schedule and the signature tolerance.
- Tests need Colima running locally. All containers are removed after the run.

## 9. Proposed doc edits (not applied)

No edit under `doc/` is made in this project (AGENTS.md §9). These are for the owner to approve separately:

1. **04-payment §6.3, executor** (owner decision): "Success → `submitted`" becomes "Success → apply the returned status: `succeeded` → refund `succeeded`, publish `refund-completed`; `pending` → `submitted`; `failed` / `canceled` → `failed`, release the amount, publish `refund-failed`, alert." §7: add "Refund completed at create and by webhook → status guard; second is a no-op."
2. **04-payment §6.1 step 3:** add `payment_intent_data.metadata = {order_id, payment_id}`. §6.3: add refund `metadata = {refund_id, order_id}`. §6.2: name how each event finds its record (§7 of this spec).
3. **04-payment §6.2, refund events:** transitions apply only from `pending` / `submitted`; a contradicting status on a final refund → error log, no event.
4. **04-payment §6.4:** the executor retries `expire` every tick while the payment is `open` and the session hasn't expired; a "not open" answer → retrieve the session and apply it as the reconciler does.
5. **04-payment §5.3:** add the shared reasons `NOT_FOUND` (`GetPayment`), `VALIDATION_FAILED` (`CreatePayment` input, including `display_lines` not summing to `amount_cents`) and `ROLE_NOT_ALLOWED`.
6. **04-payment §4 `provider_events`:** "Kept 90 days" → "Kept 90 days, purged daily by payment (the shared purger's §13 rules don't cover it)."
7. **04-payment §10:** define `late` on `payments_succeeded_total` (§7 of this spec).
8. **04-payment §6.1** (owner decision): step 2, Tx 1 also stores `session_expires_at = now + 47 min`. Step 3: `expires_at` = the stored value, and the request is identical on every attempt (Stripe rejects a reused key with different parameters). §2 constraints and §8 "Cancel command can't reach Stripe": "30-min expiry" → "47-min expiry". §4 `session_expires_at`: "planned at Tx 1, Stripe's value at Tx 2".
9. **04-payment §6.1 step 1:** `expired` / `canceled` behave like `open` (same amount → return the row; different → `PAYMENT_EXISTS_DIFFERENT_AMOUNT`). Step 4: Tx 2 sets `open` only from `creating`.

## 10. Left to the builder

Internal names, class and proto layout, request and response message shapes beyond the fields 04-payment §5.1 and this spec name, config key names, the pinned Stripe API version, the fake Stripe's internals, and test isolation. Anything visible outside payment, or anything affecting the design, that 04-payment and this spec don't cover goes back to the owner (AGENTS.md §7).

## 11. Open items (owner)

None.
