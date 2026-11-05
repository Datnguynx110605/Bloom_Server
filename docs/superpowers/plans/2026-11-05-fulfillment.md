# Fulfillment Service Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the fulfillment service of `doc/low-level-design/05-fulfillment.md` (API and worker as one app), with a real Shippo HTTP client, fulfillment's internal proto and message payloads, `OrderReadyToFulfillV1`, and a new `Bloom.Payment.Contracts`.

**Architecture:** One ASP.NET Core app `Bloom.Fulfillment`; `Fulfillment:Mode` = `api` | `worker` (HLD-ADR-014). API mode hosts three transcoded gRPC services, one per LLD §3 API component (`FulfillmentWarehouse`, `FulfillmentSupport`, `FulfillmentCustomer`), the internal `FulfillmentInternal` (plain gRPC, proto in Contracts), the Shippo webhook and the shared DLQ admin. Worker mode runs the `Bloom.Shared` outbox relay, consumer host (four consumers), retention purger, the label worker, the sweeps and a gauge monitor. PostgreSQL through EF Core (`AddBloom<FulfillmentDbContext>`); every status change is a guarded single-statement update (00-reliability §7.5); refunds of one order are serialized by a row lock on `order_accounts`. Shippo sits behind `IShippo` (HLD-ADR-018); inventory is called through `Bloom.Inventory.Contracts` clients.

**Tech Stack:** .NET 10, ASP.NET Core gRPC + JSON transcoding, EF Core 10 + Npgsql, `HttpClient` + `System.Text.Json`, Bloom.Shared, xUnit v3, Testcontainers (PostgreSQL, RabbitMQ), Microsoft.Extensions.TimeProvider.Testing.

**Spec:** `docs/superpowers/specs/2026-11-05-fulfillment-design.md` (approved; cited "spec §x"). Design source: `doc/low-level-design/05-fulfillment.md` (cited "§x"). Where the spec is silent, 05-fulfillment decides; where both are silent, the owner items and rulings below decide.

## Owner items (plan approved 2026-11-05)

Items 1–5 approved as recommended with the plan.

