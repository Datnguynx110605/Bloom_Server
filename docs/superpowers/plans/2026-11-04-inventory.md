# Inventory Service Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the inventory service of `doc/low-level-design/02-inventory.md` (API and worker as one app), `Bloom.Inventory.Contracts`, and the payload records order and fulfillment publish to inventory.

**Architecture:** One ASP.NET Core app `Bloom.Inventory`; `Inventory:Mode` = `api` | `worker` (HLD-ADR-014). API mode hosts three gRPC services, one per LLD §3 API component: `InventoryStock` and `InventoryPacking` (plain gRPC, proto in Contracts) and `InventoryWarehouse` (transcoded to JSON under `/api/admin/inventory`), plus the shared DLQ admin. Worker mode runs the `Bloom.Shared` outbox relay, consumer host (six consumers), retention purger and the stock monitor. PostgreSQL through EF Core (`AddBloom<InventoryDbContext>`); counters change only through guarded single-statement SQL; the backorder allocator runs inside the caller's transaction. The service connects as a restricted role that cannot update or delete `stock_ledger`.

**Tech Stack:** .NET 10, ASP.NET Core gRPC + JSON transcoding, EF Core 10 + Npgsql, Bloom.Shared, xUnit v3, Testcontainers (PostgreSQL, RabbitMQ), Microsoft.Extensions.TimeProvider.Testing.

**Spec:** `docs/superpowers/specs/2026-11-04-inventory-design.md` (approved; cited "spec §x"). Design source: `doc/low-level-design/02-inventory.md` (cited "§x"). Where the spec is silent, 02-inventory decides; where both are silent, the rulings below decide.

## Owner items (plan approved 2026-11-04)

Items 1–4 approved as recommended with the plan. Item 5 stays a report: parked in the PR as a design risk; no change to `doc/` in this branch.

