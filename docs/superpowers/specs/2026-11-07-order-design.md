# Order service: design

| | |
|---|---|
| **Status** | Approved (2026-11-07) |
| **Date** | 2026-11-07 |
| **Branch / PR base** | `feat/order` → `feat/fulfillment` |
| **Implements** | [03-order](../../../doc/low-level-design/03-order.md) (all sections), CHK-R01 … R17, ORD-R01 … R17 (order side), PRM-R01 … R13, LCH-R13 … R16, R19, WAR-R01, ACC-R06, R13, BI-04, BI-05, HLD §5, §8.1 – §8.3, §8.5, HLD-ADR-005, HLD-ADR-013, HLD-ADR-014 (API/worker split) |
| **Builds on** | `Bloom.Shared` ([shared-library spec](2026-10-31-shared-library-design.md)), gateway routes already in place ([api-gateway spec](2026-11-01-api-gateway-design.md)), catalog `GetPricing`, inventory `ReserveStock` / `GetAvailability`, fulfillment `CancelPackTask` |

## 1. Intent

`order` turns a cart (or a launch pass) into a paid order and owns it until it is delivered, cancelled, failed or returned. It runs the checkout and cancellation sagas, holds coupons in the same transaction as the order, and expires unpaid checkouts (03-order §1).

03-order is the approved design. This spec does not restate it. It records only the owner decisions and builder calls the LLD leaves open. Where this spec is silent, 03-order decides.

## 2. Goals

1. Order **API** deployment: every method of 03-order §5.1 (with the changes in §4), and the shared DLQ admin service (`MapBloomDeadLetterAdmin`, LLD-ADR-009).
2. Order **worker** deployment: outbox relay, every consumer of 03-order §5.3, the creating sweep, the expiry sweep, the stuck-saga monitor, the retention purger (from `Bloom.Shared`).
3. Every message of 03-order §5.3 published or sent through the outbox.
4. Contracts other services need, taken from their approved LLDs (no new fields):
   - `Bloom.Launch.Contracts`: `launch_internal.proto` with `HoldAllocation` (01-launch §5.1: `order_id`, `pass_token`, `lines[{sku_id, quantity}]` → `hold_id`, `lines`); `ReleaseAllocationHoldV1(orderId, reason)`, `HoldReleasedV1(orderId)` and their message types.
   - `Bloom.Payment.Contracts`: `payment_internal.proto` with `CreatePayment` (04-payment §5.1: `order_id`, `order_number`, `account_id`, `amount_cents`, `display_lines[{name, amount_cents}]` → `payment_id`, `checkout_url`, `session_expires_at`); `PaymentSucceededV1`, `PaymentFailedV1`, `CancelPaymentV1` and their message types (04-payment §5.3).
   - `Bloom.Order.Contracts`: `OrderCancelledV1`, `OrderPreorderDelayedV1` and their message types (03-order §5.3).
5. Automated tests for O-01 … O-21 and every edge case of 03-order §8.

**Success criteria:** `dotnet build` passes with warnings as errors. `dotnet test` passes locally (Colima running) and in the existing CI workflow. Each test case O-01 … O-21 has at least one test named after its ID.

## 3. Non-goals and anti-goals

**Non-goals:** the launch and payment services (only the contracts in §2.4). Cross-service end-to-end tests: catalog, inventory, launch, payment and fulfillment are test-double gRPC servers; their messages are published to the broker by the tests. Dockerfile, Compose entries, Kubernetes manifests, Linkerd rules, alert rules (alerts are metrics + error logs here). Any change to `Bloom.Shared` other than the new guard form (§4), or to catalog, inventory, fulfillment, identity or the gateway (every order route already exists, LLD 07 §5.1). Any change under `doc/` (proposed edits in §9).

**Anti-goals** (failure even if it technically works):
- Any method, message, field or error reason that 03-order or this spec doesn't list (AGENTS.md §1).
- A hold requested before its order row is committed (OR-02).
- Coupon uses (held + consumed) above a total or per-customer limit, even under concurrent checkouts (OR-03, BI-04).
- An order both `cancelled` and shipped (OR-04).
- Line amounts that don't sum exactly to the total, or `refunded_cents > paid_cents` on any line (OR-05).
- A pre-order reaching `paid` without both `stock_allocated` and `launch_released` (OR-07).
- `order.ready-to-fulfill.v1` emitted twice for one order.
- A status change without its `order_status_history` row, or a saga command without its saga row, in the same transaction.

