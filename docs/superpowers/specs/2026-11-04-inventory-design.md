# Inventory service: design

| | |
|---|---|
| **Status** | Approved (2026-11-04) |
| **Date** | 2026-11-04 |
| **Branch / PR base** | `feat/inventory` → `feat/catalog` |
| **Implements** | [02-inventory](../../../doc/low-level-design/02-inventory.md) (all sections), INV-R01 … R20, BI-02, BI-06, BI-07, LCH-R19, R20, HLD §5, §6, §7, §8.5, HLD-ADR-009, HLD-ADR-013, HLD-ADR-014 (API/worker split) |
| **Builds on** | `Bloom.Shared` ([shared-library spec](2026-10-31-shared-library-design.md)), gateway routes already in place ([api-gateway spec](2026-11-01-api-gateway-design.md)), catalog's `LockSkuForStock` ([catalog spec](2026-11-02-catalog-design.md)) |

## 1. Intent

`inventory` is the source of truth for how many units of each SKU Bloom has, which serial units exist, and every change to them. It never lets regular sales exceed stock (BI-02), gives arriving launch stock to waiting pre-orders first (INV-R10), and records every change in an append-only ledger (BI-06) (02-inventory §1).

02-inventory is the approved, complete design. This spec does not restate it. It records only the owner decisions and builder calls the LLD leaves open. Where this spec is silent, 02-inventory decides.

## 2. Goals

1. Inventory **API** deployment: all 11 methods of 02-inventory §5.1 (Stock, Packing, Warehouse APIs of §3) and the shared DLQ admin service (`MapBloomDeadLetterAdmin`, LLD-ADR-009).
2. Inventory **worker** deployment: outbox relay, the six consumers of §5.3, the stuck-reservation monitor (§3), the retention purger (from `Bloom.Shared`).
3. Backorder allocator (§6.4) in-process, called from every caller §6.4 lists.
4. Published messages: `inventory.stock-allocated.v1`, `inventory.reservation-released.v1`, `identity.staff-action-recorded.v1` (receipts, adjustments, serial changes), all through the outbox.
5. Contracts other services need: inventory's internal `.proto` (`ReserveStock`, `GetAvailability`, `CommitForReship`, `AssignSerial`, `UnassignSerial`, `ReplaceSerial`) and payload records for the messages inventory publishes and consumes that have none yet (§5).
6. `stock_ledger` append-only at the database permission level (§4).
7. Automated tests for I-01 … I-18 and every edge case of 02-inventory §8.

**Success criteria:** `dotnet build` passes with warnings as errors. `dotnet test` passes locally (Colima running) and in the existing CI workflow. Each test case I-01 … I-18 has at least one test named after its ID.

## 3. Non-goals and anti-goals

**Non-goals:** the order, launch and fulfillment services (only the contracts in §5). Cross-service end-to-end tests: order and fulfillment are faked by publishing their messages and calling inventory's gRPC directly. Dockerfile, Compose entries, Kubernetes manifests, Linkerd allow rules (§9 of the LLD: callers are restricted at the mesh, not in code). Any change to `Bloom.Shared` or catalog. Any gateway change (routes `/api/admin/inventory/**` and `/api/admin/dlq/inventory/**` already exist). Any change under `doc/` (proposed edits in §9). IR-08 p95 is not asserted; it's deferred to the cluster load test.

**Anti-goals** (failure even if it technically works):
- Any method, message, field or error reason that 02-inventory or this spec doesn't list (AGENTS.md §1).
- `reserved > on_hand` ever, or `available` negative (IR-01).
- Received or freed units visible to a regular `ReserveStock` before the allocator has run in the same transaction (IR-02).
- A backorder served while an older one for the same SKU is unfilled, or a backorder line split (strict FIFO, §6.4).
- A counter change without a ledger row in the same transaction, or any `UPDATE` / `DELETE` of a ledger row succeeding (IR-03).
- Inventory releasing a reservation on its own, including from the stuck-reservation monitor (IR-05).
- A partially applied goods receipt (IR-07).
- Locks on `stock_items` taken in any order other than ascending `sku_id`.

## 4. Owner decisions