1. **Warranty-plan lines in a pre-order's `order.paid.v1`.** LCH-R13: a launch order holds launch items plus their warranty plans; 03-order §5.3 gives `order.paid.v1` `lines[{skuId, quantity}]` without a kind. Inventory can't tell a plan SKU from a launch SKU not yet received (neither has a `stock_items` row), so a plan line would become a backorder that never fills: it blocks every later pre-order of that SKU (strict FIFO) and the order never gets `stock-allocated`. **Recommendation:** `order.paid.v1` `lines` carry **product lines only**, as `ReserveStock` and `HoldAllocation` already do (03-order §6.1 step 5); `OrderPaidV1` documents it; inventory backorders every line. Proposed doc edit 8: 03-order §5.3 "`lines` = product lines (no warranty plans)". Alternative: add `kind` to each line (additive) and inventory skips plans. (Task 5)
2. **Audit actor when fulfillment forwards no staff** (spec §7 "or none if absent"). `StaffActionRecorded.ActorId` is a non-null `Guid` and identity's `audit_log.actor_id` / `actor_role` are `NOT NULL`. **Recommendation:** `ActorId = Guid.Empty`, `ActorRole = ""`. Alternative: refuse packing calls without `x-bloom-user-id` (`InvalidOperationException`, as catalog). (Task 6)
3. **Serialized decrease with `count_correction` / `other`.** §6.8 says the serial "becomes `damaged` / `lost`", which maps only the first two reason codes. **Recommendation:** a serialized decrease accepts only `damaged_in_warehouse` (serial → `damaged`) and `lost_in_warehouse` (serial → `lost`); `count_correction` / `other` with `delta = −1` on a serialized SKU → `VALIDATION_FAILED reason_code`. Increases keep all three codes (spec §4). Doc edit 9: 02-inventory §6.8. Alternative: serial → `lost`, only `on_hand` changes. (Task 9)
4. **Error reason for "catalog unavailable" on a first receipt.** §5.2 says the receipt fails with `UNAVAILABLE`; 00-reliability §8 needs a `reason` on every error. **Recommendation:** status `UNAVAILABLE`, reason `CATALOG_UNAVAILABLE` (as identity's `ORDER_SERVICE_UNAVAILABLE`). Doc edit 10: 02-inventory §5.4. (Task 3)
5. **Report only — release processed before `order.paid` of a pre-order.** §7 calls "delayed paid after a release" impossible, but `order.paid.v1` (`inventory.order-paid` queue) and the release command (`inventory.release-reservation` queue) travel on different queues; a retry of `order.paid` (1 s … 2 min) lets the release run first. Inventory then finds nothing to release, and the later `order.paid` creates backorders for a cancelled order that block strict FIFO for that SKU forever. **Recommendation:** build as the LLD says in this branch and park this in the PR as a design risk for the owner (a fix needs a tombstone or an order-side guard: a design change with an ADR). No task.

## Global Constraints

- Build settings from `Directory.Build.props` (net10.0, Nullable, ImplicitUsings, `TreatWarningsAsErrors`). Central versions in `Directory.Packages.props`. **No new packages** (spec §5).
- No method, message, field, error reason, metric or route that 02-inventory, the spec or the owner items don't list (AGENTS.md §1). No health endpoints. Never touch `doc/`, `Bloom.Shared`, catalog, the gateway, Dockerfiles, Compose or manifests (spec §3).
- IR-01: `reserved ≤ on_hand` and every counter ≥ 0, enforced by `CHECK` and by guarded `UPDATE … WHERE` (§6, 00-reliability §7.5). Every counter change writes a `stock_ledger` row in the same transaction (IR-03). Inventory never releases a reservation on its own (IR-05).
- `stock_items` rows are locked in ascending `sku_id` order, always (§6, §7).
- Every receipt, adjustment and packing operation publishes `StaffActionRecorded` through `OutboxWriter.PublishEvent(IdentityMessages.StaffActionRecorded, …)` in its transaction; `service = inventory` (spec §7).
- Business times come from the injected `TimeProvider`; SQL takes timestamps as parameters, never `now()` (the idempotency guard's own SQL is excepted).
- Every error is `BloomErrors.Create` shape (ErrorInfo domain `bloom`); reasons and statuses from Task 1's table only.
- JSON field names are proto lowerCamel; `int64` fields are JSON strings.
- Docker via Colima: never run `colima`; daemon unreachable → stop and report. Never `dotnet run`. Test containers must be gone after the run.
- Every test case I-01 … I-18 has at least one test whose method name starts with its ID: `I01_…`. Test names below are binding; listed assertions are the minimum.
- Test command: `dotnet test --project src/Inventory/Bloom.Inventory.Tests`, narrowed with `--filter-class '*<Class>'`.

## Review Focus

1. **Lock-order deadlocks.** Multi-SKU calls that list SKUs in descending order, run concurrently with each other and with receipts, must all finish; none may fail with Postgres `40P01`. Test in Task 4.
2. **Serial input normalization.** `" abc123 "` in a receipt, a scan, an adjustment or `GetSerial` means `ABC123`; two serials in one receipt differing only by case are duplicates. Tests in Tasks 3 and 6.
3. **Retried staff writes.** The same `Idempotency-Key` replays the first `receipt_id` / `adjustment_id` and nothing is counted twice; a receipt rejected before its transaction can be fixed and resent with the same key. Tests in Tasks 3 and 9.
4. **Messages about refs inventory doesn't hold.** A release for an unknown order still publishes `reservation-released`; a shipment for an unknown `refId` changes nothing; lost-in-transit for an unknown `refId` dead-letters. Tests in Tasks 7 and 8.
5. **Ledger completeness.** After a mixed run (receipt, reserve, paid, backorder, release, ship, return, adjust), each SKU's summed ledger deltas equal its counters (IR-03). Test in Task 9.

## Rulings in this plan (builder's calls the spec left open)

- **R1** Tests run the app on `TestServer` (as catalog R1). gRPC is called through `GrpcChannel.ForAddress(base, new() { HttpHandler = server.CreateHandler() })`; tests emulate the gateway with `x-bloom-user-id/type/role`.
- **R2** The app never migrates at startup. Tests migrate as the database owner. `DesignTimeFactory` uses env `Inventory__MigrationDatabase` if set, else `Host=design-time;Database=inventory` (spec §4: the owner string is for migrations only; the app never reads it).
- **R3** `TryAddSingleton(TimeProvider.System)`; tests register `FakeTimeProvider` first.
- **R4** Config section `Inventory`: `Mode`, `Database` (app role), `RabbitMq`, `Catalog` (absolute URL of catalog's gRPC endpoint). Both modes need every key. Missing → throw at build naming the key. `appsettings.json` placeholders only: `Mode=api`, `RabbitMq=amqp://rabbitmq:5672`, `Catalog=http://catalog:8080`.
- **R5** `BloomOptions.ServiceName = "inventory"`; telemetry `inventory-api` / `inventory-worker`; meter `Bloom.Inventory`.
- **R6** Both modes register the six consumers (the DLQ admin needs the catalog, as catalog R6). Only the worker calls `AddWorker()` and adds `StockMonitor`.
- **R7** Roles (identity's values) per spec §6: `ListStockLevels` `warehouse, catalog_manager, support, admin`; `GetLedger` `warehouse, admin`; `RecordGoodsReceipt`, `AdjustStock` `warehouse, admin`; `GetSerial` `warehouse, support, admin`. Refusal → `PERMISSION_DENIED` / `ROLE_NOT_ALLOWED`. Missing `x-bloom-*` on a Warehouse method → `InvalidOperationException`. The role check runs before validation and before the idempotency guard (a refused call claims no key).
- **R8** Ledger grants (spec §4). The `Initial` migration ends with SQL that grants to the role named by the Postgres setting `inventory.app_role` (set by the migration connection: `Options=-c inventory.app_role=<name>`), default `inventory_app`: `GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public`, `ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES`, then `REVOKE UPDATE, DELETE, TRUNCATE ON stock_ledger`. A `DO` block with `format('%I', role)`. Creating the role is a deployment step; tests create it once per container.
- **R9** Locking. Every write that may run the allocator, or touches serials, first locks its `stock_items` rows with one `SELECT … WHERE sku_id = ANY(@ids) ORDER BY sku_id FOR UPDATE`. `ReserveStock` and `CommitForReship` instead run their guarded `UPDATE` per SKU in ascending order (the update takes the lock, §6.1). Packing methods also lock the allocation row `FOR UPDATE`. Decreases stay guarded (`WHERE on_hand - reserved >= @q`, `committed >= @q`, `lost >= 1`); `CHECK` constraints are the backstop.
- **R10** Transactions. `InventoryStock` and `InventoryPacking` methods begin and commit their own transaction. Warehouse writes run inside `IdempotencyGuard.ExecuteAsync` (single-transaction form; the guard owns the transaction). Consumers run in `ConsumerHost`'s transaction and never begin or commit one. Rows inserted through EF are saved (`SaveChangesAsync`) before any later SQL in the same transaction reads them.
- **R11** Ledger. One row per SKU per change, through one scoped `Ledger.Add(Guid skuId, string entryType, Deltas d, string refType, Guid refId)`; `actor` = the staff id (`D` form) for Warehouse writes, else `inventory`. `ref_type` / `ref_id`: receipts `receipt` / receipt id; adjustments `adjustment` / adjustment id; allocation changes `reship` when the allocation's kind is `reship`, else `order`, with the allocation's `ref_id`; customer return `order` / `orderId`; returned-to-sender and lost-in-transit `reship` or `order` by the allocation kind of `refId` (`order` when none). Deltas per §6. Each row adds 1 to `ledger_entries_total{entry_type}`.
- **R12** Allocator (§6.4, spec §7): `Allocator.RunAsync(Guid skuId, CancellationToken ct)`, caller holds the row lock. No `stock_items` row → no-op. Otherwise reads `backordered` allocations by `(paid_at, order_id)`; per allocation: guarded `on_hand -= q, committed += q WHERE on_hand - reserved >= q`, else stop; status `committed` (`WHERE status = 'backordered'`, `version + 1`); ledger `backorder_allocated`; `backorder_wait_seconds` = now − `paid_at`. After each run, for every order it committed: if no `preorder` allocation of that order is in another status, write `StockAllocatedV1(orderId)` unless this scope already wrote it (a `HashSet<Guid>` in the scoped allocator).
- **R13** Receipt (spec §4 order). Before the guard: `lines` empty → `VALIDATION_FAILED lines`; `note` > 500 chars → `VALIDATION_FAILED note` (empty allowed). (a) per line, collected as violations: `sku_id` not a UUID or duplicated; `quantity ≤ 0`; each serial not valid after normalization (R14) or repeated in the request; each IMEI invalid or repeated; `imeis` non-empty with a length ≠ `serials`; serials / IMEIs already in `serial_units` (one query). (b) only if (a) is clean: `LockSkuForStock` (2 s deadline) for each SKU with no `stock_items` row; catalog `NOT_FOUND` → violation on `lines[i].sku_id`; any other failure → owner item 4. (c) serialized: serial count = quantity; non-serialized: no serials, no IMEIs. Violations from (a) or (b)+(c) → `RECEIPT_INVALID`: `InvalidArgument`, `ErrorInfo` plus one `google.rpc.BadRequest` with a `FieldViolation` per problem in request order (`field` = `lines[i].sku_id`, `lines[i].quantity`, `lines[i].serials`, `lines[i].serials[j]`, `lines[i].imeis`, `lines[i].imeis[j]`). Inside the guard: insert missing `stock_items` (`ON CONFLICT DO NOTHING`, counters 0), lock (R9), insert receipt, lines, `serial_units` (`in_stock`, `receipt_id`), save; a unique violation on `serial_units` here (a concurrent receipt won the race) → `RECEIPT_INVALID` with one violation on `lines` naming the constraint. Then `on_hand += q` and ledger `received` per SKU, the allocator per SKU ascending, audit `inventory.goods-received` (`target_type` `receipt`, `details` `{lines:[{skuId, quantity}]}`).
- **R14** Serial = `Trim().ToUpperInvariant()`, then `^[A-Z0-9]{1,32}$`. IMEI = `^[0-9]{15}$` with a valid Luhn check digit. One static `Identifiers` class: `string? NormalizeSerial(string)`, `bool IsValidImei(string)`. Outside a receipt a bad value → `VALIDATION_FAILED` with the field name.
- **R15** Validation outside receipts (`VALIDATION_FAILED`, `field` = proto field name): ids must be UUIDs (`D` form accepted case-insensitively); `lines` non-empty, `quantity > 0`, no duplicate `sku_id` (`lines`); adjustment `delta ≠ 0`, `reason_code` allowed for the direction (§4.4: `damaged_in_warehouse`, `lost_in_warehouse` −; `found` +; `count_correction`, `other` ±), `note` 1–500 trimmed chars, serialized shapes of spec §4 (`serial` / `new_serials` / `new_imeis`); `page_size` per R19.
- **R16** Packing (§6.6, spec §4). Inputs normalized (R14). `AssignSerial`: serial already `assigned` to `ref_id` with that SKU → ok; lock allocation `(ref_id, sku_id)` `FOR UPDATE`; none or not `committed` → `NOT_COMMITTED`; assigned count for `(ref_id, sku_id)` ≥ quantity → `SERIAL_COUNT_EXCEEDED`; guarded `UPDATE serial_units SET status='assigned', ref_id=@ref, version+1 WHERE serial=@s AND sku_id=@sku AND status='in_stock'` → 0 rows → `SERIAL_NOT_AVAILABLE`. `UnassignSerial`: serial `in_stock` → ok; `assigned` to `ref_id` → `in_stock`, `ref_id` null; else `SERIAL_NOT_AVAILABLE`. `ReplaceSerial`: retry case (old `in_stock` and new `assigned` to `ref_id`) → ok; else old must be `assigned` to `ref_id` (`SERIAL_NOT_AVAILABLE`), new `in_stock` of the same SKU (`SERIAL_NOT_AVAILABLE`), swap in one transaction. Idempotent replies write nothing. Audit actions `inventory.serial-assigned`, `-unassigned`, `-replaced`; `target_type` `serial`, `target_id` the (new) serial, `details` `{refId}` (replace adds `oldSerial`); actor from `x-bloom-user-id` / `x-bloom-role`, owner item 2 when absent.
- **R17** Consumers: queue `inventory.<handler>`, handlers `order-paid` (event `order`), `release-reservation`, `receive-customer-return`, `receive-returned-to-sender`, `mark-lost-in-transit` (commands on `inventory.commands`), `shipment-shipped` (event `fulfillment`). `GetPayload<T>()` first; every check that can end in `PermanentFailureException` runs before the first write. Behaviour per §6 and spec §7; allocations in a status the message doesn't act on are skipped.
- **R18** `StockMonitor : BackgroundService` (worker): `PeriodicTimer(1 min, TimeProvider)`; `internal Task RunOnceAsync(CancellationToken)` sets `stuck_reservations` (allocations `reserved` with `created_at < now − 30 min`), `stock_available{sku}` (`on_hand − reserved` per row) and `backorders_waiting{sku}` (count of `backordered` allocations per SKU; SKUs with none are dropped). Reports only (IR-05).
- **R19** Paging: `page` 0 → 1; `page_size` 0 → 50, 1–100, else `VALIDATION_FAILED page_size`; past the end → empty. `ListStockLevels` by `sku_id`; `GetLedger` newest first by `(created_at, id)`, `from` inclusive, `to` exclusive.
- **R20** `GetAvailability`: one item per distinct id in request order; unknown → `available = 0` (§8); a malformed id → `VALIDATION_FAILED sku_ids`.
- **R21** `ReserveStock` / `CommitForReship` idempotency (§6.1, §6.9): compare the request as a multiset of `(sku_id, quantity)` with all allocations of the `ref_id`, any status; equal → ok, else `RESERVATION_CONFLICT`. A unique violation on the allocation key (a concurrent duplicate) → roll back and compare once more. `reserve_total{result}`: `ok` (incl. idempotent replay), `sold_out` (`SOLD_OUT` and `SKU_NOT_STOCKED`); `reserve_seconds` per call.
- **R22** Test isolation: one database per test host; consumers tested by invoking the handler in a scope and transaction like `ConsumerHost` (`Consume.Async`); only `WorkerTests` start worker hosts.

---

## File Structure

```
Bloom.slnx                        # + /src/Inventory/ (3 projects), /src/Fulfillment/Bloom.Fulfillment.Contracts
src/Inventory/Bloom.Inventory.Contracts/  .csproj (as Catalog.Contracts), InventoryMessages.cs, Messages.cs, Protos/inventory_internal.proto
src/Order/Bloom.Order.Contracts/          + OrderPaidV1.cs, OrderMessages.Paid
src/Fulfillment/Bloom.Fulfillment.Contracts/  .csproj (plain SDK), FulfillmentMessages.cs, Messages.cs
src/Inventory/Bloom.Inventory/
  Bloom.Inventory.csproj (Sdk.Web), Program.cs, InventoryApp.cs, InventoryOptions.cs, appsettings.json
  Protos/warehouse.proto                                       # package bloom.inventory.v1
  Data/ InventoryDbContext.cs, Entities.cs, DesignTimeFactory.cs, Db.cs, Migrations/
  Errors/InventoryErrors.cs  Context/Caller.cs  Observability/InventoryMetrics.cs
  Domain/ Identifiers.cs, Ledger.cs, StockLocks.cs, Allocator.cs, Audit.cs
  Services/ StockService.cs, PackingService.cs, WarehouseService.Receipts.cs, .Adjustments.cs, .Reads.cs   # partial WarehouseService
  Consumers/ OrderPaidConsumer.cs, ReleaseReservationConsumer.cs, ShipmentShippedConsumer.cs,
             CustomerReturnConsumer.cs, ReturnedToSenderConsumer.cs, LostInTransitConsumer.cs
  Worker/StockMonitor.cs
src/Inventory/Bloom.Inventory.Tests/
  Infrastructure/ Containers.cs, TestInventory.cs, FakeCatalog.cs, Api.cs, Seed.cs, Outbox.cs, Consume.cs, MetricCapture.cs, LogCapture.cs, Poll.cs
  ShellTests, SchemaTests, ReceiptTests, StockApiTests, OrderPaidTests, PackingTests, ReleaseTests,
  FulfillmentConsumerTests, AdjustmentTests, WarehouseReadTests, WorkerTests
```

Routes (spec §6), service `InventoryWarehouse`, prefix `/api/admin/inventory`: `ListStockLevels` `GET /stock` · `RecordGoodsReceipt` `POST /receipts` (`body: "*"`) · `AdjustStock` `POST /adjustments` (`body: "*"`) · `GetLedger` `GET /ledger/{sku_id}` · `GetSerial` `GET /serials/{serial}`. `InventoryStock`, `InventoryPacking`: no HTTP binding.

---

### Task 1: Projects, app shell, config, errors, caller, metrics, test infrastructure

**Files:**
- Create: `Bloom.Inventory.csproj` (as `Bloom.Catalog.csproj` minus AWSSDK.S3, Markdig, StackExchange.Redis; refs Bloom.Shared, Bloom.Identity.Contracts, Bloom.Catalog.Contracts; `InternalsVisibleTo Bloom.Inventory.Tests`), `Program.cs`, `InventoryApp.cs`, `InventoryOptions.cs`, `appsettings.json`, `Errors/InventoryErrors.cs`, `Context/Caller.cs`, `Observability/InventoryMetrics.cs`, `Data/InventoryDbContext.cs` (reliability tables only), `Data/DesignTimeFactory.cs` (R2)
- Create: `Bloom.Inventory.Tests.csproj` (as catalog's minus AWSSDK.S3, Testcontainers.Minio, Testcontainers.Redis), `Infrastructure/Containers.cs`, `TestInventory.cs`, `FakeCatalog.cs`, `Api.cs`, `MetricCapture.cs`, `LogCapture.cs`, `Poll.cs` (copy catalog's helpers)
- Modify: `Bloom.slnx`
- Test: `ShellTests.cs`

**Interfaces:**
- Produces:
  - `public static class InventoryApp { public static WebApplication Build(string[] args, Action<WebApplicationBuilder>? configure = null); }` — same shape as `CatalogApp.Build`: validates `InventoryOptions` (R4), `AddBloomTelemetry` (R5) + meter, `AddDbContext<InventoryDbContext>`, `AddBloom<InventoryDbContext>`, `AddGrpcClient<CatalogInternal.CatalogInternalClient>(o => o.Address = new Uri(options.Catalog))`. API mode: `AddGrpc().AddJsonTranscoding(o => { o.TypeRegistry = TypeRegistry.FromMessages(ErrorInfo.Descriptor, BadRequest.Descriptor); o.JsonSettings.WriteInt64sAsStrings = true; })`. Later tasks add services, consumers and the worker.
  - `public sealed class InventoryOptions` bound from `Inventory` (R4), `bool IsApi`, `void Validate()`.
  - `public static class InventoryErrors` — `const string` per reason, `RpcException Error(string reason, string message)`, `Validation(string field, string message)` (`metadata["field"]`), `SoldOut(Guid skuId)` (`metadata["skuId"]`), `ReceiptInvalid(IReadOnlyList<(string Field, string Description)> violations)` (R13), `CatalogUnavailable()`:

    | Reason | Status | Reason | Status |
    |---|---|---|---|
    | `SOLD_OUT` | FailedPrecondition | `RECEIPT_INVALID` | InvalidArgument |
    | `RESERVATION_CONFLICT` | InvalidArgument | `ADJUSTMENT_EXCEEDS_AVAILABLE` | FailedPrecondition |
    | `SKU_NOT_STOCKED` | FailedPrecondition | `SERIAL_ASSIGNED` | FailedPrecondition |
    | `SERIAL_NOT_AVAILABLE` | FailedPrecondition | `NOT_FOUND` | NotFound |
    | `SERIAL_COUNT_EXCEEDED` | FailedPrecondition | `VALIDATION_FAILED` | InvalidArgument |
    | `NOT_COMMITTED` | FailedPrecondition | `CATALOG_UNAVAILABLE` | Unavailable |

    `CONCURRENCY_CONFLICT`, `ROLE_NOT_ALLOWED`, `IDEMPOTENCY_KEY_*` come from `BloomErrors`.
  - `public sealed record Caller(Guid UserId, string UserType, string? Role)` — copy catalog's `From`, `TryFrom`, `RequireStaff`; role arrays `StockViewers`, `LedgerReaders`, `StockWriters`, `SerialReaders` (R7).
  - `public sealed class InventoryMetrics` (`MeterName = "Bloom.Inventory"`): `Reserve(string result, double seconds)`, `BackorderAllocated(double waitSeconds)`, `Adjustment(string reasonCode)`, `LedgerEntry(string entryType)`, `SetStuck(long count)`, `SetStock(IReadOnlyDictionary<Guid,int> available, IReadOnlyDictionary<Guid,int> backorders)` (observable gauges, label `sku`). Instruments: `reserve_total`, `reserve_seconds`, `backorder_wait_seconds`, `adjustments_total`, `ledger_entries_total`, `stuck_reservations`, `stock_available`, `backorders_waiting` (§10).
  - Tests: `Containers` (assembly fixture) — `postgres:17.6` (`max_connections=1000`), `rabbitmq:4.1.4-management`; at start `CREATE ROLE inventory_app LOGIN PASSWORD 'inventory-app-test'`; `Task<string> CreateDatabaseAsync()` (owner string), `static string AsAppRole(string ownerConnectionString)`, `RabbitMqUri`, `RabbitCtlAsync(...)`.
  - `FakeCatalog : IAsyncDisposable` (as identity's `FakeOrder`: Kestrel loopback HTTP/2) — `Uri Address`, `void AddSku(Guid skuId, Guid productId, bool serialized)`, `bool Down` (→ `Unavailable`), `int LockCalls(Guid skuId)`; unknown SKU → `NOT_FOUND`.
  - `TestInventory : IAsyncDisposable` — `static Task<TestInventory> StartAsync(Containers c, Action<TestInventoryOptions>? configure = null)`; options `Mode = "api"`, `string? ConnectionString` (owner string to reuse), `Config`. Fresh database migrated as owner; the app runs with `AsAppRole(...)`; own `FakeCatalog`. Exposes `HttpClient Http`, `InventoryStock.InventoryStockClient Stock` and `InventoryPacking.InventoryPackingClient Packing` (Task 4 adds them), `FakeCatalog Catalog`, `FakeTimeProvider Clock`, `LogCapture Logs`, `IServiceProvider Services`, `string ConnectionString` (owner), `InventoryDbContext NewDb()` (owner).
  - `Api` extensions on `TestInventory`: `SendAsync(HttpMethod, string path, object? body = null, TestCaller? caller = null, Guid? idempotencyKey = null)`, `OkJsonAsync()`, `ExpectErrorAsync(int httpStatus, string reason)`; `TestCaller.Staff(string role, Guid? id = null)`.

- [ ] **Step 1: Create the projects**, add them to `Bloom.slnx` under `/src/Inventory/`.
- [ ] **Step 2: Write the failing tests** in `ShellTests.cs`

```csharp
[Fact] public void Unknown_mode_fails_at_build()                                 // Mode=edge → throws
[Theory, InlineData("Inventory:Database"), InlineData("Inventory:RabbitMq"), InlineData("Inventory:Catalog")]
public void Missing_config_fails_at_build_naming_the_key(string key)             // both modes
[Theory, MemberData(nameof(ReasonTable))]
public void Each_reason_has_its_status(string reason, StatusCode code)           // the 12 rows; domain "bloom"
[Fact] public void Sold_out_carries_the_sku()                                    // metadata["skuId"]
[Fact] public void Receipt_invalid_lists_every_violation()                       // BadRequest with 2 field violations, in order
[Fact] public void Role_checks()   // warehouse passes StockWriters; support fails StockWriters, passes SerialReaders; catalog_manager passes StockViewers only
[Fact] public async Task Api_host_starts_as_the_app_role()                      // SELECT current_user through the app's DbContext == "inventory_app"
```

- [ ] **Step 3: Run** `dotnet test --project src/Inventory/Bloom.Inventory.Tests --filter-class '*ShellTests'` — Expected: build fails (types missing).
- [ ] **Step 4: Implement** the shell, options, errors, caller, metrics and test infrastructure.
- [ ] **Step 5: Run** the same command — PASS. `dotnet build -c Release` — 0 warnings.
- [ ] **Step 6: Commit** — `git add -A && git commit -m "feat(inventory): app shell, config, errors, caller and test infrastructure"`

---

### Task 2: Data model, initial migration and ledger grants

**Files:**
- Create: `Data/Entities.cs`, `Data/Db.cs`, `Data/Migrations/*` (`dotnet ef migrations add Initial --project src/Inventory/Bloom.Inventory`, then append the R8 SQL to `Up`)
- Modify: `Data/InventoryDbContext.cs`
- Test: `SchemaTests.cs`

**Interfaces:**
- Produces (snake_case columns of §4; `timestamptz` ↔ `DateTimeOffset`; UUIDv7 ids set by the app; constants classes `AllocationKinds` (`regular`, `preorder`, `reship`), `AllocationStatuses` (`reserved`, `backordered`, `committed`, `shipped`, `released`), `SerialStatuses` (`in_stock`, `assigned`, `shipped`, `returned`, `damaged`, `lost`), `EntryTypes` (the 12 of §4.4), `RefTypes` (`order`, `reship`, `receipt`, `adjustment`), `ReasonCodes` (the 5 of §4.4)):
  - `StockItem { Guid SkuId, ProductId; bool Serialized; int OnHand, Reserved, Committed, Returned, Damaged, Lost; DateTimeOffset UpdatedAt; }`
  - `StockAllocation { Guid RefId, SkuId, OrderId; string Kind, Status; int Quantity; DateTimeOffset? PaidAt; long Version; DateTimeOffset CreatedAt, UpdatedAt; }` — PK `(ref_id, sku_id)`, **no FK** to `stock_items` (pre-orders exist before the first receipt).
  - `SerialUnit { string Serial; string? Imei; Guid SkuId; string Status; Guid? RefId; Guid ReceiptId; long Version; DateTimeOffset UpdatedAt; }` — FK `sku_id` → `stock_items`.
  - `GoodsReceipt { Guid Id, StaffId; string Note; DateTimeOffset ReceivedAt; }`, `GoodsReceiptLine { Guid ReceiptId, SkuId; int Quantity; }` (FK → receipt)
  - `StockAdjustment { Guid Id, SkuId; string? Serial; int Delta; string ReasonCode, Note; Guid StaffId; DateTimeOffset CreatedAt; }`
  - `LedgerEntry { Guid Id, SkuId; string EntryType; int DOnHand, DReserved, DCommitted, DReturned, DDamaged, DLost; string RefType; Guid RefId; string Actor; DateTimeOffset CreatedAt; }` → table `stock_ledger`
  - DbSets `StockItems`, `StockAllocations`, `SerialUnits`, `GoodsReceipts`, `GoodsReceiptLines`, `StockAdjustments`, `StockLedger`; `AddBloomReliabilityTables()`.
  - Named constraints: `ck_stock_items_reserved_le_on_hand`, counters `>= 0` (one check per column), allocation `kind` / `status` / `quantity > 0`, serial `status`, receipt line `quantity > 0`, adjustment `delta <> 0` / `reason_code`, ledger `entry_type` / `ref_type`; `ux_serial_units_imei` (`WHERE imei IS NOT NULL`). Indexes: `ix_stock_allocations_fifo (sku_id, paid_at, order_id) WHERE status = 'backordered'`, `ix_stock_allocations_stuck (created_at) WHERE status = 'reserved'`, `ix_stock_allocations_order (order_id)`, `ix_serial_units_sku_status (sku_id, status)`, `ix_serial_units_ref (ref_id) WHERE ref_id IS NOT NULL`, `ix_stock_ledger_sku (sku_id, created_at, id)`.
  - `static class Db` — `IsUniqueViolation(DbUpdateException e, string constraint)` / `(PostgresException e, …)`, `IsCheckViolation(…)` (as catalog's).

- [ ] **Step 1: Write the failing tests** in `SchemaTests.cs`

```csharp
[Fact] public async Task Migration_applies_and_model_has_no_pending_changes()
[Fact] public async Task Reserved_above_on_hand_is_rejected()          // UPDATE stock_items SET reserved = on_hand + 1 → 23514 ck_stock_items_reserved_le_on_hand
[Fact] public async Task Negative_counters_are_rejected()              // each counter -1 → 23514
[Fact] public async Task Imei_is_unique_when_present()                 // two null IMEIs ok; same IMEI twice → ux_serial_units_imei
[Fact] public async Task Lld_indexes_exist()                           // pg_indexes: fifo and stuck partial indexes, (sku_id, status)
[Fact] public async Task I18_ledger_update_and_delete_are_refused_for_the_app_role()
// as inventory_app: INSERT a ledger row → ok, SELECT → ok; UPDATE → 42501; DELETE → 42501; TRUNCATE → 42501;
// UPDATE stock_items as inventory_app → ok (full DML elsewhere)
[Fact] public async Task Grants_follow_the_configured_role_name()      // migrate a fresh db with Options=-c inventory.app_role=inv_other (role created first) → inv_other has INSERT, not UPDATE, on stock_ledger
```

- [ ] **Step 2: Run** `--filter-class '*SchemaTests'` — FAIL.
- [ ] **Step 3: Implement** entities, configuration, the migration and the R8 grant SQL.
- [ ] **Step 4: Run** — PASS.
- [ ] **Step 5: Commit** — `git commit -m "feat(inventory): data model, migration and append-only ledger grants"`

---

### Task 3: Goods receipts, ledger and backorder allocator

**Files:**
- Create: `Protos/warehouse.proto` (service `InventoryWarehouse` with `RecordGoodsReceipt` only), `Services/WarehouseService.Receipts.cs`, `Domain/Identifiers.cs`, `Domain/Ledger.cs`, `Domain/StockLocks.cs`, `Domain/Allocator.cs`, `Domain/Audit.cs`, `src/Inventory/Bloom.Inventory.Contracts/` (csproj as `Bloom.Catalog.Contracts.csproj` but without the `Protobuf` item, which Task 4 adds with the proto; `InventoryMessages.cs`, `Messages.cs`)
- Modify: `InventoryApp.cs` (scoped `Ledger`, `StockLocks`, `Allocator`, `Audit`; map `WarehouseService`), `Bloom.slnx`, `Bloom.Inventory.csproj` (ref Contracts)
- Test: `ReceiptTests.cs`, `Infrastructure/Seed.cs`, `Infrastructure/Outbox.cs`

**Interfaces:**
- Produces:
  - Proto: `RecordGoodsReceiptRequest { string note = 1; repeated ReceiptLine lines = 2; }`, `ReceiptLine { string sku_id; int32 quantity; repeated string serials; repeated string imeis; }` → `RecordGoodsReceiptResponse { string receipt_id; }`. Requires `Idempotency-Key` (spec §4).
  - `InventoryMessages.StockAllocated = "inventory.stock-allocated.v1"`, `ReservationReleased = "inventory.reservation-released.v1"`, `ReleaseReservation = "inventory.release-reservation.v1"`; records `StockAllocatedV1(Guid OrderId)`, `ReservationReleasedV1(Guid OrderId)`, `ReleaseReservationV1(Guid OrderId)`.
  - `Identifiers` (R14). `record struct Deltas(int OnHand = 0, int Reserved = 0, int Committed = 0, int Returned = 0, int Damaged = 0, int Lost = 0)`.
  - `Ledger` (scoped): `void Add(Guid skuId, string entryType, Deltas d, string refType, Guid refId)`; `string Actor { get; set; }` (default `inventory`, R11).
  - `StockLocks` (scoped): `Task<Dictionary<Guid, StockItem>> LockAsync(IEnumerable<Guid> skuIds, CancellationToken ct)` — R9 statement, returns the rows that exist (untracked).
  - `Allocator` (scoped): `Task RunAsync(Guid skuId, CancellationToken ct)` (R12).
  - `Audit` (scoped): `void Record(Guid actorId, string actorRole, string action, string targetType, string targetId, object? details = null)` (as catalog's, service `inventory`).
  - Tests: `Seed.Sku(TestInventory t, bool serialized = false) → Guid` (registers it in `FakeCatalog`), `Seed.ReceiveAsync(t, Guid skuId, int quantity, string[]? serials = null) → Guid receiptId` (through the API, staff `warehouse`), `Seed.BackorderAsync(t, Guid skuId, int quantity, DateTimeOffset paidAt) → Guid orderId` (inserts a `preorder` / `backordered` allocation directly), `Seed.ItemAsync(t, Guid skuId) → StockItem?`, `Seed.LedgerAsync(t, Guid skuId)`; `Outbox.AuditAsync(t)`, `Outbox.PayloadsAsync<T>(t, string messageType)`.

- [ ] **Step 1: Write the failing tests** in `ReceiptTests.cs` (staff `warehouse`, fresh key per call unless stated)

```csharp
[Fact] public async Task Receipt_adds_stock_ledger_and_audit()
// 2 lines (one serialized with 2 serials + IMEIs) → 200 receiptId; stock_items on_hand 5 / 2, product_id + serialized from catalog;
// serial_units in_stock with receipt_id and IMEI; ledger "received" per SKU with d_on_hand, ref receipt, actor = staff id; ledger_entries_total{received} 2;
// audit inventory.goods-received target receipt
[Fact] public async Task I04_receipt_serves_oldest_backorders_first()
// backorders [2, 2, 3] oldest first; receive 5 → first two committed, on_hand 1, committed 4, third still backordered;
// ledger backorder_allocated × 2 ref order; StockAllocatedV1 for the first two orders only; backorder_wait_seconds count 2
[Fact] public async Task I05_receipt_smaller_than_oldest_backorder_allocates_nothing()   // backorders [2, 1]; receive 1 → none committed, on_hand 1
[Fact] public async Task I07_one_duplicate_serial_among_100_rejects_the_whole_receipt()
// 100 serials, one repeated → 400 RECEIPT_INVALID; BadRequest lists lines[0].serials[j] of the repeat; no rows anywhere
[Fact] public async Task Every_bad_line_is_listed()
// quantity 0, existing serial, bad IMEI (Luhn), IMEI count ≠ serial count, duplicate sku line → one violation each, request order
[Fact] public async Task Serial_count_and_non_serialized_checks_run_after_the_lock()
// serialized SKU with 2 serials for quantity 3, non-serialized SKU with a serial → both violations; catalog lock was called (accepted, spec §4)
[Fact] public async Task Format_errors_skip_the_catalog_call()        // bad serial format on a new SKU → RECEIPT_INVALID; LockCalls == 0
[Fact] public async Task I08_catalog_lock_called_on_first_receipt_only()  // receive twice → LockCalls(sku) == 1
[Fact] public async Task Unknown_sku_is_listed()                       // catalog NOT_FOUND → violation lines[0].sku_id
[Fact] public async Task Catalog_down_on_first_receipt_writes_nothing()   // Down → 503 CATALOG_UNAVAILABLE; no rows; same key later with catalog up → 200
[Fact] public async Task Serials_are_trimmed_and_upper_cased()          // " abc123 " stored as "ABC123"; "abc1" + "ABC1" in one receipt → duplicate
[Fact] public async Task Same_key_replays_the_receipt()                 // same key, same body → same receiptId, on_hand counted once; different body → IDEMPOTENCY_KEY_REUSED; no key → IDEMPOTENCY_KEY_REQUIRED
[Fact] public async Task Concurrent_receipts_for_one_sku_allocate_serially()   // 10 parallel receipts of 1, backorders [1×10] → all 10 committed, FIFO by paid_at
[Theory, InlineData("support"), InlineData("catalog_manager")]
public async Task Only_warehouse_and_admin_record_receipts(string role)  // 403 ROLE_NOT_ALLOWED, no key claimed
```

- [ ] **Step 2: Run** `--filter-class '*ReceiptTests'` — FAIL.
- [ ] **Step 3: Implement** (R9–R14).
- [ ] **Step 4: Run** — PASS.
- [ ] **Step 5: Commit** — `git commit -m "feat(inventory): goods receipts, stock ledger and FIFO backorder allocator"`

---

### Task 4: Stock API (ReserveStock, GetAvailability, CommitForReship)

**Files:**
- Create: `src/Inventory/Bloom.Inventory.Contracts/Protos/inventory_internal.proto`, `Services/StockService.cs`
- Modify: `Bloom.Inventory.Contracts.csproj` (+`<Protobuf Include="Protos\inventory_internal.proto" GrpcServices="Both" />`), `InventoryApp.cs` (map `StockService`), `TestInventory.cs` (+`Stock`, `Packing` clients)
- Test: `StockApiTests.cs`

**Interfaces:**
- Produces (package `bloom.inventory.v1`, `csharp_namespace = "Bloom.Inventory.Contracts"`):
  - `service InventoryStock { rpc ReserveStock(ReserveStockRequest) returns (google.protobuf.Empty); rpc GetAvailability(GetAvailabilityRequest) returns (GetAvailabilityResponse); rpc CommitForReship(CommitForReshipRequest) returns (google.protobuf.Empty); }`
  - `service InventoryPacking { rpc AssignSerial(AssignSerialRequest) returns (google.protobuf.Empty); rpc UnassignSerial(UnassignSerialRequest) returns (google.protobuf.Empty); rpc ReplaceSerial(ReplaceSerialRequest) returns (google.protobuf.Empty); }` (implemented in Task 6)
  - `StockLine { string sku_id; int32 quantity; }`; `ReserveStockRequest { string order_id; repeated StockLine lines; }`; `GetAvailabilityRequest { repeated string sku_ids; }` → `GetAvailabilityResponse { repeated SkuAvailability items; }`, `SkuAvailability { string sku_id; int32 available; }`; `CommitForReshipRequest { string reship_id; string order_id; repeated StockLine lines; }`; `AssignSerialRequest { string ref_id; string sku_id; string serial; }`; `UnassignSerialRequest { string ref_id; string serial; }`; `ReplaceSerialRequest { string ref_id; string old_serial; string new_serial; }`.
  - `ReserveStock` per §6.1 + R21: allocations `regular` / `reserved`, ledger `reserved` (`d_reserved`). `CommitForReship` per §6.9 + R21: guarded `on_hand -= q, committed += q`, allocations `reship` / `committed` with `order_id`, ledger `committed` ref `reship`. Both: no row → `SKU_NOT_STOCKED`, short → `SoldOut(skuId)`, all-or-nothing.

- [ ] **Step 1: Write the failing tests** in `StockApiTests.cs`

```csharp
[Fact] public async Task I01_500_concurrent_reserves_of_one_with_10_available()
// exactly 10 ok, 490 SOLD_OUT (metadata skuId); reserved 10, on_hand 10; reserve_total ok 10 / sold_out 490
[Fact] public async Task I02_retried_reserve_is_ok_and_reserved_once()      // same order + lines twice → ok; reserved 2 not 4; one ledger row per SKU
[Fact] public async Task Same_order_with_other_lines_is_RESERVATION_CONFLICT()
[Fact] public async Task Multi_sku_reserve_is_all_or_nothing()               // A ok, B short → SOLD_OUT for B; A unchanged
[Fact] public async Task Never_received_sku_is_SKU_NOT_STOCKED()
[Fact] public async Task Invalid_reserve_requests_are_VALIDATION_FAILED()    // empty lines, quantity 0, duplicate sku, bad order_id → field set
[Fact] public async Task I06_receipt_during_100_concurrent_reserves_serves_backorders_first()
// on_hand 0, backorders [3]; receipt of 10 ∥ 100 reserves of 1 → backorder committed 3; ok reserves ≤ 7 and == reserved; on_hand − reserved ≥ 0
[Fact] public async Task Opposite_lock_orders_never_deadlock()
// Review Focus 1: 50 rounds of reserve [B, A] ∥ reserve [A, B] ∥ receipt [B, A] → no call fails with anything but SOLD_OUT; no 40P01 in logs
[Fact] public async Task GetAvailability_returns_available_per_sku()          // [A, unknown, A] → [A: on_hand − reserved, unknown: 0]; malformed → VALIDATION_FAILED sku_ids
[Fact] public async Task I16_reship_without_stock_is_SOLD_OUT()
[Fact] public async Task Reship_commits_stock_idempotently()                 // ok → on_hand −q, committed +q, allocation reship/committed with order_id, ledger ref reship; retry → ok once; other lines → RESERVATION_CONFLICT; never-received SKU → SKU_NOT_STOCKED
[Fact] public async Task Internal_methods_have_no_http_route()               // POST /api/admin/inventory/reserve → 404
```

- [ ] **Step 2: Run** `--filter-class '*StockApiTests'` — FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** — PASS.
- [ ] **Step 5: Commit** — `git commit -m "feat(inventory): reserve, availability and reship commit"`

---

### Task 5: `order.paid.v1` consumer

**Files:**
- Create: `src/Order/Bloom.Order.Contracts/OrderPaidV1.cs`, `Consumers/OrderPaidConsumer.cs`, Tests `Infrastructure/Consume.cs` (as catalog's)
- Modify: `OrderMessages.cs` (+`Paid = "order.paid.v1"`), `Bloom.Inventory.csproj` (ref Order.Contracts), `InventoryApp.cs` (`AddConsumer<OrderPaidConsumer>("order-paid", …)`)
- Test: `OrderPaidTests.cs`

**Interfaces:**
- Produces:
  - `public sealed record OrderPaidV1(Guid OrderId, string OrderNumber, Guid AccountId, Guid? LaunchId, DateTimeOffset PaidAt, long TotalCents, IReadOnlyList<OrderPaidLineV1> Lines)`; `OrderPaidLineV1(Guid SkuId, int Quantity)` — doc comment: lines are product lines only (owner item 1).
  - `OrderPaidConsumer` (`Bindings = [ConsumerBinding.Event("order", OrderMessages.Paid)]`). Regular (§6.2): lock SKUs of the order's `reserved` allocations; each → guarded `reserved −= q, on_hand −= q, committed += q`, status `committed`, ledger `committed`; others no-op. Pre-order: lines grouped by `skuId` (quantities summed); lock; insert `preorder` / `backordered` allocations with `paid_at` (`ON CONFLICT DO NOTHING`); save; allocator per SKU ascending.

- [ ] **Step 1: Write the failing tests** in `OrderPaidTests.cs`

```csharp
[Fact] public async Task I03_regular_paid_commits_the_reservation()          // reserve 2 → paid → reserved −2, on_hand −2, committed +2; ledger committed ref order
[Fact] public async Task I17_duplicate_paid_is_applied_once()                // same payload twice (new message ids) → counters moved once; pre-order: one backorder
[Fact] public async Task Paid_for_released_allocation_changes_nothing()
[Fact] public async Task Preorder_without_stock_waits_as_backorder()          // no stock_items row → backordered with paid_at; no counters; no stock-allocated
[Fact] public async Task Preorder_paid_with_stock_on_hand_commits_now()       // §8: on_hand 5 → committed; StockAllocatedV1(orderId) once
[Fact] public async Task Stock_allocated_waits_for_every_preorder_line()      // lines A (stock) + B (none) → A committed, no message; receive B → message once
[Fact] public async Task Preorder_queues_behind_older_backorders()           // older backorder 3, on_hand 2, new paid 1 → new stays backordered (strict FIFO)
[Fact] public async Task Invalid_payload_is_permanent_failure()               // missing orderId → PermanentFailureException, nothing written
```

- [ ] **Step 2: Run** `--filter-class '*OrderPaidTests'` — FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** — PASS.
- [ ] **Step 5: Commit** — `git commit -m "feat(inventory): order paid consumer for regular and pre-orders"`

---

### Task 6: Packing API (AssignSerial, UnassignSerial, ReplaceSerial)

**Files:**
- Create: `Services/PackingService.cs`
- Modify: `InventoryApp.cs` (map `PackingService`)
- Test: `PackingTests.cs`

**Interfaces:**
- Consumes: Task 4 proto; `Audit`; `Caller.TryFrom`.
- Produces: R16 behaviour; serial changes write no ledger rows (counters don't change).

- [ ] **Step 1: Write the failing tests** in `PackingTests.cs` (seed: serialized SKU received with 3 serials, reserve 2, regular paid → committed)

```csharp
[Fact] public async Task Assign_marks_the_serial_assigned_and_audits()      // status assigned, ref_id; audit inventory.serial-assigned, actor from headers
[Fact] public async Task Assign_retry_is_ok()                                // same call twice → ok, one audit
[Fact] public async Task I10_assign_serial_of_another_sku_is_SERIAL_NOT_AVAILABLE()
[Fact] public async Task I11_assign_more_serials_than_quantity_is_SERIAL_COUNT_EXCEEDED()
[Fact] public async Task Concurrent_assigns_never_exceed_quantity()          // 3 parallel assigns for quantity 2 → 2 ok, 1 SERIAL_COUNT_EXCEEDED
[Fact] public async Task Assign_without_committed_allocation_is_NOT_COMMITTED()   // reserved allocation, and no allocation at all
[Fact] public async Task Unassign_and_replace()                              // unassign → in_stock, ref null; retry ok; replace old→new → old in_stock, new assigned; retry ok; audits
[Fact] public async Task Unassign_of_serial_assigned_elsewhere_is_SERIAL_NOT_AVAILABLE()
[Fact] public async Task Scanned_serial_is_normalized()                      // " abc123 " finds ABC123; "ab-1" → VALIDATION_FAILED serial
[Fact] public async Task Missing_staff_headers_audit_with_empty_actor()      // owner item 2: ActorId Guid.Empty, ActorRole ""
```

- [ ] **Step 2: Run** `--filter-class '*PackingTests'` — FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** — PASS.
- [ ] **Step 5: Commit** — `git commit -m "feat(inventory): serial assignment for packing"`

---

### Task 7: Release command consumer

**Files:**
- Create: `Consumers/ReleaseReservationConsumer.cs`
- Modify: `InventoryApp.cs` (`AddConsumer<ReleaseReservationConsumer>("release-reservation", ConsumerBinding.Command(InventoryMessages.ReleaseReservation))`)
- Test: `ReleaseTests.cs`

**Interfaces:**
- Produces: §6.5 over every allocation with `order_id` = `orderId` (reships included), SKUs locked ascending; `committed` → `committed −= q, on_hand += q`, assigned serials of the `ref_id` + SKU → `in_stock`, ledger `commitment_released`, allocator for the SKU; `reserved` → `reserved −= q`, ledger `reservation_released`; `backordered` → no counters; all three → `released`; `shipped` / `released` untouched. Always writes `ReservationReleasedV1(orderId)`.

- [ ] **Step 1: Write the failing tests** in `ReleaseTests.cs`

```csharp
[Fact] public async Task Release_of_reserved_frees_available()               // reserved −q; ledger reservation_released; released-message
[Fact] public async Task Release_of_backordered_touches_no_counters()        // §8: status released; counters unchanged; message
[Fact] public async Task I09_release_of_committed_with_assigned_serials()
// paid + 1 serial assigned; older backorder waiting for 1 → on_hand +q then allocator commits the backorder; serial in_stock, ref null;
// ledger commitment_released then backorder_allocated; StockAllocatedV1 for the backorder's order
[Fact] public async Task Release_covers_the_orders_reships()                 // reship committed for the order → released too, ledger ref reship
[Fact] public async Task Shipped_and_released_allocations_are_untouched()
[Fact] public async Task Release_of_unknown_order_still_publishes_released() // Review Focus 4
[Fact] public async Task Repeated_release_changes_nothing_more()             // second time: counters same, second message written
[Fact] public async Task Release_and_receipt_on_shared_skus_never_deadlock() // 30 rounds: release of [A, B] order ∥ receipt [B, A] → all succeed
```

- [ ] **Step 2: Run** `--filter-class '*ReleaseTests'` — FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** — PASS.
- [ ] **Step 5: Commit** — `git commit -m "feat(inventory): release reservation command"`

---

### Task 8: Fulfillment consumers (shipped, customer return, returned to sender, lost in transit)

**Files:**
- Create: `src/Fulfillment/Bloom.Fulfillment.Contracts/Bloom.Fulfillment.Contracts.csproj` (plain SDK, as `Bloom.Launch.Contracts`), `FulfillmentMessages.cs`, `Messages.cs`; `Consumers/ShipmentShippedConsumer.cs`, `CustomerReturnConsumer.cs`, `ReturnedToSenderConsumer.cs`, `LostInTransitConsumer.cs`
- Modify: `Bloom.slnx` (`/src/Fulfillment/`), `Bloom.Inventory.csproj` (ref), `InventoryApp.cs` (four `AddConsumer`, R17)
- Test: `FulfillmentConsumerTests.cs`

**Interfaces:**
- Produces:
  - `FulfillmentMessages.ShipmentShipped = "fulfillment.shipment-shipped.v1"`, `ReceiveCustomerReturn = "inventory.receive-customer-return.v1"`, `ReceiveReturnedToSender = "inventory.receive-returned-to-sender.v1"`, `MarkLostInTransit = "inventory.mark-lost-in-transit.v1"`.
  - `ShipmentShippedV1(Guid OrderId, Guid RefId, IReadOnlyList<ShippedLineV1> Lines, string TrackingNumber)`, `ShippedLineV1(Guid SkuId, int Quantity, IReadOnlyList<string> Serials)` (05-fulfillment §5); `ReceiveCustomerReturnV1(Guid OrderId, IReadOnlyList<ReturnedUnitV1> Units)`, `ReturnedUnitV1(Guid SkuId, string? Serial, int Quantity)`; `ReceiveReturnedToSenderV1(Guid RefId, IReadOnlyList<ReturnedToSenderUnitV1> Units)`, `ReturnedToSenderUnitV1(Guid SkuId, string? Serial, int Quantity, string Outcome /* sealed | failed */)`; `MarkLostInTransitV1(Guid RefId)` (02-inventory §5.3).
  - Shipped (spec §7): `committed` allocations of `refId`; any serialized one with assigned serials < quantity → permanent failure before writes; else `committed −= q`, status `shipped`, assigned → `shipped`, ledger `shipped`. Message `serials[]` unused.
  - Customer return: serialized → serial must exist, be of `skuId`, `shipped` (else permanent) → `returned`; `returned += q` (1 per serial); ledger `customer_returned` ref order.
  - Returned to sender: serialized → serial `shipped` with `ref_id = refId` and that SKU, else permanent; `sealed` → serial `in_stock`, `ref_id` null, `on_hand += q`, ledger `rts_restocked`, allocator; `failed` → serial `damaged`, `damaged += q`, ledger `rts_damaged`; unknown outcome → permanent.
  - Lost in transit: no `shipped` allocation for `refId` → permanent; per allocation: serialized → serials `shipped` for `refId` + SKU → `lost` (none → permanent), `lost += count`; non-serialized → `lost += q`; ledger `lost_in_transit`. Allocations stay `shipped`.

- [ ] **Step 1: Write the failing tests** in `FulfillmentConsumerTests.cs` (seed: serialized SKU, paid order, serials assigned via `Packing`)

```csharp
[Fact] public async Task Shipment_moves_committed_to_shipped()               // committed −q; allocation shipped; serials shipped; ledger shipped
[Fact] public async Task I12_shipment_with_missing_serials_is_permanent_and_changes_nothing()   // quantity 2, 1 assigned → PermanentFailureException; all rows unchanged
[Fact] public async Task Shipment_for_unknown_ref_changes_nothing()          // Review Focus 4
[Fact] public async Task Non_serialized_shipment_needs_no_serials()
[Fact] public async Task I13_customer_return_is_held_not_sellable()          // serial returned, returned +1, on_hand unchanged, GetAvailability unchanged
[Fact] public async Task Customer_return_of_unshipped_serial_is_permanent()
[Fact] public async Task I14_returned_to_sender_sealed_restocks_and_runs_allocator()   // serial in_stock, on_hand +1, waiting backorder committed
[Fact] public async Task Returned_to_sender_failed_is_damaged()              // serial damaged, damaged +1, ledger rts_damaged
[Fact] public async Task Lost_in_transit_marks_serials_and_counts()          // serials lost, lost +2; non-serialized line lost +q
[Fact] public async Task Lost_in_transit_for_unknown_ref_is_permanent()      // Review Focus 4
```

- [ ] **Step 2: Run** `--filter-class '*FulfillmentConsumerTests'` — FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** — PASS.
- [ ] **Step 5: Commit** — `git commit -m "feat(inventory): shipment, return and delivery-failure consumers"`

---

### Task 9: Stock adjustments

**Files:**
- Create: `Services/WarehouseService.Adjustments.cs`
- Modify: `Protos/warehouse.proto` (+`AdjustStock`)
- Test: `AdjustmentTests.cs`

**Interfaces:**
- Produces:
  - `AdjustStockRequest { string sku_id; int32 delta; string reason_code; string note; string serial; repeated string new_serials; repeated string new_imeis; }` → `AdjustStockResponse { string adjustment_id; }`. Requires `Idempotency-Key`.
  - §6.8, spec §4, owner item 3, R15. Inside the guard: lock; no row → `SKU_NOT_STOCKED`; serialized serial checks first (`assigned` → `SERIAL_ASSIGNED`; other / unknown / other SKU → `SERIAL_NOT_AVAILABLE`; increase via `new_serials`: format, not existing, IMEI rules as a receipt → `VALIDATION_FAILED new_serials` / `new_imeis`); decrease guarded `on_hand - reserved >= n` else `ADJUSTMENT_EXCEEDS_AVAILABLE`; `damaged` / `lost += n` for the two codes; increase `on_hand += n` (lost serial: `lost −= 1`), then the allocator. Insert `stock_adjustments`, ledger `adjusted` ref adjustment, audit `inventory.stock-adjusted` (`target_type` `adjustment`, `details` `{skuId, delta, reasonCode, serial}`), `adjustments_total{reason_code}`.

- [ ] **Step 1: Write the failing tests** in `AdjustmentTests.cs`

```csharp
[Fact] public async Task I15_decrease_larger_than_available_is_ADJUSTMENT_EXCEEDS_AVAILABLE()   // on_hand 3 reserved 2, −2 → refused; nothing written
[Fact] public async Task Damaged_unit_promised_to_an_order_is_refused()     // §8: on_hand 1 reserved 1, damaged −1 → ADJUSTMENT_EXCEEDS_AVAILABLE
[Fact] public async Task Damaged_decrease_moves_units_to_damaged()          // on_hand −2, damaged +2; ledger adjusted; audit; adjustments_total
[Fact] public async Task Increase_runs_the_allocator()                     // found +2 with backorder 2 → committed
[Fact] public async Task Reason_must_fit_the_direction()                   // found −1, damaged_in_warehouse +1 → VALIDATION_FAILED reason_code; note empty / 501 chars → note
[Fact] public async Task Serialized_decrease_names_one_in_stock_serial()   // lost −1 serial → serial lost, lost +1; assigned → SERIAL_ASSIGNED; shipped → SERIAL_NOT_AVAILABLE; delta −2 → VALIDATION_FAILED
[Fact] public async Task Serialized_count_correction_decrease_is_refused() // owner item 3
[Fact] public async Task Serialized_increase_with_new_serial_or_lost_serial()   // new_serials ["n1"] + IMEI → in_stock, receipt_id = adjustment id; lost serial → in_stock, lost −1
[Fact] public async Task Same_key_replays_the_adjustment()                  // Review Focus 3: same adjustmentId, counted once; refused ADJUSTMENT_EXCEEDS_AVAILABLE replays too
[Fact] public async Task Ledger_deltas_sum_to_counters()
// Review Focus 5: receipt, reserve, paid, pre-order backorder + receipt, release, ship, customer return, RTS sealed, lost in transit, adjust →
// per SKU Σ d_* == stock_items counters
```

- [ ] **Step 2: Run** `--filter-class '*AdjustmentTests'` — FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** — PASS.
- [ ] **Step 5: Commit** — `git commit -m "feat(inventory): stock adjustments"`

---

### Task 10: Warehouse reads (ListStockLevels, GetLedger, GetSerial)

**Files:**
- Create: `Services/WarehouseService.Reads.cs`
- Modify: `Protos/warehouse.proto` (+3 methods)
- Test: `WarehouseReadTests.cs`

**Interfaces:**
- Produces:
  - `ListStockLevelsRequest { optional string product_id; repeated string sku_ids; optional int32 available_at_most; optional bool has_backorders; int32 page; int32 page_size; }` → `ListStockLevelsResponse { repeated StockLevel items; }`; `StockLevel { sku_id, product_id, serialized, on_hand, reserved, available, committed, returned, damaged, lost, backordered, google.protobuf.Timestamp updated_at }` (spec §4; `available_at_most < 0` → `VALIDATION_FAILED`).
  - `GetLedgerRequest { string sku_id; google.protobuf.Timestamp from; google.protobuf.Timestamp to; int32 page; int32 page_size; }` → `GetLedgerResponse { repeated LedgerEntry entries; }` with every `stock_ledger` column (R19; unknown SKU → empty).
  - `GetSerialRequest { string serial }` → `SerialUnitView { serial, imei, sku_id, status, ref_id, receipt_id, updated_at }`; unknown → `NOT_FOUND`.

- [ ] **Step 1: Write the failing tests** in `WarehouseReadTests.cs`

```csharp
[Fact] public async Task Stock_levels_filter_and_page()
// 3 SKUs of 2 products; filters product_id, sku_ids, available_at_most=0 (sold out), has_backorders → AND; page_size 2 → 2 + 1 by sku_id; backordered = Σ quantities
[Fact] public async Task Never_received_skus_are_not_listed()
[Fact] public async Task Ledger_is_newest_first_in_range()                  // from inclusive, to exclusive (FakeTimeProvider steps); paging
[Fact] public async Task Get_serial_shows_status_and_ref()                  // " abc123 " → ABC123 assigned + refId; unknown → 404 NOT_FOUND
[Theory, MemberData(nameof(RoleMatrix))]
public async Task Read_roles(string method, string role, bool allowed)     // R7 matrix, e.g. GetLedger + support → 403 ROLE_NOT_ALLOWED
[Fact] public async Task Page_size_out_of_range_is_VALIDATION_FAILED()      // 101 → page_size
```

- [ ] **Step 2: Run** `--filter-class '*WarehouseReadTests'` — FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** — PASS.
- [ ] **Step 5: Commit** — `git commit -m "feat(inventory): stock levels, ledger and serial lookup"`

---

### Task 11: Worker mode, stock monitor, DLQ admin, full verification

**Files:**
- Create: `Worker/StockMonitor.cs`
- Modify: `InventoryApp.cs` (worker: `AddWorker()` + `StockMonitor`; API: `MapBloomDeadLetterAdmin()`)
- Test: `WorkerTests.cs`

**Interfaces:**
- Produces: `StockMonitor` (R18). Consumes everything above; RabbitMQ.Client in tests.

- [ ] **Step 1: Write the failing tests** in `WorkerTests.cs` (API host + worker host on one database)

```csharp
[Fact] public async Task Six_queues_are_bound()
// rabbitmqctl list_bindings: inventory.order-paid ← order.events, inventory.shipment-shipped ← fulfillment.events,
// release-reservation / receive-customer-return / receive-returned-to-sender / mark-lost-in-transit ← inventory.commands; keys = message types
[Fact] public async Task I17_published_paid_twice_is_applied_once()          // same envelope twice to order.events → committed once; one inbox row
[Fact] public async Task I12_shipment_missing_serials_lands_in_the_dlq()     // inventory.shipment-shipped.dlq has it; rows unchanged
[Fact] public async Task Release_command_reaches_order()                     // command to inventory.commands → a test queue on inventory.events gets reservation-released.v1
[Fact] public async Task Audit_events_reach_inventory_events()               // receipt via API → identity.staff-action-recorded.v1 on inventory.events; outbox row sent
[Fact] public async Task Stock_monitor_reports_stuck_reservations_and_levels()
// reserve; clock +29 min → RunOnceAsync → stuck 0; +31 min → 1; stock_available{sku} and backorders_waiting{sku} match; nothing released (IR-05)
[Fact] public async Task Dlq_admin_is_hosted()                               // GET /api/admin/dlq/inventory/queues, staff x-bloom-role "Admin" (catalog owner item 6) → six .dlq queues
[Fact] public async Task Worker_runs_the_stock_monitor()                     // hosted services: worker has StockMonitor, API doesn't
```

- [ ] **Step 2: Run** `--filter-class '*WorkerTests'` — FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** — PASS. Then the whole suite: `dotnet build -c Release` (0 warnings) and `dotnet test` (all projects) — PASS. ID coverage: `grep -rhoE 'I(0[1-9]|1[0-8])_' src/Inventory/Bloom.Inventory.Tests | sort -u` lists I01_ … I18_. `docker ps -a` shows no leftover test containers.
- [ ] **Step 5: Commit** — `git commit -m "feat(inventory): worker mode, stock monitor and DLQ admin"`

---

## Proposed doc edits (not applied; in addition to spec §9)

8. 03-order §5.3 `order.paid.v1`: `lines` are product lines only (owner item 1).
9. 02-inventory §6.8: serialized decreases only with `damaged_in_warehouse` / `lost_in_warehouse` (owner item 3).
10. 02-inventory §5.4: `CATALOG_UNAVAILABLE` (`UNAVAILABLE`) for §5.2's failure (owner item 4).
11. 02-inventory §7 "Delayed paid after a release": not impossible across two queues (owner item 5, needs an ADR if fixed).