## 4. Owner decisions

| Topic | Decision |
|---|---|
| Scope | All of 03-order, regular and launch checkout, nothing more except the items in this table. Code + tests only. |
| Missing services | Launch and payment don't exist yet. Their gRPC methods and messages are added to their contracts projects exactly as 01-launch and 04-payment define them (§2.4). Tests use test-double gRPC servers. |
| Cart after payment | When `payment.succeeded.v1` moves a **regular** order to `paid`, the bought items leave the cart in the same transaction. Only what the order bought is removed: anything the customer added during the checkout stays (details in §7). Expired or failed checkouts leave the cart untouched. Doc edit §9.2. |
| Cart price flag | **Dropped.** `GetCart` returns each line's current price, `available` and `out_of_stock`, with no `price_changed` flag. This changes approved rule CHK-R18. **DESIGN CHANGE**, doc edit §9.1. |
| `UpdateCoupon` | Every field except the code is editable: type, value, scope (and scope items), minimum spend, start, expiry, total and per-customer limits. Orders already holding a use keep their locked discount (OR-06). A `total_limit` below the current `uses_count` → `VALIDATION_FAILED`. Doc edit §9.3. |
| Checkout idempotency | The shared guard can't do 03-order §6.2 today: its Tx 1 only claims the key, and it allows one provider call between two transactions. **`Bloom.Shared` change:** a new guard form for multi-step operations. Tx 1 claims the key **and runs a caller hook in the same transaction**; the middle step gets the claim (`user_id`, key, `resumed` flag) and may make several calls and commits; the final step stores the outcome. A deterministic business rejection returned by the middle step is stored and replayed (00-reliability §7.4 outcome rules). No change to the shared key table. Order's hook inserts the `creating` order and a row in a new order-owned table **`checkout_keys`** (PK `(account_id, idempotency_key)`, `order_id`), which a resumed request uses to find its order. Existing guard forms and their callers are unchanged. Doc edit §9.7. |
| Launch released mid-checkout | On `launch.stage-changed.v1` with `stage = released`, `launch_released = true` is set on **every** `creating`, `pending_payment` and `awaiting_stock` order of that launch, not only `awaiting_stock`. An order paid after the release enters `awaiting_stock` with the flag set and moves to `paid` when `stock-allocated` arrives. `order.preorder-delayed.v1` is sent only by the release batch, to `awaiting_stock` orders without stock. Doc edit §9.8. |
| Cart methods | `UpsertCartLine` identifies a line by (`kind`, `sku_id`) for products and (`kind`, `sku_id`, `parent_line_no`) for plans; `quantity` is absolute, 0 removes the line. Removing a device line (`RemoveCartLine` or quantity 0) removes its plan lines. Upsert checks through catalog `GetPricing`: `LAUNCH_ITEMS_IN_CART`, `SKU_NOT_BUYABLE`, `QUANTITY_LIMIT`, `WARRANTY_NOT_ELIGIBLE`. `version` is required on writes; an account with no cart row has version 0. `GetCart` per line: `available` = catalog `buyable`; `out_of_stock` = inventory `available` < line quantity (flags omitted when inventory is unreachable, 03-order §5.2). Doc edit §9.9. |
| Starting values | Migration seeds `checkout_settings` with `express_fee_cents = 1500` ($15). `appsettings.json` sets `TaxRateBasisPoints = 825` (8.25 %). Doc edit §9.4. |
| Branch | `feat/order` off `feat/fulfillment`, PR into `feat/fulfillment`. |

## 5. Layout and dependencies (builder's call)

