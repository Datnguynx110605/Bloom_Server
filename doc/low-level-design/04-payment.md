# LLD — payment service

[Docs](../README.md) › [Low-level design](README.md)

| | |
|---|---|
| **Status** | Approved (2026-11-12) |
| **Last updated** | 2026-10-31 |
| **Product rules** | [05-checkout-payment](../product-design/05-checkout-payment.md) CHK-R10 … R17, RET-R11 … R14, BI-05 |
| **HLD** | [high-level-design.md §5, §8.2](../high-level-design/high-level-design.md), HLD-ADR-008, HLD-ADR-018 |
| **Decision records** | [LLD-ADR-001](adr/LLD-ADR-001-two-phase-idempotency.md), [LLD-ADR-007](adr/LLD-ADR-007-stripe-checkout-hosted.md) |
| **Depends on** | [00-reliability.md](00-reliability.md), [03-order.md](03-order.md) |

## 1. Problem and scope

`payment` takes money for orders and gives it back, through Stripe, without ever charging twice (BI-05) and without card data touching Bloom (QA-09). It turns Stripe's asynchronous callbacks into Bloom events and keeps its records in step with Stripe.

**In scope:** Stripe Checkout Sessions for orders, webhook intake, refunds, session cancellation, reconciliation, dispute recording.

**Out of scope:** deciding when or how much to refund (`order`, `fulfillment`), order status, tax, saved cards (not in product scope), dispute handling beyond recording.

## 2. Requirements and constraints

| ID | Requirement | Source |
|---|---|---|
| PR-01 | At most one payment per order, and at most one successful charge. | BI-05, HLD-ADR-008 layer 2 |
| PR-02 | Every Stripe call that moves money carries a Stripe idempotency key. | HLD-ADR-008 layer 3 |
| PR-03 | Every webhook is signature-verified and processed at most once per Stripe event ID. | HLD-ADR-008 layer 4 |
| PR-04 | Total refunds on a payment never exceed its amount. | BI-05 |
| PR-05 | Card data never reaches Bloom. | QA-09, CHK-R10 |
| PR-06 | A missed webhook is detected within 15 min; drift against Stripe is detected daily. | HLD-ADR-008 |

**Constraints:** Stripe test mode; Checkout Session `expires_at` minimum is 30 minutes after creation (Stripe limit), shorter than Bloom's 15-minute checkout (§8). The session is created with a 47-minute expiry: the 15-minute checkout, plus Stripe's 30-minute minimum, plus a 2-minute margin (§6.1).

## 3. Component structure

| Component | Deployment | Responsibility |
|---|---|---|
| Payment API | API | `CreatePayment` (order), `GetPayment` (Support, Admin) |
| Webhook endpoint | API | Plain HTTP `POST /webhooks/stripe` (raw body needed for the signature; not JSON-transcoded) |
| Command consumers | Worker | `payment.refund-payment.v1`, `payment.cancel-payment.v1`: record intent only |
| Provider executor | Worker | Calls Stripe for pending refunds and cancellations, with retries |
| Reconciler | Worker | Every 5 min (stuck records) and nightly (comparison) |
| Reliability plumbing | Both | Outbox, inbox, purger ([00-reliability.md](00-reliability.md)) |

All Stripe calls go through one `PaymentProvider` interface (HLD-ADR-018).

## 4. Data model (payment database)

**`payments`**

| Column | Type | Constraints / notes |
|---|---|---|
| `id` | `uuid` | PK (UUIDv7) |
| `order_id` | `uuid` | **unique** (PR-01) |
| `order_number` | `text` | for display on Stripe's page |
| `account_id` | `uuid` | |
| `amount_cents` | `bigint` | > 0 |
| `currency` | `text` | `usd` |
| `status` | `text` | `creating` \| `open` \| `succeeded` \| `expired` \| `canceled` |
| `locked_until` | `timestamptz` | `creating` only: claim + 60 s |
| `stripe_session_id` | `text` | unique, null until created |
| `stripe_payment_intent_id` | `text` | unique, null until paid |
| `checkout_url` | `text` | null until created |
| `session_expires_at` | `timestamptz` | planned at Tx 1 (now + 47 min), Stripe's value at Tx 2 |
| `cancel_requested` | `bool` | default false |
| `refunded_cents` | `bigint` | default 0, `CHECK (refunded_cents <= amount_cents)` (PR-04) |
| `succeeded_at` | `timestamptz` | null |
| `version` | `bigint` | default 0 |
| `created_at`, `updated_at` | `timestamptz` | |

