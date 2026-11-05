# Fulfillment service: design

| | |
|---|---|
| **Status** | Approved (2026-11-05) |
| **Date** | 2026-11-05 |
| **Branch / PR base** | `feat/fulfillment` → `feat/inventory` |
| **Implements** | [05-fulfillment](../../../doc/low-level-design/05-fulfillment.md) (all sections), ORD-R08 … R17, RET-R01 … R15, WAR-R01 … R06, BI-05, BI-07, HLD §7.3, §8.4, HLD-ADR-005, HLD-ADR-014 (API/worker split), HLD-ADR-018 (provider behind an interface) |
| **Builds on** | `Bloom.Shared` ([shared-library spec](2026-10-31-shared-library-design.md)), gateway routes already in place ([api-gateway spec](2026-11-01-api-gateway-design.md)), inventory's internal gRPC ([inventory spec](2026-11-04-inventory-design.md)) |

## 1. Intent

`fulfillment` gets paid orders physically to customers and back: packing with serial scanning, Shippo labels and tracking, delivery failures, returns with inspection, the refund amounts for returns and failures, and warranty coverage per serial (05-fulfillment §1).

05-fulfillment is the approved design. This spec does not restate it. It records only the owner decisions and builder calls the LLD leaves open. Where this spec is silent, 05-fulfillment decides.

## 2. Goals

1. Fulfillment **API** deployment: every method of 05-fulfillment §5.1, the three read methods of §4 (owner decision), the Shippo webhook, and the shared DLQ admin service (`MapBloomDeadLetterAdmin`, LLD-ADR-009).
2. Fulfillment **worker** deployment: outbox relay, the four consumers of §5.3, the label worker (buy, void), the sweeps (possibly-lost hourly, return expiry hourly, daily label check), the retention purger (from `Bloom.Shared`).
3. A real **Shippo client** (HTTP, test mode) behind an interface; an in-process fake Shippo for tests.
4. Every message of 05-fulfillment §5.3 published or sent through the outbox.
5. Contracts other services need: fulfillment's internal `.proto` (`CancelPackTask`); payload records for every message fulfillment publishes or sends; `OrderReadyToFulfillV1` in `Bloom.Order.Contracts`; a new `Bloom.Payment.Contracts` with `RefundPaymentV1`, `RefundCompletedV1`, `RefundFailedV1`.
6. Automated tests for F-01 … F-15 and every edge case of 05-fulfillment §8.

**Success criteria:** `dotnet build` passes with warnings as errors. `dotnet test` passes locally (Colima running) and in the existing CI workflow. Each test case F-01 … F-15 has at least one test named after its ID.

## 3. Non-goals and anti-goals

**Non-goals:** the order and payment services (only the contracts in §5). Cross-service end-to-end tests: order and payment are faked by publishing their messages and calling fulfillment's gRPC directly; inventory is a test-double gRPC server. Calls to the real Shippo API in tests. Dockerfile, Compose entries, Kubernetes manifests, Linkerd rules, Sealed Secrets, alert rules (alerts are metrics + error logs here; rules live with deployment). Any change to `Bloom.Shared`, catalog, inventory or the gateway (all fulfillment routes already exist, LLD 07 §5.1). Any change under `doc/` (proposed edits in §9).

**Anti-goals** (failure even if it technically works):
- Any method, message, field or error reason that 05-fulfillment or this spec doesn't list (AGENTS.md §1).
- A package handed over for a cancelled task, or a handed-over task cancelled (FR-01).
- `refunded_cents > paid_cents` on any line ever, or a full return of a line not refunding it exactly (FR-02).
- A handover with a serialized unit unscanned (FR-03).
- A shipment status taken from a webhook body instead of a Shippo re-fetch (FR-04).
- Two paid labels for one shipment left un-voided (FR-05).
- A local `pack_task_serials` row written without inventory's `AssignSerial` answering `ok` first (§6.2).
- A refund command written without its saga row and its line-account updates in the same transaction (§6.4.1).

## 4. Owner decisions