```
src/Order/
  Bloom.Order/            # one ASP.NET Core app; runs as order-api or order-worker by config
  Bloom.Order.Contracts/  # existing: add OrderCancelledV1, OrderPreorderDelayedV1 + message types
  Bloom.Order.Tests/      # xUnit v3 + Testcontainers (PostgreSQL, RabbitMQ)
src/Launch/Bloom.Launch.Contracts/    # existing: add launch_internal.proto (client stubs), release command + hold-released records
src/Payment/Bloom.Payment.Contracts/  # existing: add payment_internal.proto (client stubs), succeeded / failed / cancel records
src/Shared/Bloom.Shared/Idempotency/  # existing: add the multi-step guard form (§4), no schema change
```

- One app with a mode setting (`api` / `worker`), like identity, catalog, inventory and fulfillment (HLD-ADR-014).
- `AddBloom<OrderDbContext>` provides outbox, inbox, consumer host, retention purger, idempotency guard, error mapping, metrics; `AddBloomTelemetry` for OTel.
- EF Core migrations: the tables of 03-order §4 with the changes in §4 and §7 of this spec, plus `AddBloomReliabilityTables`.
- Clients from the existing contracts projects: `Bloom.Catalog.Contracts` (`GetPricing`), `Bloom.Inventory.Contracts` (`ReserveStock`, `GetAvailability`, messages), `Bloom.Fulfillment.Contracts` (`CancelPackTask`, messages), `Bloom.Identity.Contracts` (`AccountDeletedV1`), `Bloom.Shared.DeadLetters.StaffActionRecorded` for audit. `HasUnsettledOrders` server stub from the existing `order_internal.proto`.
- No new NuGet packages. New projects added to `Bloom.slnx`.

## 6. Routes (builder's call)

gRPC services transcoded to JSON under the existing gateway prefixes (LLD 07 §5.1).

| Surface | Methods → path |
|---|---|
| `/api/cart/**` (customer) | `GetCart` `GET /api/cart` · `UpsertCartLine` `PUT /api/cart/lines` · `RemoveCartLine` `DELETE /api/cart/lines/{line_no}` |
| `/api/checkout` (customer, `checkout` policy) | `StartCheckout` `POST /api/checkout` |
| `/api/orders/**` (customer) | `ListMyOrders` `GET /api/orders` · `GetOrder` `GET /api/orders/{order_id}` · `CancelOrder` `POST /api/orders/{order_id}/cancel` |
| `/api/admin/orders/**` (Support, Admin) | `SearchOrders` `GET /api/admin/orders` · `GetOrder` `GET /api/admin/orders/{order_id}` · `SupportCancelOrder` `POST /api/admin/orders/{order_id}/cancel` |
| `/api/admin/coupons/**` (Catalog manager, Admin) | `ListCoupons` `GET` · `CreateCoupon` `POST` · `UpdateCoupon` `PUT …/{coupon_id}` · `DeactivateCoupon` `POST …/{coupon_id}/deactivate` |
| `/api/admin/settings/**` (Admin) | `GetCheckoutSettings` `GET …/checkout` · `SetExpressFee` `PUT …/checkout/express-fee` |
| `/api/admin/dlq/order/**` | Shared DLQ admin |
| internal, not routed | `HasUnsettledOrders` (plain gRPC, existing `order_internal.proto`) |

- **Roles** from the trusted context (LLD 07 §4.3), checked again in order per 03-order §9. Refusal → `PERMISSION_DENIED` / `ROLE_NOT_ALLOWED`.
- **Ownership:** `account_id` from the trusted context. Another customer's order → `NOT_FOUND` (nothing leaked). Orders in `creating` are `NOT_FOUND` to customers and absent from lists (03-order §6.1).

## 7. Implementation notes (builder's calls unless marked)