**`refunds`**

| Column | Type | Constraints / notes |
|---|---|---|
| `id` | `uuid` | PK = `refundId` from the command (idempotent) |
| `payment_id`, `order_id` | `uuid` | `payment_id` FK → `payments` (Restrict) |
| `amount_cents` | `bigint` | > 0 |
| `reason` | `text` | from the command |
| `lines` | `jsonb` | optional `[{lineNo, amountCents}]`, echoed in events |
| `status` | `text` | `pending` \| `submitted` \| `succeeded` \| `failed` |
| `stripe_refund_id` | `text` | unique, null until submitted |
| `failure_reason` | `text` | null |
| `attempts` | `int` | default 0 |
| `next_attempt_at` | `timestamptz` | executor scheduling |
| `created_at`, `updated_at` | `timestamptz` | |

**`provider_events`**: `event_id text PK` (Stripe event ID), `type`, `payload jsonb`, `received_at`. Kept 90 days, purged daily by payment (the shared purger's §13 rules don't cover it).
**`disputes`**: `id`, `payment_id` (FK → `payments`, Restrict), `stripe_dispute_id` (unique), `status`, `amount_cents`, `reason`, `opened_at`, `updated_at` (the Stripe event's `created`, D25; §6.2).

**`job_runs`**: `name text PK`, `last_run_date date`. The migration seeds the row `nightly_compare` (§6.5).

## 5. Interfaces

### 5.1 gRPC and HTTP

| Method | Caller | Request → Response |
|---|---|---|
| `CreatePayment` | order | `order_id`, `order_number`, `account_id`, `amount_cents`, `display_lines[{name, amount_cents}]` → `payment_id`, `checkout_url`, `session_expires_at` |
| `GetPayment` | Support, Admin | `order_id` → payment, refunds, disputes |
| `POST /webhooks/stripe` | Stripe (via gateway public route) | Raw event → `200` once stored and processed; `400` bad signature, or a validly signed body that isn't a Stripe event (§6.2); `413` body above 256 KB; `500` processing error (Stripe retries) |

`display_lines` sum to `amount_cents`: one entry per order line (name includes "× qty"; amount = the line's `paid_cents`) plus shipping. Stripe automatic tax is off; amounts are Bloom's. A retry for the same `order_id` must carry identical `display_lines` (order derives them from the stored order).

### 5.2 Messages

| Message | Direction | Payload / effect |
|---|---|---|
| `payment.succeeded.v1` | Published → order | `orderId`, `paymentId`, `amountCents`, `succeededAt` |
| `payment.failed.v1` | Published → order | `orderId`, `paymentId`, `failureCode` (each failed attempt) |
| `payment.refund-completed.v1` | Published → order, fulfillment, notification | `orderId`, `refundId`, `amountCents`, `lines?` |
| `payment.refund-failed.v1` | Published → order, fulfillment | `orderId`, `refundId`, `failureReason`: Stripe's refund `failure_reason` when Stripe reports `failed` or `canceled` (the status itself when Stripe gives none); Stripe's error reason for a permanent `refunds.create` error; `provider_unavailable` after the 5th transient failure |
| `payment.refund-payment.v1` | Consumed (`payment.refund-payment`) | Command from order / fulfillment |
| `payment.cancel-payment.v1` | Consumed (`payment.cancel-payment`) | Command from order |

### 5.3 Error reasons

| Reason | Status | When |
|---|---|---|
| `PAYMENT_EXISTS_DIFFERENT_AMOUNT` | `INVALID_ARGUMENT` | `CreatePayment` retried for an order with another amount |
| `PAYMENT_CREATE_IN_PROGRESS` | `ABORTED` | Another call is creating this order's session (< 60 s) |
| `PROVIDER_UNAVAILABLE` | `UNAVAILABLE` | Stripe error or timeout (caller retries) |
| `NOT_FOUND` | `NOT_FOUND` | `GetPayment` for an unknown order |
| `VALIDATION_FAILED` | `INVALID_ARGUMENT` | `CreatePayment` input, including `display_lines` not summing to `amount_cents`; the field is named in `ErrorInfo` metadata |
| `ROLE_NOT_ALLOWED` | `PERMISSION_DENIED` | Caller's role may not call the method (00-reliability §8) |

## 6. Behaviour

### 6.1 CreatePayment (two-phase, [LLD-ADR-001](adr/LLD-ADR-001-two-phase-idempotency.md))

1. Look up `payments` by `order_id`:
   - A different amount than the existing row's → `PAYMENT_EXISTS_DIFFERENT_AMOUNT`, for every existing row, `creating` included. The amount is compared first.
   - `open` / `succeeded`, same amount → return it (idempotent).
   - `expired` / `canceled` behave like `open`: the same amount returns the row as it is. No new session is created (PR-01), and order judges by its own status. An `open` row past `session_expires_at` is also returned as it is.
   - `creating`, same amount, `locked_until > now()` → `PAYMENT_CREATE_IN_PROGRESS`; stale → take over (guarded update of `locked_until`) and continue at step 3.
2. **Tx 1:** insert `creating`, `locked_until = now() + 60 s`, `session_expires_at = now() + 47 min`. Commit.
3. Stripe `checkout.sessions.create`: mode `payment`, line items from `display_lines`, `client_reference_id = order_id`, metadata `{order_id, payment_id}` (also on `payment_intent_data.metadata`), `success_url` / `cancel_url` to the order page, `expires_at` = the stored `session_expires_at`. **Idempotency key `bloom:payment.create:<order_id>`.** The request is identical on every attempt, first or resumed (Stripe rejects a reused key with different parameters).
4. **Tx 2:** store session ID, URL, expiry; `status = open` only from `creating` (a payment that the `completed` webhook already moved to `succeeded` stays `succeeded`, and its URL is still stored). Commit. Return.

Stripe error or timeout → `PROVIDER_UNAVAILABLE`; the claimer sets `locked_until = now`, guarded on its own lock value, and the row stays `creating`, so the next call resumes at once with the same Stripe key, which returns the same session. A crash still waits the 60 s (P-02).

### 6.2 Webhook intake

One transaction per request:

1. Verify `Stripe-Signature` with the endpoint secret, 5-minute tolerance. Failure → `400`, nothing stored. A body above 256 KB → `413`. A validly signed body that isn't a JSON object with a string `id`, a string `type` and an integer `created` → `400`, nothing stored, an error log, and `webhooks_total{type="unknown", result="error"}`.
2. `INSERT INTO provider_events … ON CONFLICT DO NOTHING`. 0 rows → duplicate → `200`.
3. Apply the event (table below), writing outbox messages.
4. Commit → `200`. Any error → rollback → `500` (Stripe retries with backoff for up to 3 days).

Finding the record for an event (any miss → stored only, `200`):
- **Session events:** by `metadata.payment_id`.
- **`payment_intent.payment_failed`:** by `metadata.payment_id` on the intent. `failureCode` = `last_payment_error.decline_code` ?? `last_payment_error.code` ?? `unknown`.
- **Refund events:** by `stripe_refund_id`, or else by `metadata.refund_id`. A matched refund also stores `stripe_refund_id` if it's missing.
- **Disputes:** by `payment_intent` = `stripe_payment_intent_id`.

| Stripe event | Effect |
|---|---|
| `checkout.session.completed` with `payment_status = paid` | Payment → `succeeded` from any non-succeeded status (money was taken, so record it even if `expired` / `canceled`); store payment intent ID; publish `payment.succeeded.v1`. `order` decides whether it's a late payment. |
| `checkout.session.expired` | `open` → `expired`. No event (order already handles its own deadline). |
| `payment_intent.payment_failed` | Publish `payment.failed.v1`. Status unchanged (the customer can retry on the same page). |
| `refund.updated` / `charge.refund.updated` | `succeeded` → refund `succeeded`, publish `refund-completed`. `failed` / `canceled` → refund `failed`, release its amount from `refunded_cents`, publish `refund-failed`, alert. Transitions apply only from `pending` / `submitted`: a Stripe status that contradicts an already final refund (for example `succeeded` → `failed`) leaves the row unchanged and publishes nothing, with an error log (the alert). |
| `charge.dispute.created` / `.updated` / `.closed` | Upsert `disputes`, with `updated_at` = the Stripe event's `created`; the upsert applies only `WHERE disputes.updated_at <= EXCLUDED.updated_at`, so the newest event wins. Alert Support. No other effect. |
| Any other type | Stored only. |

### 6.3 Refund command

**Consumer** (inbox transaction):
1. `INSERT INTO refunds (id = refundId, …, status = pending, next_attempt_at = now()) ON CONFLICT DO NOTHING`. 0 rows → duplicate, done.
2. Payment must be `succeeded`, otherwise permanent failure (DLQ + alert): order never asks to refund an unpaid payment.
3. Guarded `UPDATE payments SET refunded_cents = refunded_cents + @amount WHERE id = @p AND refunded_cents + @amount <= amount_cents`. 0 rows → permanent failure (DLQ + alert): a caller tried to over-refund (PR-04).

**Executor** (every 5 s, `SKIP LOCKED`, batch 50): for each `pending` refund with `next_attempt_at <= now()`, call Stripe `refunds.create(payment_intent, amount, metadata = {refund_id, order_id})` with **idempotency key `bloom:refund:<refundId>`**. Success → apply the returned status, in the same transaction that stores `stripe_refund_id`: `succeeded` → refund `succeeded`, publish `refund-completed`; `pending` / `requires_action` → `submitted`; `failed` / `canceled` → `failed`, release the amount, publish `refund-failed`, alert; an **unknown** status → `submitted`, error log. Once `refunds.create` returns a refund object, create is never called again for that refund: the webhook and the reconciler settle it. Transient error (including Stripe `409`, which Stripe answers when the same idempotency key is still in flight) → `attempts + 1`, `next_attempt_at` per the 1 s / 5 s / 30 s / 2 min schedule; after 5 attempts → `failed`, release the amount, publish `refund-failed`, alert. Permanent Stripe error (any other 4xx, for example charge already fully refunded outside Bloom) → `failed` immediately with the reason.

Completion arrives by webhook (§6.2) or reconciliation.

### 6.4 Cancel-payment command

Consumer sets `cancel_requested = true` on an `open` payment (no-op otherwise). The executor retries `checkout.sessions.expire` (idempotent on Stripe's side) every tick while the payment is `open` and the session hasn't expired: success → `canceled`; a "not open" answer (the session is complete or expired) → retrieve the session and apply it now with the reconciler's transition (§6.5). A completed session → `succeeded`, and `order` treats that as a late payment. A later webhook is a no-op.

### 6.5 Reconciliation

| Job | Rule |
|---|---|
| Every 5 min | `open` payments older than 15 min: retrieve the session; apply `completed` / `expired` as if the webhook had arrived. `submitted` refunds older than 10 min: retrieve the refund; apply its status. (PR-06) |
| Nightly 02:00 UTC | A replica runs only if `UPDATE job_runs SET last_run_date = <UTC today> WHERE name = 'nightly_compare' AND last_run_date < <UTC today>` hits 1 row, so the comparison runs once per night. Then: list yesterday's succeeded PaymentIntents and refunds from Stripe; compare with Bloom's records by ID and amount. Any mismatch → alert with the list. No automatic correction. |

Reconciliation applies the same transitions as webhooks, so a later duplicate webhook is a no-op.

## 7. Concurrency and consistency

| Risk | Protection |
|---|---|
| Two sessions for one order | Unique `order_id` + Stripe idempotency key on create |
| Double charge | One session per order; layer 1 (order's key) + layer 2 (unique) + layer 3 (Stripe keys) + layer 4 (event dedup) |
| Duplicate refund | `refunds.id = refundId` + Stripe key `bloom:refund:<refundId>` |
| Over-refund | Guarded `refunded_cents` update + `CHECK` |
| Webhook and reconciler apply the same change | Status-guarded transitions; second is a no-op |
| Out-of-order webhooks (refund before payment success) | Refunds are only created after success (§6.3 step 2) |
| Refund completed at create and by webhook | Status guard; the second is a no-op |

## 8. Edge cases

| Case | Expected behaviour |
|---|---|
| Customer opens the payment page after the 15-min deadline | Session already expired by the cancel command; Stripe shows it as expired. |
| Customer pays in the same second the cancel runs | `completed` webhook → `succeeded` → order refunds as a late payment. |
| Cancel command can't reach Stripe | Executor retries; until then the page stays payable, and any payment becomes a late-payment refund. Session dies at Stripe's 47-min expiry at the latest. |
| Card declined | Customer retries on Stripe's page; `payment.failed.v1` per attempt. |
| Webhook arrives before Tx 2 of `CreatePayment` | Event references `metadata.payment_id`; the row exists (`creating`), so the transition applies. |
| Refund made manually in the Stripe dashboard | Not in Bloom; caught by the nightly comparison. |
| Refund fails permanently | `refund-failed`, amount released, Support alerted; the order saga stays open and visible. |
| Dispute opened | Recorded, Support alerted; nothing else changes. |
| Dispute events arrive out of order (a late `created` after `closed`) | The newest event wins (§6.2): the status stays `won` / `lost`, and `disputes_open` stays right. |

## 9. Security and validation

- Webhook signatures are mandatory; the endpoint secret and API key are Sealed Secrets visible only to `payment`.
- `CreatePayment` is callable only by `order`; amounts are trusted from `order` (service identity) and must equal the sum of `display_lines`.
- No card data, no customer email or address is sent to Stripe or stored here (PR-05).
- The webhook route on the gateway accepts only `POST`, max body 256 KB, and is rate-limited per IP.

## 10. Observability

| Metric | Labels | Purpose |
|---|---|---|
| `payments_created_total` | `result` | Session creation |
| `payments_succeeded_total` | `late` = `true` \| `false`: `true` when the success is applied to a payment that is `expired` or `canceled`, or that has `cancel_requested` | Revenue and late payments |
| `refunds_total` | `status` | Refund outcomes |
| `webhooks_total` | `type`, `result` = `processed` \| `duplicate` \| `bad_signature` \| `error` | Intake health |
| `webhook_lag_seconds` | — | Stripe `created` → processed |
| `reconcile_fixes_total` | `kind` | Missed webhooks |
| `reconcile_mismatches` | — | Daily drift |
| `disputes_open` | — | Disputes |

**Alerts:** any `refund-failed`; any reconciliation mismatch; any dispute; `webhooks_total{result="bad_signature"}` spike; `webhooks_total{result="error"}` sustained.

## 11. Test cases

| # | Case | Expected |
|---|---|---|
| P-01 | `CreatePayment` twice for one order | Same session URL, one Stripe session |
| P-02 | Crash after Stripe create, before Tx 2; retry after 60 s | Same session returned (Stripe idempotency) |
| P-03 | Same Stripe event delivered twice | Processed once |
| P-04 | Bad signature | `400`, nothing stored |
| P-05 | `completed` after the payment was `canceled` | `succeeded`, `payment.succeeded.v1` published |
| P-06 | Refund command delivered twice | One refund, one Stripe call key |
| P-07 | Refunds totalling more than the payment | Second over-limit command → DLQ, nothing refunded |
| P-08 | Stripe refund fails | `failed`, amount released, `refund-failed`, alert |
| P-09 | Missed `completed` webhook | Reconciler marks `succeeded` within 15 min |
| P-10 | Cancel after the customer already paid | Stays `succeeded`; order refunds as late payment |
| P-11 | Nightly comparison with a dashboard-only refund | Mismatch alert |

## 12. Open questions

| ID | Question |
|---|---|
| — | None open. |