| Topic | Decision |
|---|---|
| Scope | All of 05-fulfillment, nothing more except the items in this table. Code + tests only. |
| Shippo | Real HTTP client (`HttpClient` + `System.Text.Json`, Shippo test-mode key from config) behind an interface. Tests use an in-process fake Shippo; no test reaches the internet. **Lookup before buy** (§6.6): Shippo has no search by metadata, so the client lists transactions created since the shipment row was created and matches `metadata`. Every label carries `metadata = "bloom:<shipment_id>"`, which is also how the daily check recognises Bloom's labels. |
| `RefundPaymentV1` | `orderId`, `refundId`, `amountCents`, `reason`, `lines?` (`[{lineNo, amountCents}]`, optional: order's own refunds have none). Fulfillment always sends `lines` and `reason` = `return` (§6.4.1) or `delivery_failure` (§6.5). Doc edits §9.1. |
| Dismiss possibly-lost | `delivery_failures` gets `previous_status` (`handed_over` \| `in_transit`, set when the shipment is flagged) and `dismissed_at`. `DismissPossiblyLost` and the automatic dismiss on `DELIVERED` (§8) restore the shipment to `previous_status` (the `DELIVERED` case then applies `delivered`). The possibly-lost sweep measures 7 days from the latest of `last_tracking_at`, `handed_over_at`, `dismissed_at`. A shipment flagged again reuses its row: `status` → `possibly_lost`, `previous_status` set anew. Doc edit §9.2. |
| Shipment address | No address column on `shipments`. Every shipment uses `ship_to` of the order's `kind = order` pack task (by `order_id`). Outbound and ship-back: warehouse → `ship_to`. Return: `ship_to` → warehouse. Account deletion anonymizes `pack_tasks.ship_to` only. Doc edit §9.3. |
| `RecordReturnedToSender` key | **Requires** an `Idempotency-Key` and runs through the shared guard (00-reliability §7.4), like `DecideReturn` and `ConfirmLost`. Doc edit §9.4. |
| Label data | From config, no data-model change: warehouse address; one default parcel (length, width, height in inches, weight in ounces) for every label; a Shippo service-level token per option (`standard`, `express`). Return and ship-back labels use the `standard` token. |
| Error reasons beyond §5.4 | New `INVALID_STATE` (`FAILED_PRECONDITION`): a pack task, shipment or delivery failure is not in a status the action accepts (`INVALID_RETURN_STATE` stays for returns). Shared: `NOT_FOUND`, `VALIDATION_FAILED` (`INVALID_ARGUMENT`, field name in `ErrorInfo` metadata), `ROLE_NOT_ALLOWED`, `CONCURRENCY_CONFLICT` (stale `version`), `IDEMPOTENCY_KEY_*`. Inventory's reasons on scans and `CommitForReship` (e.g. `SERIAL_NOT_AVAILABLE`, `NOT_COMMITTED`) pass through unchanged, except `SOLD_OUT` → `RESHIP_NO_STOCK` (§5.4). Doc edit §9.5. |
| Staff read methods | Three new read-only methods, no new messages. `ListPackTasks` (Warehouse, Admin): filter `status?`, oldest first. `ListReturns` (Warehouse, Support, Admin): filter `status?`, oldest first. `ListDeliveryFailures` (Support, Admin): filter `status?`, oldest first. All paged (`page` from 1, `page_size` 1–100, default 50). Items: the row with its lines / units (tasks: lines and scanned serials; returns: units; failures: shipment, kind, status, `previous_status`). Doc edit §9.6. |
| Plan and device in one request | A `RequestReturn` that names a plan line together with its parent device line → `VALIDATION_FAILED`. Plans come back with their device through §6.4.1 step 3 (WAR-R05), decided per covered serial at `DecideReturn`. A plan on its own (WAR-R06) is a separate plan-only request, decided immediately. Doc edit §9.8. |
| Testing across services | Order and payment don't exist. Tests publish `order.ready-to-fulfill.v1`, `payment.refund-*.v1` and `identity.account-deleted.v1` to the broker and call `CancelPackTask` over gRPC. Inventory is a test-double gRPC server; tests assert the inventory commands fulfillment sends by reading them off the broker. |
| Branch | `feat/fulfillment` off `feat/inventory`, PR into `feat/inventory`. |

## 5. Layout and dependencies (builder's call)

```
src/Fulfillment/
  Bloom.Fulfillment/            # one ASP.NET Core app; runs as fulfillment-api or fulfillment-worker by config
  Bloom.Fulfillment.Contracts/  # existing: add fulfillment_internal.proto (CancelPackTask, client + server stubs)
                                # and payload records for every published message of §5.3
  Bloom.Fulfillment.Tests/      # xUnit v3 + Testcontainers (PostgreSQL, RabbitMQ)
src/Order/Bloom.Order.Contracts/     # existing: add OrderReadyToFulfillV1 (03-order §5.3 fields) + message type
src/Payment/Bloom.Payment.Contracts/ # new: RefundPaymentV1, RefundCompletedV1, RefundFailedV1 + message types
```

- One app with a mode setting (`api` / `worker`), like identity, catalog and inventory (HLD-ADR-014).
- `AddBloom<FulfillmentDbContext>` provides outbox, inbox, consumer host, retention purger, error mapping, metrics; `AddBloomTelemetry` for OTel.
- EF Core migrations: the tables of 05-fulfillment §4 with the changes of §4 above, plus `AddBloomReliabilityTables`.
- Inventory client from the existing `Bloom.Inventory.Contracts`. Staff-action payload is the existing `Bloom.Shared.DeadLetters.StaffActionRecorded`. `AccountDeletedV1` from `Bloom.Identity.Contracts`.
- No new NuGet packages.
- All new projects added to `Bloom.slnx`.

## 6. Routes (builder's call)

gRPC services transcoded to JSON under the existing gateway prefixes (LLD 07 §5.1).

| Surface | Methods → path |
|---|---|
| `/api/returns/**`, `/api/warranty/**` (customer) | `RequestReturn` `POST /api/returns` · `ListMyReturns` `GET /api/returns` · `GetCoverage` `GET /api/warranty/{serial}` |
| `/api/admin/fulfillment/**` (staff) | `ListPackTasks` `GET …/tasks` · `StartPacking` `POST …/tasks/{task_id}/start` · `ScanSerial` `POST …/tasks/{task_id}/serials` · `UnassignSerial` `POST …/tasks/{task_id}/serials/unassign` · `ReplaceSerial` `POST …/tasks/{task_id}/serials/replace` · `CompletePacking` `POST …/tasks/{task_id}/complete` · `HandOver` `POST …/handover` · `ListReturns` `GET …/returns` · `ReceiveReturn` `POST …/returns/{return_id}/receive` · `RecordInspection` `POST …/returns/{return_id}/inspection` · `DecideReturn` `POST …/returns/{return_id}/decision` · `ListDeliveryFailures` `GET …/failures` · `RecordReturnedToSender` `POST …/shipments/{shipment_id}/returned-to-sender` · `ConfirmLost` `POST …/shipments/{shipment_id}/lost` · `DismissPossiblyLost` `POST …/shipments/{shipment_id}/dismiss` · `GetCoverage` `GET …/warranty/{serial}` |
| internal, not routed | `CancelPackTask` (plain gRPC, `Bloom.Fulfillment.Contracts`) |
| webhook | `POST /webhooks/shippo/{secret}` (plain HTTP endpoint) |

- **Roles** from the trusted context (LLD 07 §4.3), per 10-accounts-access §3: packing, scans, handover, `ReceiveReturn`, `RecordInspection`, `RecordReturnedToSender`, `ListPackTasks` → Warehouse, Admin. `DecideReturn`, `ConfirmLost`, `DismissPossiblyLost`, `ListDeliveryFailures` → Support, Admin. `ListReturns` → Warehouse, Support, Admin. Staff `GetCoverage` → Support, Admin. Refusal → `PERMISSION_DENIED` / `ROLE_NOT_ALLOWED`.
- **Customer ownership** (§9): `account_id` from the trusted context vs `order_accounts.account_id`. Another customer's order or serial → `NOT_FOUND` (nothing leaked).
- **Webhook:** wrong secret → `404`; `POST` only; body ≤ 64 KB (§9); per-IP rate limit is the gateway's `webhook` policy.

## 7. Implementation notes (builder's calls unless marked)

- **Guarded updates** exactly as 05-fulfillment §6–7: single-statement conditional `UPDATE` on `status` (and `version` where the request carries one), affected-row check (00-reliability §7.5, §7.6).
- **Scans** call inventory first (sync, outside the transaction), then write locally. The staff id travels to inventory in the gRPC metadata (inventory spec §7, audit). `ReplaceSerial` replaces the row in place, keeping its `seq`.
- **`reship_id`** is derived deterministically from the lost shipment's id (name-based UUID), so a retried or taken-over `ConfirmLost(reship)` calls `CommitForReship` with the same id. `ConfirmLost(reship)` uses the guard's provider form (two-phase, 00-reliability §7.4): inventory's call sits between the two transactions.
- **Reship coverage** (§6.5): the original order's coverages stay `pending_delivery`; at `CompletePacking` of the reship task, each coverage's `serial` moves to the reship serial in the same position (by `seq`) of the same device line.
- **Tracking statuses** create failure rows: `FAILURE` (and the 7-day sweep) → `delivery_failures` `kind = lost`, `status = possibly_lost`. `RETURNED` → `kind = returned_to_sender`, `status = confirmed`; `RecordReturnedToSender` then resolves it (`resolved_refund`). `ConfirmLost` turns `possibly_lost` into `confirmed` and resolves it in the same transaction.
- **Ship-back shipments** (RET-R08): the label worker buys them like any label; `HandOver` accepts them by `shipment_id` or tracking number (no task, no message); tracking moves them `handed_over` → `in_transit` → `delivered` and nothing else (no events, no coverage, no failure handling).
- **Return shipments:** the return shipment's own `status` follows tracking (`label_ready` → `in_transit` → `delivered`) next to the return request's status of §6.3.
- **`HandOver` twice** for the same shipment (double scan) returns the shipment unchanged; any other non-`label_ready` status → `LABEL_NOT_READY` (`label_pending`) or `INVALID_STATE`.
- **Label worker retries:** `shipments` gets `label_attempts` and `next_attempt_at` (internal scheduling columns, as payment's refund executor). After the 1 s / 5 s / 30 s / 2 min schedule fails, the shipment stays `label_pending`, the error is logged, `labels_total{result="failed"}` counts it, and it retries every 2 min after that. Doc edit §9.7.
- **Refund saga:** `refund-completed` → `pending_steps` empty, `completed_at` set. `refund-failed` → saga stays open with the step pending; error log. The "stuck > 30 min" alert reads open sagas.
- **Refund lines:** every refund command carries one entry per order line it touches (returns: the decided units' amounts per line, plans included; delivery failures: `paid_cents − refunded_cents` of every line with a remainder). Shipping, when refunded, is in `amountCents` but not in `lines`.
- **Audit:** every staff write adds a `StaffActionRecorded` outbox row in its transaction; `service` = `fulfillment`, `action` = `fulfillment.<method in kebab case>` (e.g. `fulfillment.decide-return`).
- **Webhook:** unknown tracking number → `200`, nothing changed. Shippo re-fetch fails → `500` (Shippo retries).
- **Late tracking** (my call): `TRANSIT` on a `possibly_lost` shipment dismisses it automatically, like `DELIVERED` (§8), then applies `in_transit`. `RETURNED` on `possibly_lost` → `returned_to_sender`; the failure row's `kind` becomes `returned_to_sender`, `status` `confirmed`. Any event on a `lost`, `returned_to_sender` (already recorded) or `delivered` shipment is a no-op with a warning log (guarded transitions, §6.3).
- **Reship delivery** (my call, ORD-R13): a reship shipment's `DELIVERED` sets `order_accounts.delivered_at`, activates coverages (`starts_at` = that delivery), and starts the 14-day return window. Messages carry `refId` = `reship_id` for reship tasks (`ref_id`, §4.1).
- **Delivery-failure amount** (ORD-R17): `total_cents − Σ refunded_cents − (shipping_cents if shipping_refunded)`; then `shipping_refunded = true` and every line's `refunded_cents = paid_cents`, `refunded_units = quantity`.
- **Plan refunds** (my call): an automatic plan refund (§6.4.1 step 3) updates the plan line's `refunded_units` / `refunded_cents` like any unit, so a later plan-only request can't refund it again. Plan units get `return_units` rows (plan-only: `decision = full` at once, no inspection). A plan-only return cancels the coverage of that plan line attached to the **last** device serial by `seq` that still has an active or pending coverage. `return-decided.accepted[]` lists every accepted line, plans included.
- **Unit coverage of staff calls** (my call): `RecordInspection` must cover every unit of the return in one call (else `VALIDATION_FAILED`); a `received_serial` not shipped on this order sets `result = wrong_item` (§6.4). `DecideReturn` must decide every unit (else `VALIDATION_FAILED`); any decision is allowed for any inspection result (RET-R05). `RecordReturnedToSender` units must match what shipped exactly (each shipped serial once; non-serialized quantities per SKU) → else `VALIDATION_FAILED`.
- **Return expiry** (my call, RET-R15 "label not scanned within 14 days"): the hourly sweep expires `requested` and `label_issued` requests past `expires_at`; a bought return label is voided. `in_transit` and later never expire. Units of an expired request are free for a new request while the RET-R01 window is open. Doc edit §9.9.
- **Saga keying** (my call): `fulfillment_sagas` gets `refund_id` (unique); `refund-completed` / `refund-failed` find the saga by `refundId`. An unknown `refundId` (order's own refunds) → no-op. `shipments` gets `created_at` (lookup-before-buy window). Doc edit §9.7.
- **`shipment-shipped.lines[]`** lists product lines only (plans have no stock, 02-inventory).
- **`GetCoverage`** (my call): returns the `active` / `pending_delivery` row for the serial, else the newest `cancelled` one (with its status); none → `NOT_FOUND`.
- **§8 "returned-to-sender after a partial earlier refund"** can't be reached through the API (returns need a delivered order); its test seeds the line accounts directly.
- **`ListMyReturns`:** paged like the staff lists, newest first.
- **Consumers** use the shared inbox; queue names and bindings per 00-reliability §6. Unknown order on `refund-*` / `account-deleted` → no-op.
- **Metrics** of 05-fulfillment §10 on meter `Bloom.Fulfillment`.

## 8. Testing

- xUnit v3 + Testcontainers: real PostgreSQL and RabbitMQ (one container each per run). The app runs in-process (`WebApplicationFactory`), driven over gRPC, transcoded HTTP and the webhook endpoint.
- Fake Shippo: an in-process HTTP handler behind the real client, recording calls and holding tracking status per number. F-06 makes the first transaction call time out after Shippo stored it; F-07 posts a webhook whose body disagrees with the fake's status.
- Inventory: a test-double gRPC server (`AssignSerial`, `UnassignSerial`, `ReplaceSerial`, `CommitForReship`, with scripted `SOLD_OUT`).
- Concurrency test F-01 runs real parallel calls against Postgres.
- `FakeTimeProvider` for the sweeps and the return window.
- Tests need Colima running locally. All containers are removed after the run.

## 9. Proposed doc edits (not applied)

No edit under `doc/` is made in this project (AGENTS.md §9). For the owner to approve separately:

1. 05-fulfillment §5.3 `payment.refund-payment.v1`: add `reason` (`return` \| `delivery_failure`). 03-order §5.3: add `lines?`.
2. 05-fulfillment §4.3 `delivery_failures`: add `previous_status`, `dismissed_at`; §6.3 sweep clock includes `dismissed_at`; §6.5 dismiss restores `previous_status`; a re-flag reuses the row.
3. 05-fulfillment §9: account deletion anonymizes `ship_to` on pack tasks (drop "and shipments"); §4.1 note that every shipment takes its address from the order's `kind = order` pack task.
4. 05-fulfillment §5.1 `RecordReturnedToSender`: `Idempotency-Key` required. 00-reliability §7.4 "Applies to": add returned-to-sender refunds.
5. 05-fulfillment §5.4: add `INVALID_STATE`, `NOT_FOUND`, `VALIDATION_FAILED`, `ROLE_NOT_ALLOWED`, `CONCURRENCY_CONFLICT`, `IDEMPOTENCY_KEY_*`; note that inventory's scan reasons pass through.
6. 05-fulfillment §5.1: add `ListPackTasks`, `ListReturns`, `ListDeliveryFailures` (§4); §3 lists them under the Warehouse / Support APIs.
7. 05-fulfillment §4.1 `shipments`: add `label_attempts`, `next_attempt_at`, `created_at`. §4.3 `fulfillment_sagas`: add `refund_id` (unique).
8. 05-fulfillment §6.4 `RequestReturn`: a plan line and its parent device line can't be in one request (`VALIDATION_FAILED`).
9. 05-fulfillment §6.4 expiry: `requested` and `label_issued` expire (RET-R15: label not scanned), not only `label_issued`.

## 10. Left to the builder

Internal names, class and proto layout (package `bloom.fulfillment.v1`, one gRPC service per 05-fulfillment §3 API component), request / response message shapes beyond the fields 05-fulfillment §5.1 and this spec name, config key names, test isolation. Anything visible outside fulfillment or affecting the design that 05-fulfillment and this spec don't cover goes back to the owner (AGENTS.md §7).

## 11. Open items (owner)

None.