- **Guarded updates** exactly as 03-order §6–7: single-statement conditional `UPDATE` on `status` (and `version` where the request carries one), affected-row check, history row in the same transaction (00-reliability §7.5, §7.6).
- **StartCheckout** uses the new multi-step guard form (§4): Tx 1 claims the key and inserts the order + `checkout_keys` row; calls to catalog, inventory / launch and payment and order's Tx 2 run in the middle step; a resumed request finds its order through `checkout_keys` and continues from the order's status (03-order §6.2). The new form gets its own tests in `Bloom.Shared.Tests`; all existing shared and service tests still pass. Each outgoing call has a 5 s deadline. `HoldAllocation` forwards the customer's `x-bloom-*` headers (01-launch §5.1 needs the customer context), as fulfillment does for inventory.
- **Business rejections at the hold** (`SOLD_OUT`, `LIMIT_EXCEEDED`, `PASS_*`): the hold is all-or-nothing in one service and a launch order has no inventory hold at checkout, so there is no partial hold to release. Order → `cancelled` (`checkout_failed`), no saga. Coupon limit at Tx 2 → `cancelled` + release command + `checkout_compensation` saga (03-order §6.2).
- **Release commands per order kind:** regular → `inventory.release-reservation.v1`; launch before payment → `launch.release-allocation-hold.v1`; launch after payment → both (03-order §6.6).
- **`CreatePayment` display lines:** one entry per order line (`name`, `paid_cents`), plus `Express shipping` with `shipping_cents` when it is above 0. They sum to `total_cents` (04-payment §9). Doc edit §9.5.
- **`CancelPackTask` answers:** fulfillment's `PACKING_STARTED` → `CANCEL_NOT_ALLOWED`; `ALREADY_SHIPPED` passes through; `UNAVAILABLE` / timeout → `UNAVAILABLE` to the caller, order unchanged.
- **Refund saga steps:** `refund-completed` removes `refund` from `pending_steps` of the order's open saga that has it; empty → `completed_at`. `refund-failed` replaces `refund` with `refund_failed` and logs an error, so the saga stays open and the stuck monitor reports it. A `refund-*` with no matching open saga (fulfillment's refunds) only updates line amounts. Doc edit §9.6.
- **Line refunds:** `refund-completed` with `lines` adds each amount to that line's `refunded_cents`; `CHECK (refunded_cents <= paid_cents)` stops any overflow (OR-05). Without `lines` (order's own full refunds), line amounts are unchanged (03-order §5.3).
- **Late payment:** saga `late_payment` (step `refund`), `payment.refund-payment.v1` for `total_cents`, and `order.cancelled.v1` with reason `late_payment`. `cancel_reason` keeps its first value.
- **Pre-order release:** the `launch.stage-changed.v1` handler updates the open orders of that launch (§4) in batches of 500 with the guard `launch_released = false`; each batch is its own transaction and the inbox row commits with the last one. A redelivery after a crash only touches orders not yet updated, so `order.preorder-delayed.v1` and `order.ready-to-fulfill.v1` go out once per order. Other stages and changes are no-ops.
- **Reships** (ORD-R13): order stays `shipped`; fulfillment's reship `shipment-shipped` is a no-op (guard) and the reship `shipment-delivered` moves the order to `delivered`. `refId` is ignored.
- **Returns:** `return-decided.accepted[]` adds to `returned_qty` on every listed line (plans included); the `returned` check uses product lines only (03-order §6.7).
- **Cart removal on payment** (owner decision): for each order line, the cart line with the same `kind`, `sku_id` (and, for a plan, the cart line of its parent's SKU) loses the bought quantity; a line at 0 is deleted, and so are plan lines whose parent line was deleted.
- **Coupon code taken:** `CreateCoupon` with a code that exists ignoring case → `ALREADY_EXISTS` / `COUPON_CODE_TAKEN` (same pattern as catalog's `SLUG_TAKEN`). Deactivated coupons can't be reactivated (PRM-R13 describes deactivation only). Doc edit §9.3.
- **Other errors:** shared `NOT_FOUND`, `VALIDATION_FAILED` (`INVALID_ARGUMENT`, field in `ErrorInfo` metadata), `ROLE_NOT_ALLOWED`, `CONCURRENCY_CONFLICT`, `IDEMPOTENCY_KEY_*`. Inventory's and launch's reasons pass through (03-order §5.4). Doc edit §9.3.
- **Order number:** `BLM-` + 8 random Crockford base32 chars; on a unique conflict, generate again in the same Tx 1.
- **Paging:** `ListMyOrders` (newest first), `SearchOrders` (newest first; filters `number`, `account_id`, `status`, any combination), `ListCoupons` (newest first; filter `active?`). `page` from 1, `page_size` 1–100, default 50.
- **Audit:** every staff write adds a `StaffActionRecorded` outbox row in its transaction; `service` = `order`, `action` = `order.<method in kebab case>`.
- **Consumers** use the shared inbox; queue names and bindings per 00-reliability §6. Unknown order → no-op.
- **Metrics** of 03-order §10 on meter `Bloom.Order`.

## 8. Testing

- xUnit v3 + Testcontainers: real PostgreSQL and RabbitMQ (one container each per run). The app runs in-process (`WebApplicationFactory`), driven over gRPC and transcoded HTTP.
- Test-double gRPC servers for catalog (`GetPricing`), inventory (`ReserveStock`, `GetAvailability`), launch (`HoldAllocation`), payment (`CreatePayment`) and fulfillment (`CancelPackTask`), each scriptable (errors, delays, timeouts).
- Tests publish payment, inventory, launch, fulfillment and identity messages to the broker, and read order's commands and events off it.
- Concurrency tests (O-05 coupon race, paid-vs-expired race) run real parallel work against Postgres.
- `FakeTimeProvider` for sweeps, deadlines and the 14-day unsettled window.
- Tests need Colima running locally. All containers are removed after the run.

## 9. Proposed doc edits (not applied)

No edit under `doc/` is made in this project (AGENTS.md §9). For the owner to approve separately, upstream first:

1. **DESIGN CHANGE — cart price flag.** New PD-ADR-009 "No price-change flag in the cart": the cart stores no price, so "changed" has no reference; the binding check stays at checkout start (CHK-R04). CHK-R18: drop "or have changed price". 03-order §5.1 `GetCart`: drop `price_changed`. 03-order §8: "Cart item price changed since added" → "Shown at the current price; checkout uses it."
2. **Cart after payment.** 05-checkout-payment: new CHK-R19 "When payment succeeds, the items bought leave the cart; items added during the checkout stay." 03-order §6.4: add the cart removal to the `pending_payment` → `paid` row (regular orders).
3. 03-order §5.1 `UpdateCoupon`: every field except the code; `total_limit` ≥ `uses_count`. §5.4: add `COUPON_CODE_TAKEN` (`ALREADY_EXISTS`), `NOT_FOUND`, `VALIDATION_FAILED`, `ROLE_NOT_ALLOWED`, `CONCURRENCY_CONFLICT`, `IDEMPOTENCY_KEY_*`; note fulfillment's `PACKING_STARTED` maps to `CANCEL_NOT_ALLOWED`.
4. 03-order §4.4: seed `express_fee_cents = 1500`; default `TaxRateBasisPoints = 825`.
5. 03-order §5.2 `CreatePayment`: add `order_number` and `display_lines` (one per order line, plus Express shipping), matching 04-payment §5.1.
6. 03-order §4.1 `order_sagas.pending_steps`: add `refund_failed`.
7. **Docs vs code: idempotency guard.** 00-reliability §7.4: add a third execution form for multi-step operations (Tx 1 claim + caller hook; middle step with several calls and commits; final step stores the outcome; returned business rejections stored). 03-order §4.1: add `checkout_keys` (`account_id`, `idempotency_key`, `order_id`); §6.2 step 2: "insert the order and its `checkout_keys` row" instead of "stored with the key". LLD-ADR-001 stays; if you'd rather record the new form as a decision, a new LLD-ADR-011 supersedes nothing and only extends it.
8. 03-order §5.3 `launch.stage-changed.v1` and §6.5: on `released`, flag every `creating`, `pending_payment` and `awaiting_stock` order of the launch; §8: add "Launch released while a checkout is open" → paid order waits only for stock.
9. 03-order §5.1 `UpsertCartLine` / `RemoveCartLine` / `GetCart`: line identity, absolute quantity, plan cascade, add-time checks, `available` and `out_of_stock` meanings (§4 of this spec).

## 10. Left to the builder

Internal names, class and proto layout (package `bloom.order.v1`, one gRPC service per 03-order §3 API component), request / response message shapes beyond the fields 03-order §5.1 and this spec name, config key names, test isolation. Anything visible outside order or affecting the design that 03-order and this spec don't cover goes back to the owner (AGENTS.md §7).

## 11. Open items (owner)

None.