| Topic | Decision |
|---|---|
| Scope | All of 02-inventory, nothing more except the items in this table. Code + tests only. |
| `ListStockLevels` filters | `product_id?`, `sku_ids[]?`, `available_at_most?` (int ≥ 0; `0` = sold out), `has_backorders?` (bool: SKU has ≥ 1 `backordered` allocation). Filters AND together. Paged (`page` from 1, `page_size` 1–100, default 50), ordered by `sku_id`. Each item: `sku_id`, `product_id`, `serialized`, `on_hand`, `reserved`, `available`, `committed`, `returned`, `damaged`, `lost`, `backordered` (sum of `backordered` quantities), `updated_at`. Never-received SKUs have no row and don't appear. Doc edit §9.1. |
| Receipt IMEIs | `RecordGoodsReceipt` lines keep `serials[]?` and `imeis[]?` as parallel arrays: `imeis[i]` belongs to `serials[i]`. `imeis[]` is empty or exactly as long as `serials[]`; any other length → `RECEIPT_INVALID` for that line. An empty-string entry isn't allowed (a line is all-IMEI or no-IMEI). IMEIs on a non-serialized line → `RECEIPT_INVALID`. Doc edit §9.2. |
| Serialized adjustments | One unit per call. **Decrease:** `delta = −1` with `serial` (must be `in_stock`, of that SKU; `assigned` → `SERIAL_ASSIGNED`; other states or unknown → `SERIAL_NOT_AVAILABLE`). **Increase:** `delta = +1` with exactly one of `serial` (a `lost` unit of that SKU back to `in_stock`, `lost −= 1`) or `new_serials` of length 1 plus optional parallel `new_imeis` of length 1 (validated as in a receipt; `receipt_id` of the new `serial_units` row = the adjustment id). Any other combination → `VALIDATION_FAILED`. Non-serialized SKUs: any non-zero `delta`, no `serial` / `new_serials`. Doc edit §9.3. |
| Error reasons beyond §5.4 | Reused, no new codes. `NOT_FOUND` (`NOT_FOUND`): `GetSerial` for an unknown serial. `VALIDATION_FAILED` (`INVALID_ARGUMENT`, field name in `ErrorInfo` metadata): bad field, e.g. empty `lines`, duplicate `sku_id` in `ReserveStock` / `CommitForReship`, quantity ≤ 0, `delta = 0`, reason code not allowed in that direction (§6.8), note empty or > 500, bad serial / IMEI format outside a receipt, `page_size` out of range, the serialized-adjustment shapes above. `NOT_COMMITTED`: `AssignSerial` when no allocation `(ref_id, sku_id)` exists, as well as when it isn't `committed`. `SERIAL_NOT_AVAILABLE`: `UnassignSerial` / `ReplaceSerial` when the old serial isn't `assigned` to `ref_id` (and isn't the idempotent `in_stock` case of §6.6). `SKU_NOT_STOCKED`: `AdjustStock` or `CommitForReship` on a SKU with no `stock_items` row. Shared from 00-reliability: `ROLE_NOT_ALLOWED`, `CONCURRENCY_CONFLICT`. Doc edit §9.4. |
| Ledger append-only | As 02-inventory §4.4: two Postgres roles. Migrations run as the database owner. The service connects as an app role that has full DML on its tables except `stock_ledger`, where it has `INSERT, SELECT` only. A migration issues the `GRANT` / `REVOKE` statements to the app role (name from config, default `inventory_app`); creating the role itself is a deployment step. Config has two connection strings: `Inventory:Database` (app role) for the service, `Inventory:MigrationDatabase` (owner) for migrations. Tests create the role in the container and run the service as it (I-18). |
| First-receipt order | Replaces the order of 02-inventory §6.3 steps 1–2: (a) run every check that doesn't need `serialized` (quantity > 0, no duplicate lines, serial / IMEI format, duplicates within the request, serials or IMEIs already in `serial_units`, IMEI array length); (b) call catalog `LockSkuForStock` for each SKU with no `stock_items` row (outside the transaction, idempotent); (c) run the `serialized`-dependent checks (serial count = quantity; no serials / IMEIs on a non-serialized line). Errors from (a) and (c) are collected into one `RECEIPT_INVALID`; if (a) has errors, (b) and (c) are skipped. **Accepted:** a receipt rejected at (c) can leave the SKU locked in catalog. Catalog unavailable at (b) → `UNAVAILABLE`, nothing written (§8). Steps 3–5 of §6.3 unchanged. Doc edit §9.5. |
| Retry guard on staff writes | `RecordGoodsReceipt` and `AdjustStock` **require** an `Idempotency-Key` header and run through the shared idempotency guard (00-reliability §7.4, LLD-ADR-001), claimed in the same transaction as the write. A retry with the same key returns the first `receipt_id` / `adjustment_id`; missing / invalid / reused key → the shared `IDEMPOTENCY_KEY_*` reasons. A receipt that fails before the transaction (validation, catalog down) leaves no completed key, so the retry runs again. Doc edit §9.7. |
| Testing across services | No order or fulfillment service exists. Tests publish their messages to the broker and call inventory's gRPC directly; catalog's `LockSkuForStock` is a test double gRPC server. |
| Branch | `feat/inventory` off `feat/catalog`, PR into `feat/catalog`. |

## 5. Layout and dependencies (builder's call)

```
src/Inventory/
  Bloom.Inventory/            # one ASP.NET Core app; runs as inventory-api or inventory-worker by config
  Bloom.Inventory.Contracts/  # inventory_internal.proto (client + server stubs) for order and fulfillment;
                              # payload records StockAllocatedV1, ReservationReleasedV1, ReleaseReservationV1
  Bloom.Inventory.Tests/      # xUnit v3 + Testcontainers (PostgreSQL, RabbitMQ)
src/Order/
  Bloom.Order.Contracts/      # existing: add OrderPaidV1 (03-order §5.3 fields) and its message type constant
src/Fulfillment/
  Bloom.Fulfillment.Contracts/ # new: ShipmentShippedV1 (05-fulfillment §5), ReceiveCustomerReturnV1,
                               # ReceiveReturnedToSenderV1, MarkLostInTransitV1 (02-inventory §5.3 fields)
```

- One app with a mode setting (`api` / `worker`), like identity and catalog (HLD-ADR-014).
- `AddBloom<InventoryDbContext>` provides outbox, inbox, consumer host, retention purger, error mapping, metrics; `AddBloomTelemetry` for OTel.
- EF Core migrations in `Bloom.Inventory`: the tables of 02-inventory §4, `AddBloomReliabilityTables`, and the ledger grants of §4.
- Catalog client from the existing `Bloom.Catalog.Contracts`. Staff-action payload is the existing `Bloom.Shared.DeadLetters.StaffActionRecorded`.
- No new NuGet packages. Everything needed is already pinned.
- All new projects added to `Bloom.slnx`.

## 6. Routes (builder's call)

gRPC services transcoded to JSON for the Warehouse API, under the existing gateway prefix `/api/admin/inventory/**` (LLD 07 §5.1).

| Surface | Methods → path |
|---|---|
| `/api/admin/inventory/**` (staff) | `ListStockLevels` `GET /api/admin/inventory/stock` (query string) · `RecordGoodsReceipt` `POST /api/admin/inventory/receipts` · `AdjustStock` `POST /api/admin/inventory/adjustments` · `GetLedger` `GET /api/admin/inventory/ledger/{sku_id}` · `GetSerial` `GET /api/admin/inventory/serials/{serial}` |
| internal, not routed | `ReserveStock`, `GetAvailability`, `CommitForReship`, `AssignSerial`, `UnassignSerial`, `ReplaceSerial` (plain gRPC, `Bloom.Inventory.Contracts`) |

- Per-method role checks from the trusted context (LLD 07 §4.3), per 10-accounts-access §3: `ListStockLevels` Warehouse, Catalog manager, Support, Admin · `GetLedger` Warehouse, Admin · `RecordGoodsReceipt`, `AdjustStock` Warehouse, Admin · `GetSerial` Warehouse, Support, Admin (02-inventory §5.1). Refusal → `PERMISSION_DENIED` / `ROLE_NOT_ALLOWED`.
- `GetLedger` (my call): `sku_id` required; `from?`, `to?` (timestamps, `from` inclusive, `to` exclusive); paged (`page`, `page_size` 1–100, default 50); newest first by `(created_at, id)`. An unknown SKU returns an empty page.

## 7. Implementation notes (builder's calls unless marked)

- **Guarded updates** exactly as 02-inventory §6 (single-statement conditional `UPDATE`, 00-reliability §7.5). Multi-SKU operations sort lines by `sku_id` before touching rows. Allocations and serials use their `version` / status guards (00-reliability §7.6).
- **Allocator** is one routine taking the open transaction and a SKU id, called with the `stock_items` row locked (`SELECT … FOR UPDATE`). Every caller §6.4 lists calls it: pre-order `order.paid`, receipt, release of `committed`, returned-to-sender `sealed`, increase adjustment.
- **`stock-allocated`** is written when every `preorder` allocation of the order is `committed` (§6.4 step 3), at most once per order per transaction.
- **Release command** applies §6.5 to every allocation with `order_id` = the command's `orderId`, reship allocations included. It always writes `inventory.reservation-released.v1`, also when nothing changed.
- **`shipment-shipped`** works on inventory's own allocation and assigned serials for `refId`. A serialized line with fewer assigned serials than its quantity → permanent failure (DLQ + alert), nothing changed (I-12). The message's `serials[]` aren't used to change state.
- **`receive-customer-return`** units carry `serial` for serialized SKUs (serial `shipped` → `returned`) or `quantity` for non-serialized; `returned += q`. A serial not in `shipped` state → permanent failure (DLQ).
- **`mark-lost-in-transit`**: serialized → serials `shipped` for `refId` become `lost`; non-serialized → the `shipped` allocation quantities of `refId`; `lost += q` per SKU.
- **Unexpected state in a consumed message** (my call). Allocation status not the one §6.5 / §6.7 acts on → that allocation is skipped, no-op (the status guards of §7). A **serial** in the wrong state (e.g. returned-to-sender for a serial that isn't `shipped`, lost-in-transit with no `shipped` serial for `refId`, customer return of a serial not `shipped`) → permanent failure (DLQ + alert), nothing changed: it means real drift between fulfillment and inventory.
- **Ledger `actor`**: the staff id from the trusted context for Warehouse API writes; `inventory` for consumer-driven and service-call writes. `ref_type` ∈ `order`, `reship`, `receipt`, `adjustment`.
- **Audit.** Every receipt, adjustment and serial operation adds a `StaffActionRecorded` outbox row in its transaction; `service` = `inventory`, `action` = `inventory.<event>` (`inventory.goods-received`, `inventory.stock-adjusted`, `inventory.serial-assigned`, `inventory.serial-unassigned`, `inventory.serial-replaced`). Packing operations carry the staff id fulfillment forwards in the gRPC metadata (trusted context), or none if absent.
- **Stuck-reservation monitor** (worker): every minute, counts allocations `reserved` with `created_at` older than 30 min and sets the `stuck_reservations` gauge. Reports only (IR-05).
- **Validation** per 02-inventory §9: serials trimmed and upper-cased, then 1–32 chars `[A-Z0-9]`; IMEI 15 digits with a valid Luhn check digit.
- **`GetAvailability`** for a never-received SKU → `available = 0` (§8).
- **Consumers** use the shared inbox; queue names and bindings per 00-reliability §6 and 02-inventory §5.3.
- **Metrics** of 02-inventory §10 on meter `Bloom.Inventory`.

## 8. Testing

- xUnit v3 + Testcontainers: real PostgreSQL and RabbitMQ (one container each per run). The app runs in-process (`WebApplicationFactory`), driven over gRPC and transcoded HTTP.
- The service connects as the restricted app role in tests, so I-18 runs against the real grants.
- Catalog `LockSkuForStock` is a test double; I-08 counts its calls, and a "catalog down" test covers §8.
- Concurrency tests (I-01, I-06) run real parallel calls against Postgres.
- `FakeTimeProvider` for the stuck-reservation monitor.
- Tests need Colima running locally. All containers are removed after the run.

## 9. Proposed doc edits (not applied)

No edit under `doc/` is made in this project (AGENTS.md §9). For the owner to approve separately:

1. 02-inventory §5.1 `ListStockLevels`: filters, paging and response fields of §4.
2. 02-inventory §5.1 `RecordGoodsReceipt`: `imeis[]` parallel to `serials[]`, empty or same length.
3. 02-inventory §5.1 / §6.8 `AdjustStock`: serialized adjustments are one unit per call (`delta = ±1`) with the shapes of §4.
4. 02-inventory §5.4: add `NOT_FOUND`, `VALIDATION_FAILED`, `ROLE_NOT_ALLOWED`; note the extra cases for `NOT_COMMITTED`, `SERIAL_NOT_AVAILABLE`, `SKU_NOT_STOCKED`.
5. 02-inventory §6.3: the (a) → lock → (c) validation order of §4, and that a rejected first receipt may leave the SKU locked in catalog.
6. 02-inventory §5.1 `GetLedger`: `from` / `to`, paging, newest first (§6).
7. 02-inventory §5.1: `RecordGoodsReceipt` and `AdjustStock` require an `Idempotency-Key` (00-reliability §7.4, single-transaction form); §5.4 lists the shared `IDEMPOTENCY_KEY_*` reasons. 00-reliability §7.4 "Applies to": add stock-changing staff operations (goods receipt, stock adjustment) next to the money-moving ones.

## 10. Left to the builder

Internal names, class and proto layout (package `bloom.inventory.v1`, one gRPC service per 02-inventory §3 API component), request / response message shapes beyond the fields 02-inventory §5.1 and this spec name, test isolation, package versions (latest stable compatible with .NET 10). Anything visible outside inventory or affecting the design that 02-inventory and this spec don't cover goes back to the owner (AGENTS.md §7).

## 11. Open items (owner)

None.