1. **Last unit's share after an earlier partial refund.** §6.4.1 step 1 gives the last unit `paid_cents − refunded_cents`. If an earlier unit of the line got a *partial* refund, that's more than the unit's share (1001-cent line, unit 1 partial 300 → unit 2 "full" = 701, not 501), against RET-R06. **Recommendation:** the last unit's share is `paid_cents − (quantity − 1) × floor(paid_cents / quantity)` (same rule for tax). Whenever earlier units were full, this equals the LLD formula (F-09: 500 + 501), and a full return of the whole line still refunds it exactly (FR-02). Proposed doc edit 10. (Task 9)
2. **Plan and product units in one request.** Spec §4 only bans a plan line together with its *parent* device line. A plan with some *other* product line would be neither inspected (no item comes back) nor plan-only. **Recommendation:** a request is either plan-only or product-only; any mix → `VALIDATION_FAILED units`. This includes the spec's case. Proposed doc edit 11 (replaces spec §9 item 8). (Task 9)
3. **Accepted unit inspected as `wrong_item`.** RET-R05 allows any decision for any inspection result. Inventory's customer-return consumer dead-letters a serial it didn't ship (inventory plan R17). **Recommendation:** accepted serialized units whose inspection is `wrong_item` are left out of `inventory.receive-customer-return.v1`, with a warning log (the item isn't Bloom stock). The refund is still paid. Alternative: send them and let the message dead-letter. (Task 9)
4. **Nothing to refund.** A decision that rejects every unit, or a delivery failure on an order already fully refunded, has amount 0. **Recommendation:** no `payment.refund-payment.v1` and no saga. The response has `refund_id = ""` and `amount_cents = 0`. `return-decided` / `delivery-failed` are still published. (Tasks 9, 10)
5. **`packing-started` for reship tasks.** The order stays *Shipped* during a reship (§6.5, ORD-R13), and `packing-started` would move it back. **Recommendation:** `fulfillment.packing-started.v1` only for `kind = order` tasks. `shipment-shipped` and `shipment-delivered` are published for reship tasks with `refId = reship_id` (spec §7). (Task 4)

## Global Constraints

- Build settings from `Directory.Build.props` (net10.0, Nullable, ImplicitUsings, `TreatWarningsAsErrors`). Central versions in `Directory.Packages.props`. **No new packages** (spec §5).
- No method, message, field, error reason, metric or route that 05-fulfillment, the spec or the owner items don't list (AGENTS.md §1). No health endpoints. Never touch `doc/`, `Bloom.Shared`, catalog, inventory, the gateway, Dockerfiles, Compose or manifests (spec §3).
- Anti-goals of spec §3 are hard rules: no handover of a cancelled task, nor a cancel of a handed-over task (FR-01); `refunded_cents ≤ paid_cents` always (FR-02, `CHECK` + guarded update); no handover with a serialized unit unscanned (FR-03); status only from a Shippo re-fetch (FR-04); no second paid label left un-voided (FR-05); no `pack_task_serials` row before inventory answers `ok`; no refund command without its saga and line updates in the same transaction.
- **Lock order**, always: `pack_tasks` → `shipments` → `return_requests` → `delivery_failures` → `order_accounts` → `order_line_accounts` (ascending `line_no`) → `warranty_coverages`. Every refund computation starts with `SELECT … FROM order_accounts WHERE order_id = @o FOR UPDATE` (serializes refunds of one order).
- Every staff write publishes `StaffActionRecorded` through `OutboxWriter.PublishEvent(IdentityMessages.StaffActionRecorded, …)` in its transaction; `service = fulfillment`, `action = fulfillment.<method in kebab case>` (spec §7).
- Business times come from the injected `TimeProvider`; SQL takes timestamps as parameters, never `now()` (the idempotency guard's own SQL is excepted).
- Every error is `BloomErrors.Create` shape (ErrorInfo domain `bloom`); reasons and statuses from Task 1's table only; inventory's reasons pass through (spec §4).
- JSON field names are proto lowerCamel; `int64` fields are JSON strings.
- Docker via Colima: never run `colima`; daemon unreachable → stop and report. Never `dotnet run`. Test containers must be gone after the run. No test reaches the internet.
- Every test case F-01 … F-15 has at least one test whose method name starts with its ID: `F01_…`. Test names below are binding; listed assertions are the minimum.
- Test command: `dotnet test --project src/Fulfillment/Bloom.Fulfillment.Tests`, narrowed with `--filter-class '*<Class>'`.

## Review Focus

1. **Concurrent decisions on one order.** Two `DecideReturn` calls for different returns of the same 2-unit, 1001-cent line, in parallel, must refund 500 and 501 (not 500 twice), and no call may fail with `40P01`. Test in Task 9.
2. **Webhook replays and late events.** `DELIVERED` twice gives one `shipment-delivered`; a late `TRANSIT` after `DELIVERED` changes nothing; a `TRANSIT` on a `possibly_lost` shipment dismisses it. Tests in Task 7.
3. **Scan retries around inventory.** If inventory answered `ok` but the local write was lost, a retry gives one row. An inventory refusal (`SERIAL_NOT_AVAILABLE`) passes through and writes nothing. Tests in Task 4.
4. **Other customers' data.** `RequestReturn`, `ListMyReturns` and `GetCoverage` for another customer's order or serial → `NOT_FOUND`, with no detail leaked. Tests in Tasks 8 and 11.
5. **Label buy vs cancel or expiry.** A Support cancel or a return expiry while a label is pending or being bought must leave no bought label un-voided. Tests in Tasks 5 and 8.

## Rulings in this plan (builder's calls the spec left open)

- **R1** Tests run the app on `TestServer` (as inventory R1); gRPC through `GrpcChannel.ForAddress(base, new() { HttpHandler = server.CreateHandler() })`; tests emulate the gateway with `x-bloom-user-id/type/role`.
- **R2** The app never migrates at startup; tests migrate. The app connects as the database owner (no restricted role; fulfillment has no append-only table). `DesignTimeFactory` uses env `Fulfillment__Database` if set, else `Host=design-time;Database=fulfillment`.
- **R3** `TryAddSingleton(TimeProvider.System)`; tests register `FakeTimeProvider` first.
- **R4** Config section `Fulfillment`: `Mode`, `Database`, `RabbitMq`, `Inventory` (absolute URL of inventory's gRPC endpoint), `Shippo:ApiKey`, `Shippo:WebhookSecret`, `Shippo:Carrier` (carrier code for `tracks`), `Shippo:StandardToken`, `Shippo:ExpressToken`, `Warehouse:Name|Street1|City|State|Zip|Phone`, `Parcel:LengthIn|WidthIn|HeightIn|WeightOz` (decimal > 0). Both modes need every key. Missing or invalid → throw at build, naming the key. `appsettings.json` has placeholders only (`Mode=api`, `RabbitMq=amqp://rabbitmq:5672`, `Inventory=http://inventory:8080`); the Shippo base URL is the constant `https://api.goshippo.com/`.
- **R5** `BloomOptions.ServiceName = "fulfillment"`; telemetry `fulfillment-api` / `fulfillment-worker`; meter `Bloom.Fulfillment`.
- **R6** Both modes register the four consumers (the DLQ admin needs the catalog). Only the worker calls `AddWorker()` and adds `LabelWorker`, `Sweeps`, `FulfillmentMonitor`. `SendsCommandsTo("inventory", "payment")`.
- **R7** Roles (identity's lowercase values) per spec §6: `Packers` = `warehouse, admin` (packing, scans, handover, `ReceiveReturn`, `RecordInspection`, `RecordReturnedToSender`, `ListPackTasks`); `Deciders` = `support, admin` (`DecideReturn`, `ConfirmLost`, `DismissPossiblyLost`, `ListDeliveryFailures`, staff `GetCoverage`); `ReturnViewers` = `warehouse, support, admin` (`ListReturns`). Customer methods need `x-bloom-user-type = customer`. Refusal → `PERMISSION_DENIED` / `ROLE_NOT_ALLOWED`. Missing `x-bloom-*` → `InvalidOperationException`. The role check runs before validation and before the idempotency guard.
- **R8** Transactions. Plain writes begin and commit their own transaction. `DecideReturn`, `RecordReturnedToSender`, `ConfirmLost(refund)` run in `IdempotencyGuard.ExecuteAsync`; `ConfirmLost(reship)` in `ExecuteWithProviderAsync` (provider = `CommitForReship`, spec §7). `ConfirmLost` picks the form from `resolution` before calling the guard. Consumers run in `ConsumerHost`'s transaction. Rows inserted through EF are saved before later SQL in the same transaction reads them.
- **R9** Status reads that pick an error reason happen *after* a guarded update affects 0 rows: re-read the row, then map it (e.g. `CancelPackTask`, `HandOver`). The error for a row in the wrong status is `INVALID_STATE` (`INVALID_RETURN_STATE` for return requests). Any status change that carries a `version` uses `ConcurrentUpdates.UpdateVersionedAsync` (stale → `CONCURRENCY_CONFLICT`). Every update of a versioned row sets `version = version + 1`.
- **R10** Validation (`VALIDATION_FAILED`, `metadata["field"]` = proto field name): ids are UUIDs; serials are `Trim().ToUpperInvariant()` then `^[A-Z0-9]{1,32}$` (as inventory R14); `units` non-empty; enum strings exactly as the LLD; `notes` ≤ 500 chars; paging as inventory R19 (`page` 0 → 1; `page_size` 0 → 50, 1–100); `status` filter must be a valid status.
- **R11** Inventory calls: 5 s deadline. The caller's `x-bloom-user-id`, `x-bloom-user-type` and `x-bloom-role` are forwarded as metadata (inventory audit, spec §7). An `RpcException` carrying a `bloom` `ErrorInfo` is rethrown with the same status and reason. For `CommitForReship`, `SOLD_OUT` becomes `RESHIP_NO_STOCK`. Anything else (`UNAVAILABLE`, `DEADLINE_EXCEEDED`) propagates unchanged.
- **R12** `reship_id` = RFC 9562 UUIDv5 (SHA-1) of the lost shipment's id under a fixed namespace constant in `Domain/Ids.cs`.
- **R13** Shippo usage (`ShippoClient : IShippo`, `Authorization: ShippoToken <key>`, 10 s timeout per call, JSON snake_case):
  - Buy: `POST shipments/` (`address_from`, `address_to`, one parcel from config, `extra.is_return` for return labels, `metadata`, `async: false`), pick the rate whose `servicelevel.token` matches. Then `POST transactions/` (`rate`, `label_file_type: "PDF"`, `metadata`, `async: false`). A `status` other than `SUCCESS`, or no matching rate, is an error.
  - List: `GET transactions/?results=100&page=n`, newest first, following `next`; stop at the first item older than `since`. Keep `status = SUCCESS` and `metadata` starting with `bloom:`.
  - Void: `POST refunds/` (`transaction`, `async: false`).
  - Track: `GET tracks/{carrier}/{tracking_number}` → `tracking_status.status`.
  - Metadata is `bloom:<shipment_id>`. Addresses: outbound and ship-back go warehouse → `ship_to`; return goes `ship_to` → warehouse. `ship_to` is the order's `kind = order` task. Outbound labels use the shipment's `service` token; return and ship-back use `standard`.
- **R14** Label worker (§6.6, spec §7): `PeriodicTimer(5 s)`. `internal Task RunOnceAsync(CancellationToken)` buys due shipments one per transaction (`SELECT … WHERE status = 'label_pending' AND next_attempt_at <= @now ORDER BY next_attempt_at LIMIT 1 FOR UPDATE SKIP LOCKED`), then voids `void_pending` ones the same way.
  - Buy: `ListLabelsAsync(created_at)` and match on metadata → reuse (`labels_total{reused}`), else buy (`{bought}`). Then a guarded `label_pending → label_ready` with transaction id, tracking number and label URL, `label_attempts = 0`. A return label also does `requested → label_issued` on the request and publishes `return-label-issued`.
  - Failure *n*: `label_attempts = n`. For n ≤ 4, `next_attempt_at = now + [1 s, 5 s, 30 s, 2 min][n−1]` with a warning log. For n ≥ 5, retry in 2 min; at n = 5 also `labels_total{failed}` and an error log.
  - Void: `VoidLabelAsync` → `voided`, `labels_total{voided}`. A failure logs a warning and the next tick retries.
- **R15** Taking a shipment's label away (cancel, expiry) uses one statement: `SET status = CASE WHEN status = 'label_ready' THEN 'void_pending' ELSE 'voided' END WHERE … AND status IN ('label_pending','label_ready')`. It waits on the label worker's row lock, then sees the committed status.
- **R16** Tracking (§6.3, spec §7). Webhook flow, in order:
  1. Wrong secret (`CryptographicOperations.FixedTimeEquals`) → `404`.
  2. Body > 64 KB → `413`.
  3. JSON without `data.tracking_number` → `400`.
  4. Unknown number → `200`.
  5. Re-fetch from Shippo (failure → `500`).
  6. In one transaction, lock the shipment `FOR UPDATE` and apply. Every event on a non-terminal shipment sets `last_tracking_at = now`. Terminal is `delivered`, `lost`, `returned_to_sender`, `void_pending`, `voided`: there it's a no-op with a warning log.
  - `tracking_webhooks_total{result}`: `applied` (status changed), `unchanged`, `unknown`, `bad_secret`, `invalid`, `error`.
  - `delivered_at` = `TimeProvider` now.
- **R17** Failure rows. A flag (`FAILURE` or the sweep) upserts `delivery_failures`: `kind = lost`, `status = possibly_lost`, `previous_status` = the shipment's status before. An existing `dismissed` row is reused (spec §4). `RETURNED` on `handed_over` / `in_transit` / `possibly_lost` upserts `kind = returned_to_sender`, `status = confirmed`. Dismiss (manual or automatic): failure → `dismissed`, `dismissed_at = now`, shipment → `previous_status`.
- **R18** Refunds are computed in `RefundEngine` (Task 9); unit shares are integer math (owner item 1). Partial tax = `round_half_up(partial × unit_tax / share)` = `(2·partial·unit_tax + share) / (2·share)` in `long`. Line updates are guarded: `refunded_cents + @x <= paid_cents AND refunded_units + @n <= quantity`; 0 rows → `InvalidOperationException` (cannot happen under the order lock). `refund_amount_cents_total{reason}` counts every command sent.
- **R19** `fulfillment_sagas`: `return_refund` keyed by `return_id`, `delivery_failure` keyed by `shipment_id`, `pending_steps = {refund}`, `refund_id` unique, set only when a command is sent (owner item 4).
- **R20** Account deletion (spec §4): on every pack task of the account's orders, `ship_to.name`, `ship_to.lines[*]` and `ship_to.phone` become `"[deleted]"` (as order's ACC-R06 rule); city, state and ZIP stay.
- **R21** `time_to_handover_seconds` is measured from `pack_tasks.created_at`, the arrival of `ready-to-fulfill`, which is sent when the order is paid.
- **R22** Monitor and alerts: `FulfillmentMonitor` (worker, every minute, `internal RunOnceAsync`) sets `pack_tasks_by_status`, `returns_by_status` and `possibly_lost_open`, and logs one error when sagas have been open > 30 min (count and refs). Label failures after retries and duplicate labels are error logs (R14, Task 11).
- **R23** Test isolation: one database per test host. Consumers are tested by invoking the handler in a scope and transaction like `ConsumerHost` (`Consume.Async`, as inventory). Only `WorkerTests` start worker hosts; sweeps, label worker and monitor are driven by `RunOnceAsync` / `Run…Async`.

---

## File Structure

```
Bloom.slnx                                  # + Bloom.Fulfillment, Bloom.Fulfillment.Tests, /src/Payment/Bloom.Payment.Contracts
src/Order/Bloom.Order.Contracts/            + OrderReadyToFulfillV1.cs, OrderMessages.ReadyToFulfill
src/Payment/Bloom.Payment.Contracts/        .csproj (plain SDK), PaymentMessages.cs, Messages.cs
src/Fulfillment/Bloom.Fulfillment.Contracts/  .csproj (as Inventory.Contracts), FulfillmentMessages.cs, Messages.cs, Protos/fulfillment_internal.proto
src/Fulfillment/Bloom.Fulfillment/
  Bloom.Fulfillment.csproj (Sdk.Web), Program.cs, FulfillmentApp.cs, FulfillmentOptions.cs, appsettings.json
  Protos/fulfillment.proto                  # package bloom.fulfillment.v1: FulfillmentWarehouse, FulfillmentSupport, FulfillmentCustomer
  Data/ FulfillmentDbContext.cs, Entities.cs, DesignTimeFactory.cs, Db.cs, Migrations/
  Errors/ FulfillmentErrors.cs, Inputs.cs   Context/Caller.cs   Observability/FulfillmentMetrics.cs
  Domain/ Ids.cs, Audit.cs, InventoryCalls.cs, Views.cs, Failures.cs, RefundEngine.cs
  Shippo/ IShippo.cs, ShippoClient.cs
  Services/ InternalService.cs, WarehouseService.Packing.cs, .Handover.cs, .Returns.cs, .Failures.cs,
            SupportService.cs, CustomerService.cs, ShippoWebhook.cs
  Consumers/ ReadyToFulfillConsumer.cs, RefundCompletedConsumer.cs, RefundFailedConsumer.cs, AccountDeletedConsumer.cs
  Worker/ LabelWorker.cs, Sweeps.cs, FulfillmentMonitor.cs
src/Fulfillment/Bloom.Fulfillment.Tests/
  Infrastructure/ Containers.cs, TestFulfillment.cs, FakeInventory.cs, FakeShippo.cs, Api.cs, Seed.cs, Outbox.cs, Consume.cs,
                  MetricCapture.cs, LogCapture.cs, Poll.cs
  ShellTests, SchemaTests, ReadyToFulfillTests, PackingTests, LabelTests, HandoverTests, TrackingTests,
  ReturnTests, RefundTests, DeliveryFailureTests, CoverageTests, WorkerTests
```

Routes: spec §6 table, verbatim. Every `POST` uses `body: "*"`. `FulfillmentInternal.CancelPackTask` has no HTTP binding.

---

### Task 1: Contracts, app shell, config, errors, caller, metrics, test infrastructure

**Files:**
- Create: `src/Payment/Bloom.Payment.Contracts/{Bloom.Payment.Contracts.csproj, PaymentMessages.cs, Messages.cs}`, `src/Order/Bloom.Order.Contracts/OrderReadyToFulfillV1.cs`, `src/Fulfillment/Bloom.Fulfillment.Contracts/Protos/fulfillment_internal.proto`
- Modify: `OrderMessages.cs`, `Bloom.Fulfillment.Contracts.csproj` (as `Bloom.Inventory.Contracts.csproj`, `GrpcServices="Both"`), `FulfillmentMessages.cs`, `Messages.cs` (Fulfillment.Contracts), `Bloom.slnx`
- Create: `Bloom.Fulfillment.csproj` (as `Bloom.Inventory.csproj`, refs Shared, Identity/Inventory/Order/Payment/Fulfillment Contracts, `InternalsVisibleTo Bloom.Fulfillment.Tests`), `Program.cs`, `FulfillmentApp.cs`, `FulfillmentOptions.cs`, `appsettings.json`, `Errors/FulfillmentErrors.cs`, `Errors/Inputs.cs`, `Context/Caller.cs`, `Observability/FulfillmentMetrics.cs`, `Data/FulfillmentDbContext.cs` (reliability tables only), `Data/DesignTimeFactory.cs`
- Create: `Bloom.Fulfillment.Tests.csproj` (as inventory's), `Infrastructure/{Containers, TestFulfillment, Api, Outbox, Consume, MetricCapture, LogCapture, Poll}.cs` (copy inventory's; `Containers` without the app role)
- Test: `ShellTests.cs`

**Interfaces:**
- Produces:
  - `OrderMessages.ReadyToFulfill = "order.ready-to-fulfill.v1"`; `OrderReadyToFulfillV1(Guid OrderId, Guid AccountId, string Number, ShipToV1 ShipTo, string ShippingOption, long ShippingCents, long TotalCents, IReadOnlyList<ReadyLineV1> Lines)`; `ShipToV1(string Name, IReadOnlyList<string> Lines, string City, string State, string Zip, string Phone)`; `ReadyLineV1(int LineNo, string Kind /* product | warranty_plan */, Guid SkuId, Guid ProductId, bool Serialized, int Quantity, int? ParentLineNo, int? CoverageMonths, long PaidCents, long TaxCents)` (03-order §5.3).
  - `PaymentMessages.RefundPayment = "payment.refund-payment.v1"`, `RefundCompleted = "payment.refund-completed.v1"`, `RefundFailed = "payment.refund-failed.v1"`; `RefundPaymentV1(Guid OrderId, Guid RefundId, long AmountCents, string Reason, IReadOnlyList<RefundLineV1>? Lines)`, `RefundLineV1(int LineNo, long AmountCents)`, `RefundCompletedV1(Guid OrderId, Guid RefundId, long AmountCents, IReadOnlyList<RefundLineV1>? Lines)`, `RefundFailedV1(Guid OrderId, Guid RefundId, string FailureReason)` (spec §4, 04-payment §5).
  - `FulfillmentMessages` adds `PackingStarted`, `ShipmentDelivered`, `DeliveryFailed`, `ReturnRequested`, `ReturnClosed`, `ReturnLabelIssued`, `ReturnReceived`, `ReturnDecided` (`fulfillment.<name>.v1`, §5.3). Records: `PackingStartedV1(Guid OrderId)`, `ShipmentDeliveredV1(Guid OrderId, Guid RefId, DateTimeOffset DeliveredAt)`, `DeliveryFailedV1(Guid OrderId, string Kind)`, `ReturnRequestedV1(Guid OrderId, Guid ReturnId)`, `ReturnClosedV1(Guid OrderId, Guid ReturnId, string Reason /* decided | expired */)`, `ReturnLabelIssuedV1(Guid OrderId, Guid ReturnId, string LabelUrl)`, `ReturnReceivedV1(Guid OrderId, Guid ReturnId)`, `ReturnDecidedV1(Guid OrderId, Guid ReturnId, IReadOnlyList<AcceptedLineV1> Accepted, long RefundCents)`, `AcceptedLineV1(int LineNo, int Quantity)`.
  - `fulfillment_internal.proto` (package `bloom.fulfillment.v1`, `csharp_namespace = "Bloom.Fulfillment.Contracts"`): `service FulfillmentInternal { rpc CancelPackTask(CancelPackTaskRequest) returns (google.protobuf.Empty); }`, `CancelPackTaskRequest { string order_id = 1; string by = 2; }`.
  - `FulfillmentApp.Build(string[] args, Action<WebApplicationBuilder>? configure = null)` — same shape as `InventoryApp.Build` (R4–R6). Adds `AddGrpcClient<InventoryPacking.InventoryPackingClient>` and `InventoryStock.InventoryStockClient` (address `Inventory`), and `AddHttpClient<IShippo, ShippoClient>` in Task 5.
  - `FulfillmentOptions` (R4), `bool IsApi`, `void Validate()`.
  - `FulfillmentErrors` — `const string` per reason, `RpcException Error(string reason, string message)`, `Validation(string field, string message)`, `NotFound(string what)`:

    | Reason | Status | Reason | Status |
    |---|---|---|---|
    | `PACKING_STARTED` | FailedPrecondition | `RETURN_QUANTITY_EXCEEDED` | InvalidArgument |
    | `ALREADY_SHIPPED` | FailedPrecondition | `INVALID_RETURN_STATE` | FailedPrecondition |
    | `SERIALS_INCOMPLETE` | FailedPrecondition | `PARTIAL_EXCEEDS_SHARE` | InvalidArgument |
    | `LABEL_NOT_READY` | FailedPrecondition | `RESHIP_NO_STOCK` | FailedPrecondition |
    | `RETURN_WINDOW_CLOSED` | FailedPrecondition | `INVALID_STATE` | FailedPrecondition |
    | `NOT_FOUND` | NotFound | `VALIDATION_FAILED` | InvalidArgument |
  - `Inputs.Id(string value, string field)`, `Inputs.Serial(string value, string field)`, `Inputs.Paging(int page, int pageSize) → (int Skip, int Take)` (R10).
  - `Caller(Guid UserId, string UserType, string? Role)` — copy inventory's `From` / `TryFrom` / `RequireStaff`, plus `RequireCustomer()`; role arrays of R7.
  - `FulfillmentMetrics` (`MeterName = "Bloom.Fulfillment"`): `HandedOver(string shippingOption, double seconds)`, `Label(string result)`, `Refund(string reason, long cents)`, `Webhook(string result)`, `SetGauges(IReadOnlyDictionary<string,long> packTasks, IReadOnlyDictionary<string,long> returns, long possiblyLostOpen)`. Instruments per §10.
  - Tests: `Containers` (assembly fixture: `postgres:17.6` with `max_connections=1000`, `rabbitmq:4.1.4-management`, `Task<string> CreateDatabaseAsync()`, `RabbitMqUri`, `RabbitCtlAsync`); `TestFulfillment.StartAsync(Containers c, Action<TestFulfillmentOptions>? configure = null)` with options `Mode`, `ConnectionString`, `Config`; exposes `Http`, `Internal` (`FulfillmentInternal.FulfillmentInternalClient`), `Clock`, `Logs`, `Services`, `ConnectionString`, `NewDb()`. Default config fills every R4 key (webhook secret `secret-test`, tokens `usps_ground_advantage` / `usps_priority`, carrier `usps`). Tasks 4 and 5 add `Inventory` (FakeInventory) and `Shippo` (FakeShippo). `Api`/`Rpc`/`TestCaller` as inventory's, plus `TestCaller.Customer(Guid? id = null)`.

- [ ] **Step 1: Create the projects** and contracts; add the projects to `Bloom.slnx` (`/src/Fulfillment/`, `/src/Payment/`).
- [ ] **Step 2: Write the failing tests** in `ShellTests.cs`

```csharp
[Fact] public void Unknown_mode_fails_at_build()
[Theory, MemberData(nameof(RequiredKeys))]  // every R4 key, both modes
public void Missing_config_fails_at_build_naming_the_key(string key)
[Theory, MemberData(nameof(ReasonTable))] public void Each_reason_has_its_status(string reason, StatusCode code)   // the 12 rows; domain "bloom"
[Fact] public void Role_checks()             // warehouse ∈ Packers ∉ Deciders; support ∈ Deciders, ReturnViewers; customer fails RequireStaff; staff fails RequireCustomer
[Fact] public void Payloads_serialize_to_the_documented_fields()   // RefundPaymentV1 → orderId, refundId, amountCents, reason, lines[{lineNo, amountCents}]; OrderReadyToFulfillV1 round-trips with BloomJson
[Fact] public async Task Api_host_starts()   // CancelPackTask reachable (returns VALIDATION_FAILED for order_id "x")
```

- [ ] **Step 3: Run** `dotnet test --project src/Fulfillment/Bloom.Fulfillment.Tests --filter-class '*ShellTests'` — Expected: build fails (types missing).
- [ ] **Step 4: Implement** contracts, shell, options, errors, caller, metrics, test infrastructure. `InternalService.CancelPackTask` validates and throws `NotImplementedException` until Task 3.
- [ ] **Step 5: Run** — PASS. `dotnet build -c Release` — 0 warnings (inventory, which references Fulfillment.Contracts, still builds).
- [ ] **Step 6: Commit** — `git add -A && git commit -m "feat(fulfillment): contracts, app shell, config, errors and test infrastructure"`

---

### Task 2: Data model and initial migration

**Files:**
- Create: `Data/Entities.cs`, `Data/Db.cs` (as inventory's), `Data/Migrations/*` (`dotnet ef migrations add Initial --project src/Fulfillment/Bloom.Fulfillment`)
- Modify: `Data/FulfillmentDbContext.cs`
- Test: `SchemaTests.cs`

**Interfaces:**
- Produces (snake_case columns of §4 plus spec §4/§7 additions; `timestamptz` ↔ `DateTimeOffset`; UUIDv7 ids set by the app; constants classes for every enum column: `TaskKinds`, `TaskStatuses`, `LineKinds` (`product`, `warranty_plan`), `Directions`, `ShipmentStatuses`, `ReturnStatuses`, `InspectionResults`, `Decisions`, `FailureKinds`, `FailureStatuses`, `SagaTypes`, `CoverageStatuses`):
  - `PackTask { Guid Id, RefId, OrderId; string Kind, Status; bool CancelledBeforeArrival; string? ShipTo /* jsonb ShipToV1, null on markers */; string? ShippingOption; long Version; DateTimeOffset CreatedAt, UpdatedAt; }` — unique `ux_pack_tasks_ref_id`.
  - `PackTaskLine { Guid TaskId; int LineNo; string Kind; Guid SkuId, ProductId; bool Serialized; int Quantity; int? ParentLineNo; int? CoverageMonths; }`; `PackTaskSerial { Guid TaskId; string Serial; int LineNo; int Seq; DateTimeOffset ScannedAt; }` (PK `(task_id, serial)`, unique `(task_id, seq)`).
  - `Shipment { Guid Id; Guid? PackTaskId, ReturnRequestId; Guid OrderId; string Direction, Service, Status; string? ShippoTransactionId, TrackingNumber, LabelUrl; DateTimeOffset? LastTrackingAt, HandedOverAt, DeliveredAt; int LabelAttempts; DateTimeOffset NextAttemptAt; long Version; DateTimeOffset CreatedAt; }` — unique `shippo_transaction_id`, `tracking_number`; index `(next_attempt_at) WHERE status = 'label_pending'`, `(pack_task_id)`.
  - `OrderAccount { Guid OrderId, AccountId; long ShippingCents, TotalCents; bool ShippingRefunded; DateTimeOffset? DeliveredAt; }` (index `account_id`); `OrderLineAccount { Guid OrderId; int LineNo; string Kind; int? ParentLineNo; int Quantity; long PaidCents, TaxCents; int RefundedUnits; long RefundedCents; }` — `ck_order_line_accounts_refund_le_paid` (`refunded_cents <= paid_cents`), `refunded_units <= quantity`, both `>= 0`.
  - `ReturnRequest { Guid Id, OrderId, AccountId; string Status; DateTimeOffset ExpiresAt; Guid? ReturnShipmentId; long Version; DateTimeOffset CreatedAt, UpdatedAt; }` (indexes `(order_id)`, `(account_id, created_at)`, `(expires_at) WHERE status IN ('requested','label_issued')`); `ReturnUnit { Guid ReturnId; int UnitNo; int LineNo; string? ReceivedSerial, InspectionResult, InspectionNotes, Decision; long RefundCents, RefundTaxCents; }`.
  - `DeliveryFailure { Guid ShipmentId; string Kind, Status; string? PreviousStatus; string? RtsOutcomes /* jsonb */; Guid? ReshipId, DecidedBy; DateTimeOffset CreatedAt, UpdatedAt; DateTimeOffset? DismissedAt; }`.
  - `FulfillmentSaga { Guid RefId; string Type; string[] PendingSteps; Guid RefundId; DateTimeOffset StartedAt; DateTimeOffset? CompletedAt; }` — PK `(ref_id, type)`, unique `refund_id`.
  - `WarrantyCoverage { Guid Id, OrderId; int PlanLineNo, DeviceLineNo; string Serial; int CoverageMonths; string Status; DateTimeOffset? StartsAt, EndsAt; }` — `ux_warranty_coverages_serial_open` on `serial` `WHERE status IN ('active','pending_delivery')`; index `order_id`.
  - Named `CHECK`s on every enum column; `AddBloomReliabilityTables()`.

- [ ] **Step 1: Write the failing tests** in `SchemaTests.cs`

```csharp
[Fact] public async Task Migration_applies_and_model_has_no_pending_changes()
[Fact] public async Task Refund_above_paid_is_rejected()            // UPDATE order_line_accounts SET refunded_cents = paid_cents + 1 → 23514 ck_order_line_accounts_refund_le_paid
[Fact] public async Task Ref_id_is_unique()                         // two pack_tasks with one ref_id → 23505
[Fact] public async Task One_open_coverage_per_serial()             // two pending_delivery for a serial → 23505; cancelled + active → ok
[Fact] public async Task Tracking_number_and_refund_id_are_unique()
[Fact] public async Task Enum_columns_reject_unknown_values()        // shipments.status 'x' → 23514
```

- [ ] **Step 2: Run** `--filter-class '*SchemaTests'` — FAIL.
- [ ] **Step 3: Implement** entities, configuration and the migration.
- [ ] **Step 4: Run** — PASS.
- [ ] **Step 5: Commit** — `git commit -m "feat(fulfillment): data model and migration"`

---

### Task 3: `order.ready-to-fulfill.v1` consumer and `CancelPackTask`

**Files:**
- Create: `Consumers/ReadyToFulfillConsumer.cs`, `Services/InternalService.cs` (replace the stub), Tests `Infrastructure/Seed.cs`
- Modify: `FulfillmentApp.cs` (`AddConsumer<ReadyToFulfillConsumer>("ready-to-fulfill", ConsumerBinding.Event("order", OrderMessages.ReadyToFulfill))`)
- Test: `ReadyToFulfillTests.cs`

**Interfaces:**
- Produces:
  - Consumer: `GetPayload<OrderReadyToFulfillV1>()`; invalid payload (no lines, unknown `kind` / `shippingOption`, `quantity ≤ 0`, plan without `parentLineNo` / `coverageMonths`) → `PermanentFailureException` before any write. `INSERT pack_tasks … ON CONFLICT (ref_id) DO NOTHING` (`ref_id = order_id`, `kind = order`, `pending`, `ship_to` JSON, `shipping_option`); conflict → nothing else written (F-02). Else lines, `order_accounts`, `order_line_accounts`.
  - `CancelPackTask` (§6.1, R9): `by` ∈ `customer|support` else `VALIDATION_FAILED by`. Acts on the task with `ref_id = order_id`.
    - No task → insert a marker (`cancelled`, `cancelled_before_arrival`, `kind = order`). On a unique conflict, run again.
    - Customer: `pending → cancelled`. Support: `pending|packing|packed → cancelled`, then the shipment per R15.
    - 0 rows → re-read: `cancelled` → ok; `packing|packed` → `PACKING_STARTED` (customer); `handed_over` → `ALREADY_SHIPPED`.
    - No audit (order audits its own cancel).
  - `Seed.Ready(Action<ReadyBuilder>? b = null) → OrderReadyToFulfillV1`. Default: one serialized device line 1 (qty 2, paid 2002, tax 160), one plan line 2 (parent 1, qty 1, 12 months, paid 9900, tax 800), shipping `standard` 0 cents; total = sum. Builder: `Line(...)`, `Express(long cents)`, `Account(Guid)`. `Seed.ReadyAsync(t, OrderReadyToFulfillV1)` consumes it via `Consume.Async`.

- [ ] **Step 1: Write the failing tests** in `ReadyToFulfillTests.cs`

```csharp
[Fact] public async Task Ready_creates_task_lines_and_accounts()        // task pending kind order ref = order; lines; order_accounts totals; line accounts refunded 0
[Fact] public async Task Duplicate_ready_creates_one_task()
[Fact] public async Task F02_cancel_before_ready_leaves_a_marker_and_the_event_is_ignored()
// CancelPackTask(customer) with no task → ok, marker cancelled_before_arrival; then ready → still one cancelled task, no lines, no accounts
[Fact] public async Task Customer_cancel_of_pending_task()               // → cancelled; repeat → ok
[Fact] public async Task Support_cancel_of_pending_task()
[Fact] public async Task Cancel_validates_input()                       // order_id "x" → VALIDATION_FAILED order_id; by "bot" → by
[Fact] public async Task Invalid_ready_payload_is_permanent()            // empty lines → PermanentFailureException; nothing written
```

- [ ] **Step 2: Run** `--filter-class '*ReadyToFulfillTests'` — FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** — PASS.
- [ ] **Step 5: Commit** — `git commit -m "feat(fulfillment): ready-to-fulfill consumer and pack task cancellation"`

---

### Task 4: Packing (StartPacking, scans, CompletePacking, ListPackTasks)

**Files:**
- Create: `Protos/fulfillment.proto` (service `FulfillmentWarehouse` with these methods), `Services/WarehouseService.Packing.cs`, `Domain/Views.cs` (entity → proto mapping; later tasks add theirs), `Domain/Audit.cs` (as inventory's), `Domain/InventoryCalls.cs`, Tests `Infrastructure/FakeInventory.cs`
- Modify: `FulfillmentApp.cs` (map `WarehouseService`), `TestFulfillment.cs` (+`Inventory`), `Seed.cs`
- Test: `PackingTests.cs`

**Interfaces:**
- Consumes: Task 3 rows; inventory `InventoryPacking` client.
- Produces:
  - Proto messages: `PackTask { string id; string order_id; string ref_id; string kind; string status; string shipping_option; int64 version; repeated PackTaskLine lines; repeated ScannedSerial serials; google.protobuf.Timestamp created_at; }`, `PackTaskLine { int32 line_no; string kind; string sku_id; string product_id; bool serialized; int32 quantity; optional int32 parent_line_no; optional int32 coverage_months; }`, `ScannedSerial { int32 line_no; string serial; int32 seq; }`, `Shipment { string id; string order_id; string direction; string service; string status; string tracking_number; string label_url; int64 version; }`.
  - `StartPacking { task_id, int64 version } → PackTask`. Versioned `pending → packing`. Publish `PackingStartedV1` only for `kind = order` (owner item 5). Audit `fulfillment.start-packing`.
  - `ScanSerial { task_id, int32 line_no, serial } → PackTask` (§6.2, spec §7):
    1. Task must be `packing` (`INVALID_STATE`). The line must be a serialized product line (`VALIDATION_FAILED line_no`). Serial already on this line → return the task unchanged. On another line → `VALIDATION_FAILED serial`. Line full → `VALIDATION_FAILED line_no`.
    2. `InventoryCalls.AssignAsync(refId, skuId, serial)`.
    3. One transaction: lock the task `FOR UPDATE`, re-check `packing` and room on the line, insert with `seq = max + 1`. A PK conflict counts as the same scan.
  - `UnassignSerial { task_id, serial } → PackTask`: `packing` only. A serial not on the task → task unchanged, inventory not called. Else inventory, then delete the row.
  - `ReplaceSerial { task_id, old_serial, new_serial } → PackTask`: `packing` only. New serial already on the task where the old one was → unchanged. Old one missing → `VALIDATION_FAILED old_serial`. Else inventory `ReplaceSerial`, then update the row in place (same `line_no`, `seq`).
  - `CompletePacking { task_id, int64 version } → CompletePackingResponse { PackTask task; Shipment shipment; }` (§6.2 step 2):
    1. Task status must be `packing` (`INVALID_STATE`). Any serialized product line short of `quantity` serials → `SERIALS_INCOMPLETE`.
    2. Versioned `→ packed`. Insert an outbound shipment (`label_pending`, `service = shipping_option`, `next_attempt_at = created_at = now`).
    3. Coverages for `kind = order` tasks: each plan line's `quantity` units attach to the first N serials by `seq` of its parent line (`pending_delivery`). Reship tasks: Task 10.
    4. Audit `fulfillment.complete-packing`.
  - `ListPackTasks { optional string status; int32 page; int32 page_size; } → { repeated PackTask items; }`: oldest first by `(created_at, id)`. Markers are excluded.
  - `InventoryCalls` (scoped): `AssignAsync(Guid refId, Guid skuId, string serial, ServerCallContext ctx)`, `UnassignAsync(Guid refId, string serial, ctx)`, `ReplaceAsync(Guid refId, string oldSerial, string newSerial, ctx)`, `CommitForReshipAsync(Guid reshipId, Guid orderId, IEnumerable<(Guid SkuId, int Quantity)> lines, CancellationToken)` (R11).
  - `FakeInventory` (Kestrel loopback HTTP/2, as `FakeCatalog`): records `Calls` (method, request, `x-bloom-user-id`). `Fail(string method, StatusCode code, string reason, int times = 1)`, `bool SoldOut` (`CommitForReship` → `SOLD_OUT`), `Func<Task>? Before` (lets a test make a call slow).
  - Seed: `Seed.PackedAsync(t, OrderReadyToFulfillV1 order, params string[] serials) → (Guid TaskId, Guid ShipmentId)` (start, scan in order, complete, as `warehouse`).

- [ ] **Step 1: Write the failing tests** in `PackingTests.cs` (staff `warehouse` unless stated)

```csharp
[Fact] public async Task Start_packing_publishes_packing_started_and_audits()   // version 1→2; PackingStartedV1(orderId); audit fulfillment.start-packing; stale version → CONCURRENCY_CONFLICT
[Fact] public async Task F03_customer_cancel_during_packing_is_PACKING_STARTED()
[Fact] public async Task Support_cancel_during_packing_and_packed()             // packing → cancelled; packed (label_pending) → cancelled, shipment voided
[Fact] public async Task Scan_calls_inventory_first_and_records_seq()           // FakeInventory AssignSerial(ref_id, sku, "ABC1") with x-bloom-user-id; rows seq 1, 2; " abc1 " normalized
[Fact] public async Task Inventory_refusal_passes_through_and_writes_nothing()   // Review Focus 3: SERIAL_NOT_AVAILABLE → same status + reason; no row
[Fact] public async Task Scan_retry_after_lost_local_write_gives_one_row()      // Review Focus 3: scan, delete the local row (lost write), scan again → inventory called again, one row; a third identical scan → unchanged, no inventory call
[Fact] public async Task Scan_rules()                                           // non-serialized line, plan line → VALIDATION_FAILED line_no; full line → line_no; serial on other line → serial; task pending → INVALID_STATE
[Fact] public async Task Unassign_and_replace_keep_positions()                  // replace keeps seq; unassign of unknown serial → unchanged, no inventory call
[Fact] public async Task F04_complete_packing_with_a_missing_serial_is_SERIALS_INCOMPLETE()
[Fact] public async Task F05_one_plan_for_a_two_unit_device_covers_the_first_scanned_serial()
// scan B then A → coverage serial B, pending_delivery, months 12; one shipment label_pending outbound service standard
[Fact] public async Task List_pack_tasks_filters_and_pages()                    // status filter, oldest first, page_size 1; bad status → VALIDATION_FAILED status
[Theory, InlineData("support"), InlineData("customer")]
public async Task Only_warehouse_and_admin_pack(string role)                    // 403 ROLE_NOT_ALLOWED
```

- [ ] **Step 2: Run** `--filter-class '*PackingTests'` — FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** — PASS.
- [ ] **Step 5: Commit** — `git commit -m "feat(fulfillment): packing with serial scanning and warranty coverage"`

---

### Task 5: Shippo client and label worker

**Files:**
- Create: `Shippo/IShippo.cs`, `Shippo/ShippoClient.cs`, `Worker/LabelWorker.cs`, Tests `Infrastructure/FakeShippo.cs`
- Modify: `FulfillmentApp.cs` (`AddHttpClient<IShippo, ShippoClient>`; worker: `LabelWorker`), `TestFulfillment.cs` (+`Shippo`, wired with `ConfigurePrimaryHttpMessageHandler`)
- Test: `LabelTests.cs`

**Interfaces:**
- Produces:
  - `interface IShippo { Task<ShippoLabel> BuyLabelAsync(LabelRequest request, CancellationToken ct); Task<IReadOnlyList<ShippoLabel>> ListLabelsAsync(DateTimeOffset since, CancellationToken ct); Task VoidLabelAsync(string transactionId, CancellationToken ct); Task<string> GetTrackingStatusAsync(string trackingNumber, CancellationToken ct); }`
  - `record ShippoAddress(string Name, string Street1, string? Street2, string City, string State, string Zip, string Phone)`, `record LabelRequest(Guid ShipmentId, ShippoAddress From, ShippoAddress To, string ServiceLevelToken, bool IsReturn)`, `record ShippoLabel(string TransactionId, string TrackingNumber, string LabelUrl, string Metadata, DateTimeOffset CreatedAt)`. HTTP per R13; non-2xx or malformed → `ShippoException`.
  - `LabelWorker` (R14, R15): `internal Task RunOnceAsync(CancellationToken)`.
  - `FakeShippo : HttpMessageHandler` serving R13's five endpoints from memory. Members:
    - `IReadOnlyList<ShippoLabel> Transactions`, `IReadOnlyList<string> Refunds`, `int Calls(string pathPrefix)`
    - `TimeoutAfterStoringNextTransaction` (stores it, then throws `TaskCanceledException`), `FailNext(int n)` (500s), `NoMatchingRate`, `Action? BeforeTransaction` (runs inside the buy)
    - `SetTracking(string number, string status)` (default `PRE_TRANSIT`), `AddLabel(string metadata)` (foreign or duplicate labels)
    - Tracking numbers `TRK<n>`, label URLs `https://labels.test/<n>.pdf`.
  - Seed: `Seed.LabelReadyAsync(t, OrderReadyToFulfillV1 order, params string[] serials) → (Guid TaskId, Guid ShipmentId)` = `PackedAsync` + `LabelWorker.RunOnceAsync`.

- [ ] **Step 1: Write the failing tests** in `LabelTests.cs`

```csharp
[Fact] public async Task Worker_buys_the_label()
// label_ready, transaction id, TRK, label URL; fake got shipment from warehouse to ship_to, parcel from config, token of the service, metadata "bloom:<id>"; labels_total{bought} 1
[Fact] public async Task Express_shipment_uses_the_express_token()
[Fact] public async Task F06_label_buy_times_out_then_retry_reuses_the_label()
// TimeoutAfterStoringNextTransaction → label_pending, attempts 1, next +1 s; clock +1 s, RunOnce → label_ready with that transaction; fake has exactly 1 transaction; labels_total{reused} 1
[Fact] public async Task Failures_follow_the_retry_schedule()
// FailNext(6): next_attempt_at +1 s, +5 s, +30 s, +2 min, then +2 min; at attempt 5 labels_total{failed} 1 and an error log; not due yet → no call
[Fact] public async Task Void_pending_is_voided()                       // packed task cancelled by Support after label_ready → void_pending → RunOnce → voided; fake Refunds has it; labels_total{voided}
[Fact] public async Task Cancel_while_label_pending_buys_nothing()      // Review Focus 5: cancel before RunOnce → voided, fake has 0 transactions
[Fact] public async Task Cancel_during_buy_voids_the_bought_label()     // Review Focus 5: BeforeTransaction starts a Support cancel without awaiting it; it waits on the row lock; after RunOnce, await it → ok, shipment void_pending → next RunOnce voids it
```

- [ ] **Step 2: Run** `--filter-class '*LabelTests'` — FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** — PASS.
- [ ] **Step 5: Commit** — `git commit -m "feat(fulfillment): Shippo client and label worker"`

---

### Task 6: HandOver

**Files:**
- Create: `Services/WarehouseService.Handover.cs`
- Modify: `Protos/fulfillment.proto` (+`HandOver`)
- Test: `HandoverTests.cs`

**Interfaces:**
- Produces:
  - `HandOverRequest { oneof target { string shipment_id = 1; string tracking_number = 2; } } → Shipment`. Unknown → `NOT_FOUND`; neither set → `VALIDATION_FAILED target`.
  - Outbound (§6.2 step 4, spec §7):
    - Already `handed_over` → return it unchanged (no writes).
    - Else, in this order: guarded task `packed → handed_over`, then shipment `label_ready → handed_over` (`handed_over_at`). If either affects 0 rows → roll back and map per R9: shipment now `handed_over` (a concurrent double scan) → return it unchanged; shipment `label_pending` → `LABEL_NOT_READY`; anything else (task `cancelled`, …) → `INVALID_STATE`.
    - Publish `ShipmentShippedV1(orderId, task.ref_id, lines: product lines with their serials by seq, trackingNumber)`. Metric `time_to_handover_seconds` (R21). Audit `fulfillment.hand-over`.
  - Ship-back: shipment `label_ready → handed_over` only; no task, no message, audit. Return shipments → `INVALID_STATE`.

- [ ] **Step 1: Write the failing tests** in `HandoverTests.cs`

```csharp
[Fact] public async Task Handover_by_tracking_number_ships_the_task()
// shipment + task handed_over; ShipmentShippedV1 lines [{sku, 2, [B, A] by seq}] without plan line, refId = order; audit; time_to_handover_seconds recorded
[Fact] public async Task Handover_by_shipment_id()
[Fact] public async Task Double_scan_returns_the_shipment_unchanged()   // second call → same shipment, one message
[Fact] public async Task Handover_before_label_is_LABEL_NOT_READY()
[Fact] public async Task Handover_of_cancelled_task_is_INVALID_STATE()  // FR-01
[Fact] public async Task F01_support_cancel_and_handover_at_the_same_instant()
// 30 rounds on fresh label_ready tasks: CancelPackTask(support) ∥ HandOver → exactly one succeeds
// (cancel ok + handover INVALID_STATE, or handover ok + cancel ALREADY_SHIPPED); never both; no 40P01 in logs
```

- [ ] **Step 2: Run** `--filter-class '*HandoverTests'` — FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** — PASS.
- [ ] **Step 5: Commit** — `git commit -m "feat(fulfillment): handover scan"`

---

### Task 7: Shippo webhook, tracking and the possibly-lost sweep

**Files:**
- Create: `Services/ShippoWebhook.cs` (`MapShippoWebhook(this IEndpointRouteBuilder)`), `Domain/Failures.cs`, `Worker/Sweeps.cs` (possibly-lost part)
- Modify: `FulfillmentApp.cs` (API: map webhook; worker: `Sweeps`)
- Test: `TrackingTests.cs`

**Interfaces:**
- Produces:
  - Webhook per R16. Outbound transitions (§6.3, spec §7, R17):

    | Shippo status | From | Effect |
    |---|---|---|
    | `TRANSIT` | `handed_over` | → `in_transit` |
    | `TRANSIT` | `possibly_lost` | auto-dismiss, then → `in_transit` |
    | `DELIVERED` | `handed_over`, `in_transit`, `possibly_lost` (auto-dismiss first) | → `delivered`, `delivered_at`; `order_accounts.delivered_at`; the order's `pending_delivery` coverages → `active` (`starts_at = delivered_at`, `ends_at = + coverage_months`); publish `ShipmentDeliveredV1(orderId, task.ref_id, deliveredAt)` |
    | `RETURNED` | `handed_over`, `in_transit`, `possibly_lost` | → `returned_to_sender`; failure row (R17) |
    | `FAILURE` | `handed_over`, `in_transit` | → `possibly_lost`; failure row (R17); error log |
    | other | any non-terminal | `last_tracking_at` only |

    Ship-back: `TRANSIT` `handed_over → in_transit`; `DELIVERED` `handed_over|in_transit → delivered`; nothing else. Return shipments: Task 8.
  - `Failures` (scoped): `Task FlagAsync(Shipment s, CancellationToken ct)`, `Task DismissAsync(Guid shipmentId, CancellationToken ct)` (R17; caller holds the shipment lock).
  - `Sweeps.RunPossiblyLostAsync(CancellationToken)`: outbound `handed_over|in_transit` where `greatest(last_tracking_at, handed_over_at, failure.dismissed_at) < now − 7 days` → flag each. `Sweeps : BackgroundService` runs it hourly (`PeriodicTimer(1 h, TimeProvider)`).

- [ ] **Step 1: Write the failing tests** in `TrackingTests.cs` (shipment handed over; POST `/webhooks/shippo/{secret}` with `{"event":"track_updated","data":{"tracking_number":"TRK1","tracking_status":{"status":…}}}`)

```csharp
[Fact] public async Task Transit_then_delivered()
// fake TRANSIT → in_transit, last_tracking_at; fake DELIVERED → delivered, order delivered_at, coverage active with ends_at +12 months, ShipmentDeliveredV1; webhooks applied 2
[Fact] public async Task F07_forged_webhook_applies_the_refetched_status()   // body says DELIVERED, fake says TRANSIT → in_transit; no delivered message
[Fact] public async Task Repeated_and_late_events_change_nothing()   // Review Focus 2: DELIVERED twice → one message; then TRANSIT → still delivered, warning log
[Fact] public async Task Returned_creates_a_confirmed_failure()        // returned_to_sender; failure kind returned_to_sender status confirmed
[Fact] public async Task Failure_flags_possibly_lost()                 // possibly_lost; failure possibly_lost, previous_status in_transit
[Fact] public async Task Possibly_lost_then_delivered_dismisses_automatically()   // §8: failure dismissed, dismissed_at; shipment delivered
[Fact] public async Task Possibly_lost_then_transit_restores_and_moves_on()      // Review Focus 2
[Fact] public async Task F08_no_tracking_for_seven_days_flags_possibly_lost()
// clock +7 days −1 min → RunPossiblyLostAsync → unchanged; +2 min → possibly_lost, failure row, error log; possibly_lost_open set by monitor later (Task 11)
[Fact] public async Task Dismissed_shipment_waits_seven_days_from_dismissal()     // flagged at day 7, dismissed (DismissAsync) at day 8 → sweep at day 14 no flag; day 15+ → flagged again, same row, previous_status set anew
[Fact] public async Task Webhook_guards()
// wrong secret → 404 bad_secret; GET → 405; 70 KB body → 413; no tracking_number → 400 invalid; unknown number → 200 unknown; fake 500 → 500 error
[Fact] public async Task Ship_back_follows_tracking_only()               // seeded ship_back handed_over → TRANSIT, DELIVERED; no messages, no coverage change
```

- [ ] **Step 2: Run** `--filter-class '*TrackingTests'` — FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** — PASS.
- [ ] **Step 5: Commit** — `git commit -m "feat(fulfillment): Shippo webhook, tracking and possibly-lost sweep"`

---

### Task 8: Product returns (request, label, receive, inspect, lists, expiry)

**Files:**
- Create: `Services/CustomerService.cs`, `Services/WarehouseService.Returns.cs`
- Modify: `Protos/fulfillment.proto` (+`FulfillmentCustomer` `RequestReturn`, `ListMyReturns`; Warehouse `ListReturns`, `ReceiveReturn`, `RecordInspection`), `LabelWorker.cs` (return labels), `ShippoWebhook.cs` (return shipments), `Sweeps.cs` (expiry)
- Test: `ReturnTests.cs`

**Interfaces:**
- Produces:
  - Proto: `ReturnRequest { string id; string order_id; string status; google.protobuf.Timestamp expires_at; string label_url; int64 version; repeated ReturnUnit units; google.protobuf.Timestamp created_at; }`, `ReturnUnit { int32 unit_no; int32 line_no; string received_serial; string inspection_result; string inspection_notes; string decision; int64 refund_cents; int64 refund_tax_cents; }`.
  - `RequestReturn { order_id, repeated ReturnLine units /* {int32 line_no, int32 quantity} */ } → ReturnRequest` (§6.4, spec §4, owner item 2). Customer only.
    1. Lock `order_accounts`. Not the caller's order, or unknown → `NOT_FOUND`.
    2. `delivered_at` null or `now > delivered_at + 14 days` → `RETURN_WINDOW_CLOSED`.
    3. `units`: non-empty, line exists, `quantity > 0`, no duplicate `line_no`, not a plan/product mix → else `VALIDATION_FAILED units`.
    4. Per line: `quantity ≤ quantity − refunded_units − units in open requests` (`requested` … `inspected`) → else `RETURN_QUANTITY_EXCEEDED`.
    5. Product request: `requested`, `expires_at = now + 14 days`, units numbered 1… in request order (one per unit), and a `return` shipment (`label_pending`, `standard`, linked both ways). Publish `ReturnRequestedV1`.
    6. Plan-only requests: Task 9 (until then `VALIDATION_FAILED units`).
  - Label worker: return labels per R14 (`requested → label_issued`, `ReturnLabelIssuedV1(orderId, returnId, labelUrl)`).
  - Webhook, return shipments: `TRANSIT` shipment `label_ready → in_transit` and request `label_issued → in_transit`; `DELIVERED` shipment `→ delivered`, request unchanged; else `last_tracking_at` only.
  - `ReceiveReturn { return_id } → ReturnRequest`: `label_issued|in_transit → received`, publish `ReturnReceivedV1`, audit; else `INVALID_RETURN_STATE`.
  - `RecordInspection { return_id, repeated InspectionUnit units /* {unit_no, received_serial, result, notes} */ } → ReturnRequest` (spec §7):
    - The request must be `received`, else `INVALID_RETURN_STATE`.
    - Every unit exactly once, `result` valid, `notes` ≤ 500. Serialized lines need a valid serial; non-serialized lines take none. Otherwise `VALIDATION_FAILED units`.
    - A serial not in any `pack_task_serials` of this order → stored with `result = wrong_item`.
    - `→ inspected`, audit.
  - `ListReturns { optional status; page; page_size } → { repeated ReturnRequest items; }`: oldest first; `ReturnViewers`. `ListMyReturns { page; page_size }`: the caller's, newest first.
  - `Sweeps.RunReturnExpiryAsync(CancellationToken)` (spec §7): per request `requested|label_issued` past `expires_at`, one transaction:
    1. Lock the shipment.
    2. Guarded request `→ expired`.
    3. Shipment per R15.
    4. Publish `ReturnClosedV1(…, "expired")`.
    Runs hourly with the possibly-lost sweep.
  - Seed: `Seed.DeliveredAsync(t, OrderReadyToFulfillV1 order, params string[] serials) → (Guid TaskId, Guid ShipmentId)` (handover + webhook `DELIVERED`); `Seed.ReturnAsync(t, Guid orderId, TestCaller customer, params (int LineNo, int Qty)[] units) → Guid returnId`; `Seed.InspectedReturnAsync(` same parameters `) → Guid returnId` (request, label worker, receive, inspect all `ok` with the shipped serials by `seq`).

- [ ] **Step 1: Write the failing tests** in `ReturnTests.cs`

```csharp
[Fact] public async Task Request_creates_units_and_a_return_label()
// 2 units of line 1 → requested, units 1..2, expires +14 d; ReturnRequestedV1; RunOnce → label_issued, fake shipment from ship_to to warehouse is_return, ReturnLabelIssuedV1 with label url
[Fact] public async Task Window_and_ownership()
// not delivered → RETURN_WINDOW_CLOSED; delivered +14 d +1 s → RETURN_WINDOW_CLOSED; other customer → NOT_FOUND (Review Focus 4); staff caller → ROLE_NOT_ALLOWED
[Fact] public async Task Quantity_counts_open_requests()          // request 2 → second request 1 → RETURN_QUANTITY_EXCEEDED; after expiry the units are free again
[Fact] public async Task Mixed_plan_and_product_is_VALIDATION_FAILED()   // owner item 2: [line 1, line 2] → units
[Fact] public async Task Return_tracking_receive_and_inspect()
// TRANSIT → request in_transit; ReceiveReturn → received, ReturnReceivedV1, audit; RecordInspection → inspected; wrong-order serial stored wrong_item
[Fact] public async Task Inspection_must_cover_every_unit()       // one unit missing / twice → VALIDATION_FAILED units; serialized without serial → units
[Fact] public async Task Receive_in_wrong_state_is_INVALID_RETURN_STATE()
[Fact] public async Task F15_return_request_expires()
// label_issued; clock +14 d +1 h → RunReturnExpiryAsync → expired, shipment void_pending → RunOnce voided (fake refund), ReturnClosedV1(expired)
[Fact] public async Task Expiry_before_label_voids_nothing_to_buy()  // Review Focus 5: requested + label_pending → expired, shipment voided, fake 0 transactions
[Fact] public async Task Lists_page_and_filter()                   // ListReturns status filter oldest first, support allowed, customer refused; ListMyReturns only own, newest first
```

- [ ] **Step 2: Run** `--filter-class '*ReturnTests'` — FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** — PASS.
- [ ] **Step 5: Commit** — `git commit -m "feat(fulfillment): return requests, labels, receipt, inspection and expiry"`

---

### Task 9: Refunds — DecideReturn, plan-only returns, ship-back, refund saga consumers

**Files:**
- Create: `Domain/RefundEngine.cs`, `Services/SupportService.cs`, `Consumers/RefundCompletedConsumer.cs`, `Consumers/RefundFailedConsumer.cs`
- Modify: `Protos/fulfillment.proto` (+`FulfillmentSupport.DecideReturn`), `CustomerService.cs` (plan-only), `FulfillmentApp.cs` (two consumers on `ConsumerBinding.Event("payment", …)`)
- Test: `RefundTests.cs`

**Interfaces:**
- Produces:
  - `static class RefundMath { (long Cents, long Tax) UnitShare(long paidCents, long taxCents, int quantity, int refundedUnitsBefore); long PartialTax(long partial, long share, long unitTax); }`. Owner item 1: a unit with `refundedUnitsBefore + 1 == quantity` gets `paid − (quantity − 1) × floor(paid / quantity)`, others `floor(paid / quantity)`; tax the same. `PartialTax` per R18.
  - `RefundEngine` (scoped):
    - `Task<OrderAccount> LockOrderAsync(Guid orderId, CancellationToken ct)`: `FOR UPDATE`.
    - `Task ApplyAsync(Guid orderId, IReadOnlyDictionary<int, (int Units, long Cents)> perLine, CancellationToken ct)`: guarded line updates in ascending `line_no` (R18).
    - `Task<long> ShippingIfCompleteAsync(OrderAccount a, CancellationToken ct)`: step 4. All product lines have `refunded_units = quantity` and `!shipping_refunded` → set it, return `shipping_cents`; else 0.
    - `Guid? Send(Guid orderId, Guid sagaRef, string sagaType, string reason, long amount, IReadOnlyList<RefundLineV1> lines)`: amount 0 → null (owner item 4). Else writes `RefundPaymentV1` (`SendCommand("payment", …)`, `refundId` = UUIDv7) and the saga row (R19), and adds to `refund_amount_cents_total{reason}`.
  - `DecideReturn { return_id, repeated UnitDecision units /* {unit_no, decision, int64 partial_cents} */ } → DecideReturnResponse { string return_id; string refund_id; int64 amount_cents; repeated RefundLine lines; }`, `RefundLine { int32 line_no; int64 amount_cents; }`. `Deciders`; `IdempotencyGuard.ExecuteAsync`. Steps:
    1. Validate every unit exactly once with a valid decision. `partial` needs `partial_cents > 0`; `full` / `reject` need 0. Otherwise `VALIDATION_FAILED units`.
    2. Versionless guarded `inspected → decided`. 0 rows → `INVALID_RETURN_STATE` or `NOT_FOUND`.
    3. Lock the order (`LockOrderAsync`).
    4. Per unit, in `unit_no` order: share from `RefundMath` with `refundedUnitsBefore` = line value + units of this decision so far. A partial above the share → `PARTIAL_EXCEEDS_SHARE`. Store `decision`, `refund_cents`, `refund_tax_cents` on the unit.
    5. Plans (WAR-R05): an accepted unit whose `received_serial` has an `active|pending_delivery` coverage on this order adds one unit of that plan line at its share. The coverage → `cancelled`. A `return_units` row is added for the plan unit (`decision = full`).
    6. `ApplyAsync`, then `ShippingIfCompleteAsync`.
    7. Outbox:
       - `Send(…, return_id, return_refund, "return", total, lines)`.
       - `ReceiveCustomerReturnV1(orderId, accepted product units)`: serialized → `(sku, received_serial, 1)` except `wrong_item` (owner item 3, warning log); non-serialized → grouped `(sku, null, n)`. Not sent if empty.
       - `ReturnDecidedV1(orderId, returnId, accepted per line incl. plans, total)` and `ReturnClosedV1(…, "decided")`.
    8. Rejected units → one `ship_back` shipment (`label_pending`, `standard`, `return_request_id`). Audit `fulfillment.decide-return`.
  - Plan-only `RequestReturn` (WAR-R06, spec §7), same order lock as Task 8. The request is inserted already `decided`; plan units `decision = full` at their share.
    - Per unit, cancel the plan line's coverage on the **last** device serial by `seq` that still has an `active|pending_delivery` coverage.
    - `ApplyAsync`, `ShippingIfCompleteAsync`, `Send(…, "return")`.
    - Publish `ReturnRequestedV1`, `ReturnDecidedV1`, `ReturnClosedV1(decided)`. No label, no inspection.
  - `RefundCompletedConsumer` (`refund-completed`): saga by `refund_id` → `pending_steps = {}`, `completed_at`. Unknown → no-op. `RefundFailedConsumer` (`refund-failed`): saga unchanged, error log with `failureReason`. Unknown → no-op.

- [ ] **Step 1: Write the failing tests** in `RefundTests.cs` (support decides; fresh key per call unless stated)

```csharp
[Fact] public void Unit_share_math()                                      // (1001,0,2,0)→500; (1001,0,2,1)→501; owner item 1: after a partial the last unit still gets 501
[Fact] public async Task F09_two_units_of_a_1001_cent_line_returned_separately()   // 500 then 501; line refunded 1001, units 2; RefundPaymentV1 reason "return", lines [{1, 500}] then [{1, 501}]
[Fact] public async Task F10_partial_300_on_a_1000_share_with_80_tax()           // line paid 2000 tax 160 qty 2 → unit refund_cents 300, refund_tax_cents 24
[Fact] public async Task Partial_above_share_is_PARTIAL_EXCEEDS_SHARE()           // nothing written; key stores the rejection
[Fact] public async Task F11_full_return_of_all_units_adds_the_express_fee_once()
// express 1500: return unit 1 → no fee; return unit 2 (+ plan via WAR-R05) → amount includes 1500, shipping_refunded; a later plan-only request → no fee again
[Fact] public async Task Partial_device_refund_refunds_its_plan_in_full()         // §8 / WAR-R05: coverage cancelled; plan line refunded 9900; return_units has the plan unit; accepted [{1,1},{2,1}]
[Fact] public async Task Wrong_item_rejected_is_shipped_back()                    // §8: reject → no refund command (owner item 4), refund_id ""; ship_back label_pending; ReturnDecidedV1 refundCents 0
[Fact] public async Task Accepted_wrong_item_is_left_out_of_the_inventory_command()   // owner item 3
[Fact] public async Task F14_decide_retried_with_the_same_key_sends_one_refund()  // same key twice → same response, one RefundPaymentV1, one saga; other body same key → IDEMPOTENCY_KEY_REUSED
[Fact] public async Task Decide_writes_command_saga_and_lines_in_one_transaction()   // after success: saga return_refund with refund_id, pending {refund}; ReceiveCustomerReturnV1 units; ReturnClosedV1(decided); audit
[Fact] public async Task Concurrent_decisions_on_one_line_refund_500_and_501()    // Review Focus 1: two returns of 1 unit each, decided in parallel ×20 rounds → {500, 501}; no 40P01
[Fact] public async Task Plan_only_request_is_decided_at_once()
// 2-unit device with 2 plans: plan-only qty 1 → decided, refund plan share, coverage of the LAST serial by seq cancelled; RET-R01 window applies
[Fact] public async Task Plan_alone_after_window_is_RETURN_WINDOW_CLOSED()          // §8
[Fact] public async Task Refund_completed_closes_the_saga_and_failed_keeps_it_open()   // completed → pending {} + completed_at; failed → unchanged + error log; unknown refundId → no-op
[Fact] public async Task Ship_back_handover()                                    // ship_back label_ready → HandOver → handed_over; no ShipmentShippedV1
```

- [ ] **Step 2: Run** `--filter-class '*RefundTests'` — FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** — PASS.
- [ ] **Step 5: Commit** — `git commit -m "feat(fulfillment): return decisions, refund amounts and refund saga"`

---

### Task 10: Delivery failures (returned to sender, confirm lost, dismiss, list) and reships

**Files:**
- Create: `Services/WarehouseService.Failures.cs`
- Modify: `Protos/fulfillment.proto` (+Warehouse `RecordReturnedToSender`; Support `ConfirmLost`, `DismissPossiblyLost`, `ListDeliveryFailures`), `SupportService.cs`, `RefundEngine.cs` (`RefundRemainingAsync`), `WarehouseService.Packing.cs` (reship coverage move), `ShippoWebhook.cs` (reship delivery)
- Test: `DeliveryFailureTests.cs`

**Interfaces:**
- Produces:
  - Proto: `DeliveryFailure { string shipment_id; string order_id; string kind; string status; string previous_status; string reship_id; google.protobuf.Timestamp created_at; }`.
  - `RecordReturnedToSender { shipment_id, repeated RtsUnit units /* {serial, sku_id, outcome} */ } → DeliveryFailure`. `Packers`; **`Idempotency-Key` required** (spec §4), `ExecuteAsync`. Steps:
    1. The failure must be `kind = returned_to_sender`, `status = confirmed`, else `INVALID_STATE`.
    2. Units must match what shipped exactly (spec §7), else `VALIDATION_FAILED units`: each shipped serial once, non-serialized count per SKU, `outcome ∈ sealed|failed`.
    3. Lock the order. `RefundEngine.RefundRemainingAsync(OrderAccount a, CancellationToken ct) → Task<(long Amount, IReadOnlyList<RefundLineV1> Lines)>` (new here; spec §7 ORD-R17 formula: every line with a remainder becomes fully refunded, shipping added if not yet refunded), then `Send(…, shipment_id, delivery_failure, "delivery_failure", …)`.
    4. The order's coverages → `cancelled`. `ReceiveReturnedToSenderV1(task.ref_id, units)`, with non-serialized units grouped by `(sku, outcome)`. Publish `DeliveryFailedV1(orderId, "returned_to_sender")`.
    5. Failure → `resolved_refund`, `rts_outcomes`, `decided_by`. Audit.
  - `ConfirmLost { shipment_id, resolution } → DeliveryFailure`. `Deciders`; key required. The failure must be `kind = lost`, `status ∈ possibly_lost|confirmed`, else `INVALID_STATE`.
    - `refund` (`ExecuteAsync`): shipment `→ lost`; `MarkLostInTransitV1(task.ref_id)`; refund as above; coverages cancelled; `DeliveryFailedV1(orderId, "lost")`; failure `resolved_refund`.
    - `reship` (`ExecuteWithProviderAsync`):
      - Provider: validate state (no lock), then `CommitForReshipAsync(Ids.ReshipId(shipmentId), orderId, product lines of the task)`; `SOLD_OUT` → `RESHIP_NO_STOCK`.
      - Apply: guarded failure `→ resolved_reship` with `reship_id`; shipment `→ lost`; `MarkLostInTransitV1(task.ref_id)`; new pack task (`kind = reship`, `ref_id = reship_id`, same lines and `ship_to`, `pending`). No refund, no `delivery-failed`.
    - Audit `fulfillment.confirm-lost` (details: resolution).
  - `DismissPossiblyLost { shipment_id } → DeliveryFailure`: failure `possibly_lost` else `INVALID_STATE`; `Failures.DismissAsync`; audit.
  - `ListDeliveryFailures { optional status; page; page_size }`: oldest first.
  - Reship packing (spec §7): `CompletePacking` of a reship task creates no coverages. For each `pending_delivery` coverage of the order, find its serial's rank by `seq` within its device line in the task that holds it. `serial` becomes the reship task's serial at that rank in the same line.
  - Reship delivery (spec §7): `DELIVERED` on a reship's outbound shipment behaves as Task 7 (sets `delivered_at`, activates coverages, `ShipmentDeliveredV1` with `refId = reship_id`).

- [ ] **Step 1: Write the failing tests** in `DeliveryFailureTests.cs`

```csharp
[Fact] public async Task F12_returned_to_sender_one_sealed_one_failed()
// RETURNED → RecordReturnedToSender [A sealed, B failed] → ReceiveReturnedToSenderV1 with both outcomes; RefundPaymentV1 = total (incl. express), reason delivery_failure, every line;
// coverages cancelled; DeliveryFailedV1(returned_to_sender); failure resolved_refund; saga delivery_failure
[Fact] public async Task Returned_to_sender_after_a_partial_earlier_refund_refunds_the_remainder()   // §8: seed line refunded 500 → amount = total − 500
[Fact] public async Task Returned_to_sender_units_must_match()   // missing serial, extra serial, wrong count → VALIDATION_FAILED units; no key → IDEMPOTENCY_KEY_REQUIRED
[Fact] public async Task Confirm_lost_refund()                    // possibly_lost → lost; MarkLostInTransitV1(refId); full refund; DeliveryFailedV1(lost); resolved_refund
[Fact] public async Task F13_confirm_lost_reship_with_stock()
// CommitForReship(reship_id = v5(shipment), order, product lines); reship task pending, ref reship_id, same lines; resolved_reship; no refund, no delivery-failed;
// pack the reship → coverage moves to the new serial at the same rank; deliver it → coverage active, ShipmentDeliveredV1 refId = reship_id, order delivered_at set
[Fact] public async Task Reship_without_stock_is_RESHIP_NO_STOCK()   // §8: SoldOut → RESHIP_NO_STOCK; key released; then ConfirmLost(refund) with a new key → ok
[Fact] public async Task Confirm_lost_retried_with_the_same_key()    // reship twice → one task, CommitForReship called once
[Fact] public async Task Dismiss_restores_the_previous_status()     // possibly_lost (from in_transit) → in_transit; failure dismissed; again → INVALID_STATE
[Fact] public async Task Failure_lists_and_roles()                  // ListDeliveryFailures status filter; warehouse → ROLE_NOT_ALLOWED; RecordReturnedToSender support → ROLE_NOT_ALLOWED
```

- [ ] **Step 2: Run** `--filter-class '*DeliveryFailureTests'` — FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** — PASS.
- [ ] **Step 5: Commit** — `git commit -m "feat(fulfillment): delivery failures, lost confirmations and reships"`

---

### Task 11: Coverage lookup, account deletion, daily label check, monitor, worker mode, full verification

**Files:**
- Create: `Consumers/AccountDeletedConsumer.cs`, `Worker/FulfillmentMonitor.cs`
- Modify: `Protos/fulfillment.proto` (+`GetCoverage` on `FulfillmentCustomer` and `FulfillmentSupport`), `CustomerService.cs`, `SupportService.cs`, `Sweeps.cs` (daily label check), `FulfillmentApp.cs` (consumer `account-deleted` on `ConsumerBinding.Event("identity", IdentityMessages.AccountDeleted)`; worker: `FulfillmentMonitor`; API: `MapBloomDeadLetterAdmin()`)
- Test: `CoverageTests.cs`, `WorkerTests.cs`

**Interfaces:**
- Produces:
  - `GetCoverageRequest { string serial } → Coverage { string serial; string order_id; string status; int32 coverage_months; google.protobuf.Timestamp starts_at; google.protobuf.Timestamp ends_at; }`. The `active|pending_delivery` row, else the newest `cancelled`; none → `NOT_FOUND`. Customer: the coverage's order must be theirs, else `NOT_FOUND`. Staff: `Deciders`.
  - `AccountDeletedConsumer` (R20): unknown account → no-op.
  - `Sweeps.RunLabelCheckAsync(CancellationToken)`, daily (`PeriodicTimer(24 h)`):
    1. `ListLabelsAsync(now − 48 h)`, skipping labels younger than 10 min.
    2. Keep a label iff a shipment row with its metadata id has that `shippo_transaction_id`.
    3. Else `VoidLabelAsync`, `labels_total{voided}`, and an error log `"Orphan or duplicate Shippo label"` (FR-05).
  - `FulfillmentMonitor` (R22).

- [ ] **Step 1: Write the failing tests**

`CoverageTests.cs`:
```csharp
[Fact] public async Task Customer_sees_own_coverage_by_serial()       // " b " → B pending_delivery; after delivery active with dates
[Fact] public async Task Other_customers_serial_is_NOT_FOUND()        // Review Focus 4: same body as unknown serial
[Fact] public async Task Cancelled_coverage_is_shown_when_no_open_one()
[Fact] public async Task Staff_lookup_needs_support_or_admin()        // warehouse → ROLE_NOT_ALLOWED
[Fact] public async Task Account_deleted_anonymizes_ship_to()         // name, lines, phone "[deleted]"; city/state/zip, tracking numbers, amounts kept; unknown account → no-op
[Fact] public async Task Daily_check_voids_orphan_and_duplicate_labels()
// fake AddLabel("bloom:<unknown id>") and a second label for a real shipment, both older than 10 min → both voided, error logs; the shipment's own label kept; a 5-min-old orphan kept
```

`WorkerTests.cs` (API host + worker host on one database):
```csharp
[Fact] public async Task Four_queues_are_bound()
// fulfillment.ready-to-fulfill ← order.events, fulfillment.refund-completed / refund-failed ← payment.events, fulfillment.account-deleted ← identity.events
[Fact] public async Task Ready_to_fulfill_through_the_broker_creates_one_task()   // same envelope twice → one task
[Fact] public async Task Commands_reach_inventory_and_payment_exchanges()
// decide a return end-to-end; test queues on inventory.commands and payment.commands receive receive-customer-return.v1 and refund-payment.v1 (spec §4 testing)
[Fact] public async Task Events_reach_fulfillment_events()                         // shipment-shipped.v1 and identity.staff-action-recorded.v1 on fulfillment.events
[Fact] public async Task Monitor_sets_gauges_and_reports_stuck_sagas()
// pack_tasks_by_status, returns_by_status, possibly_lost_open 1; open saga +31 min → one error log
[Fact] public async Task Dlq_admin_is_hosted()                                     // GET /api/admin/dlq/fulfillment/queues (x-bloom-role "Admin") → four .dlq queues
[Fact] public async Task Worker_runs_label_worker_sweeps_and_monitor()             // hosted services: worker has them, API doesn't
```

- [ ] **Step 2: Run** `--filter-class '*CoverageTests'` and `'*WorkerTests'` — FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** — PASS. Then the whole suite: `dotnet build -c Release` (0 warnings) and `dotnet test` (all projects) — PASS. ID coverage: `grep -rhoE 'F(0[1-9]|1[0-5])_' src/Fulfillment/Bloom.Fulfillment.Tests | sort -u` lists F01_ … F15_. `docker ps -a` shows no leftover test containers.
- [ ] **Step 5: Commit** — `git commit -m "feat(fulfillment): coverage lookup, account deletion, label check and worker mode"`

---

## Proposed doc edits (not applied; in addition to spec §9)

10. 05-fulfillment §6.4.1 step 1: the last unit's share is `paid_cents − (quantity − 1) × floor(paid_cents / quantity)` (owner item 1).
11. 05-fulfillment §6.4 `RequestReturn`: a request is plan-only or product-only (owner item 2; replaces spec §9 item 8).
12. 05-fulfillment §6.4.1 step 6: accepted `wrong_item` serialized units are not sent to inventory (owner item 3).
13. 05-fulfillment §6.4.1 / §6.5: no refund command and no saga when the amount is 0 (owner item 4).
14. 05-fulfillment §5.3 `packing-started`: `kind = order` tasks only (owner item 5).
