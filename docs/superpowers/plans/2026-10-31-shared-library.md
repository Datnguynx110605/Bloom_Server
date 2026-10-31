# Shared Library and Repo Foundation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the .NET 10 monorepo skeleton, the shared reliability library (00-reliability §4–§13 incl. DLQ admin §7.7), the shared OpenTelemetry setup, the dev Docker Compose stack and a build+test CI workflow.

**Architecture:** One class library `Bloom.Shared` (folders per module) plus one xUnit v3 test project using Testcontainers (one PostgreSQL + one RabbitMQ per run). Services call `AddBloom<TDbContext>()` once; workers add `.AddWorker()`; APIs map `MapBloomDeadLetterAdmin()`. The library joins the caller's EF Core `DbContext`; all money/counter/queue SQL is raw SQL through EF.

**Tech Stack:** .NET 10, EF Core 10 + Npgsql, RabbitMQ.Client 7 (async API), Grpc.AspNetCore, Google.Protobuf, Grpc.StatusProto, Google.Api.CommonProtos, OpenTelemetry .NET, xUnit v3, Testcontainers for .NET, GitHub Actions, Docker Compose.

**Spec:** `docs/superpowers/specs/2026-10-31-shared-library-design.md` (approved). Design source: `doc/low-level-design/00-reliability.md` (cited as `§x`), LLD-ADR-001/002/003/009, HLD §9.3/§9.5.

## Global Constraints

- SDK pinned by `global.json`: `{"sdk":{"version":"10.0.100","rollForward":"latestFeature"}}`. `TargetFramework` `net10.0`, `Nullable` enable, `ImplicitUsings` enable, `TreatWarningsAsErrors` true (in `Directory.Build.props`).
- Central package management (`Directory.Packages.props`, `ManagePackageVersionsCentrally=true`). Latest stable version compatible with .NET 10 for every package; add with `dotnet add <proj> package <id>` (writes the version centrally).
- Table, column, queue, exchange, header names and the retry schedule are exactly §4, §5.1, §5.2, §6, §7.3. No other names.
- No domain logic in the library (§1). Handlers never publish to RabbitMQ; only the relay, consumer host (retry/DLQ) and DLQ admin publish, always with publisher confirms and `delivery_mode = 2` (LLD-ADR-002).
- No behaviour, endpoints, fields, headers or options the docs/spec don't describe (AGENTS.md §1). Never create/edit anything under `doc/`.
- All time comparisons in SQL use the database `now()` (§10).
- Docker runs via Colima: never run `colima`. If the Docker daemon is unreachable, stop and report. Never `docker compose up`, never `dotnet run`. Compose is validated with `docker compose config` only. Test containers must be gone after each run (`docker ps -a` shows none from the run).
- Every test case T-01 … T-24 has at least one test whose method name starts with its ID (`T01_…`).
- Test names below are binding; assertion lists are the minimum.

## Review Focus

1. **Relay publishes to a missing exchange** (broker closes the channel with 404): the batch's unconfirmed rows get `attempts + 1` and a `last_error`, stay `pending`, and the relay opens a new channel and publishes the next valid row. Test in Task 6.
2. **Malformed deliveries** (non-JSON body, envelope missing `messageId`, unknown `messageType`, payload missing a required field): straight to the DLQ with `x-failure-reason`, original ACKed, no inbox row, handler never called (§10). Tests in Task 7.
3. **Idempotency-Key header edge forms** (37+ chars, `{braced}` GUID, non-hex, empty string; uppercase vs lowercase of the same UUID): malformed ones rejected before any row exists; case variants are the same key. Tests in Task 8.
4. **Broker connection dropped while the worker runs**: consumers and the relay reconnect on their own and keep processing without a restart. Test in Task 7.
5. **DLQ operation on an empty DLQ or with dead letters arriving mid-pass**: returns promptly (no hang waiting for messages); only messages present at the start are touched; late arrivals stay. Tests in Task 10.

## Rulings in this plan (builder's calls the spec left open)

- **Ruling R1 (needs owner yes, spec §6.9):** no interceptor. Library code throws `RpcException` that already carries `google.rpc.Status` + `ErrorInfo{domain="bloom"}` (built by `BloomErrors`). An interceptor would have nothing to convert, and the idempotency rule "classified by gRPC status, any non-`RpcException` released" then applies literally.
- **Ruling R2 (needs owner yes, §7.7):** `PeekDeadLetters.limit` unset (0) → 20; values outside 1–100 are clamped into 1–100 (no new error reason).
- **Ruling R3 (needs owner yes, §7.6):** optimistic-lock `NOT_FOUND` carries a reason supplied by the calling service (the docs define none in the library), like `FAILED_PRECONDITION`.
- R4: idempotency `operation` = `ServerCallContext.Method` without its leading `/` (e.g. `bloom.order.v1.OrderService/StartCheckout`); the key is stored in canonical lowercase `D` form, so case variants are one key.
- R5: `{service}` mismatch on DLQ admin → `NOT_FOUND` / `DLQ_NOT_FOUND`. Role check runs first.
- R6: a DLQ registered in the catalog but not yet declared on the broker is listed with count 0; any other operation on it → `DLQ_NOT_FOUND`.
- R7: RabbitMQ automatic recovery is off. The library reconnects itself: one shared connection re-created when closed; every consumer and the relay re-open their channel after a failure (1 s pause). A retry/DLQ publish failure in the consumer host closes that consumer's channel without ACK, so the broker redelivers the original (the §7.3 crash path).
- R8: `occurredAt` = `DateTime.UtcNow` when the writer is called (inside the caller's transaction); JSON writes it as RFC 3339 with `Z`.
- R9: consumer prefetch 16. Gauges (`outbox_pending_count`, `outbox_oldest_pending_age_seconds`, `dlq_depth`) are polled every 15 s by the worker (DB query + passive queue declare).
- R10: metric `service` label is attached by the library to every measurement; the collector's Prometheus exporter runs with `add_metric_suffixes: false`, so Prometheus names equal §12.

---

## File Structure

```
global.json · Directory.Build.props · Directory.Packages.props · Bloom.slnx · .gitignore
.github/workflows/ci.yml
deploy/compose/docker-compose.yml, otel-collector.yaml, tempo.yaml, prometheus.yml, loki.yaml, grafana/datasources.yaml
src/Shared/Bloom.Shared/
  Bloom.Shared.csproj
  BloomOptions.cs, BloomServiceCollectionExtensions.cs (AddBloom, BloomBuilder)
  Messaging/   MessageNames.cs, Envelope.cs, BloomJson.cs, OutboxWriter.cs, MessageScope.cs,
               RabbitMqConnection.cs, ConsumerBinding.cs, ConsumerCatalog.cs, IMessageHandler.cs,
               PermanentFailureException.cs, TopologyInitializer.cs, OutboxRelay.cs, ConsumerHost.cs
  Persistence/ OutboxMessage.cs, InboxMessage.cs, IdempotencyKey.cs, ModelBuilderExtensions.cs
  Idempotency/ IdempotencyGuard.cs
  Concurrency/ ConcurrentUpdates.cs
  Errors/      BloomErrors.cs
  Retention/   RetentionPurger.cs
  Observability/ BloomMetrics.cs, BloomActivity.cs, GaugePoller.cs, BloomTelemetryExtensions.cs
  DeadLetters/ Protos/dead_letter_admin.proto, DeadLetterAdminService.cs, DeadLetterPass.cs, StaffActionRecorded.cs
src/Shared/Bloom.Shared.Tests/
  Infrastructure/ Containers.cs, TestDbContext.cs, TestApp.cs, FakeServerCallContext.cs, Rabbit.cs
  Protos/test.proto
  <Module>Tests.cs per task
```

---

### Task 1: Repo skeleton, CI, message contracts

**Files:**
- Create: `global.json`, `Directory.Build.props`, `Directory.Packages.props`, `Bloom.slnx`, `.gitignore` (bin/, obj/, TestResults/, *.user, .vs/, .idea/)
- Create: `src/Shared/Bloom.Shared/Bloom.Shared.csproj` (classlib, `<FrameworkReference Include="Microsoft.AspNetCore.App" />`, `InternalsVisibleTo Bloom.Shared.Tests`)
- Create: `src/Shared/Bloom.Shared.Tests/Bloom.Shared.Tests.csproj` (xunit.v3, xunit.runner.visualstudio, Microsoft.NET.Test.Sdk)
- Create: `src/Shared/Bloom.Shared/Messaging/MessageNames.cs`, `Envelope.cs`, `BloomJson.cs`
- Create: `.github/workflows/ci.yml`
- Test: `src/Shared/Bloom.Shared.Tests/MessageContractTests.cs`

**Interfaces:**
- Produces:
  - `public static class MessageKinds { public const string Event = "event"; public const string Command = "command"; }`
  - `public static class MessageNames` — `string EventExchange(string producer)`, `string CommandExchange(string target)`, `string ConsumerQueue(string service, string handler)`, `string RetryQueue(string queue, string delay)`, `string DeadLetterQueue(string queue)`, `IReadOnlyList<(string Delay, int Milliseconds)> RetryDelays`, `bool IsValidMessageType(string messageType)`.
  - `public sealed record Envelope(Guid MessageId, string MessageType, string Kind, string Producer, DateTime OccurredAt, Guid CorrelationId, JsonElement Payload, Guid? CausationId = null)` with `byte[] ToUtf8Bytes()`, `static Envelope Parse(ReadOnlySpan<byte> utf8)` (throws `JsonException`), `T GetPayload<T>()` (throws `PermanentFailureException` on `JsonException` — create the exception type here: `public sealed class PermanentFailureException(string message, Exception? inner = null) : Exception(message, inner)` in `Messaging/PermanentFailureException.cs`).
  - `internal static class BloomJson { public static JsonSerializerOptions Options }` — `JsonSerializerDefaults.Web`, `DefaultIgnoreCondition = WhenWritingNull`, `RespectRequiredConstructorParameters = true`, `RespectNullableAnnotations = true`, unknown members ignored (default).

- [ ] **Step 1: Create the skeleton** — `dotnet new sln --format slnx -n Bloom`, the two projects under `src/Shared/`, added to the solution with solution folder `src/Shared`; props files per Global Constraints.

- [ ] **Step 2: Write the failing tests** in `MessageContractTests.cs`

```csharp
[Fact] public void Names_follow_5_1()
{
    Assert.Equal("order.events", MessageNames.EventExchange("order"));
    Assert.Equal("inventory.commands", MessageNames.CommandExchange("inventory"));
    Assert.Equal("inventory.order-paid", MessageNames.ConsumerQueue("inventory", "order-paid"));
    Assert.Equal("inventory.order-paid.retry.30s", MessageNames.RetryQueue("inventory.order-paid", "30s"));
    Assert.Equal("inventory.order-paid.dlq", MessageNames.DeadLetterQueue("inventory.order-paid"));
    Assert.Equal(new[] { ("1s", 1000), ("5s", 5000), ("30s", 30000), ("2m", 120000) }, MessageNames.RetryDelays);
}
[Theory]
[InlineData("order.paid.v1", true)] [InlineData("inventory.release-reservation.v1", true)]
[InlineData("identity.staff-action-recorded.v12", true)]
[InlineData("Order.paid.v1", false)] [InlineData("order.paid", false)] [InlineData("order.v1", false)]
[InlineData("order.paid.v0", false)] [InlineData("order..paid.v1", false)]
public void Message_type_format(string type, bool valid) => Assert.Equal(valid, MessageNames.IsValidMessageType(type));

[Fact] public void Envelope_serializes_with_5_2_field_names()
// parse ToUtf8Bytes() as JsonDocument: property names exactly
// messageId, messageType, kind, producer, occurredAt, correlationId, payload; no causationId when null;
// occurredAt string ends with "Z"; with CausationId set, "causationId" is present.
[Fact] public void Envelope_ignores_unknown_fields()      // extra "foo": 1 parses fine (§5.3 rule 2)
[Fact] public void Envelope_missing_required_field_throws() // JSON without messageId → JsonException
[Fact] public void GetPayload_missing_required_field_is_permanent()
// record TestPayload(string Sku, int Quantity); payload {"sku":"a"} → PermanentFailureException
```

- [ ] **Step 3: Run** `dotnet test --filter FullyQualifiedName~MessageContractTests` — Expected: build fails (types missing).

- [ ] **Step 4: Implement** `MessageNames`, `Envelope`, `BloomJson`, `PermanentFailureException`. Message-type regex: `^[a-z][a-z0-9-]*(\.[a-z0-9]+(-[a-z0-9]+)*)+\.v[1-9][0-9]*$`, requiring at least producer + name + version.

- [ ] **Step 5: Run** the same filter — Expected: PASS.

- [ ] **Step 6: Write `.github/workflows/ci.yml`** — `on: [push, pull_request]`, one job on `ubuntu-latest`: `actions/checkout@v4`, `actions/setup-dotnet@v4` with `global-json-file: global.json`, `dotnet restore`, `dotnet build --no-restore -c Release`, `dotnet test --no-build -c Release`. No path filters (spec §8.2).

- [ ] **Step 7: Verify** `dotnet build -c Release` → 0 warnings, 0 errors.

- [ ] **Step 8: Commit** — `git add -A && git commit -m "feat(shared): repo skeleton, CI and message contracts"`

---

### Task 2: Persistence model, options, test infrastructure

**Files:**
- Create: `Persistence/OutboxMessage.cs`, `InboxMessage.cs`, `IdempotencyKey.cs`, `ModelBuilderExtensions.cs`
- Create: `BloomOptions.cs`, `BloomServiceCollectionExtensions.cs`
- Create tests: `Infrastructure/Containers.cs`, `Infrastructure/TestDbContext.cs`, `Infrastructure/TestApp.cs`, `SchemaTests.cs`
- Packages: `Npgsql.EntityFrameworkCore.PostgreSQL` (lib); `Testcontainers.PostgreSql` (tests)

**Interfaces:**
- Produces:
  - Entities (CLR names PascalCase, columns snake_case): `OutboxMessage { Guid Id; string MessageType; string Kind; string Exchange; string RoutingKey; string Payload; string Headers; string Status; int Attempts; string? LastError; DateTime CreatedAt; DateTime? SentAt }` (`Payload`/`Headers` mapped `jsonb`); `InboxMessage { string Consumer; Guid MessageId; string MessageType; DateTime ProcessedAt }`; `IdempotencyKey { Guid UserId; string Key; string Operation; byte[] RequestHash; string Status; DateTime LockedUntil; int? ResponseStatus; byte[]? ResponseBody; DateTime CreatedAt; DateTime? CompletedAt }`.
  - `public static ModelBuilder AddBloomReliabilityTables(this ModelBuilder mb)`.
  - `public sealed class BloomOptions { string ServiceName; string RabbitMqUri = "amqp://guest:guest@localhost:5672/"; }`
  - `public static BloomBuilder AddBloom<TDbContext>(this IServiceCollection services, Action<BloomOptions> configure) where TDbContext : DbContext` — builds options eagerly, throws `ArgumentException` unless `ServiceName` matches `^[a-z][a-z0-9-]*$`; registers `BloomOptions` singleton, `DbContext` scoped alias → `TDbContext`, `AddHttpContextAccessor()`. Later tasks add their registrations here.
  - `public sealed class BloomBuilder { public IServiceCollection Services { get; } }` (methods added by Tasks 5, 6).
  - Tests: `Containers` (xUnit v3 assembly fixture, `[assembly: AssemblyFixture(typeof(Containers))]`) with `Task<string> CreateDatabaseAsync()` (fresh database `t_<guid:N>`, returns its connection string). `TestDbContext` with `DbSet<Counter> Counters` (`counters`: `id text pk, held int, allocation int`), `DbSet<Item> Items` (`items`: `id uuid pk, name text, version bigint not null default 0`), `DbSet<Effect> Effects` (`effects`: `id bigint identity pk, consumer text, message_id uuid`), plus `AddBloomReliabilityTables()`. `TestApp.BuildAsync(Containers c, Action<BloomBuilder>? configure = null) → Task<IHost>`: new database, `EnsureCreatedAsync`, `AddDbContext<TestDbContext>`, `AddBloom<TestDbContext>(o => o.ServiceName = "t" + 8 hex chars)`; not started.

- [ ] **Step 1: Write the failing test** `SchemaTests.Tables_match_section_4` — after `EnsureCreated`, query `information_schema.columns` and `pg_indexes`; assert for each table the exact column set with `data_type` and `is_nullable` from §4.1–§4.3 (`uuid`, `text`, `jsonb`, `integer`, `bytea`, `timestamp with time zone`), defaults (`attempts` 0, `created_at`/`processed_at` `now()`), primary keys `(id)`, `(consumer, message_id)`, `(user_id, key)`, indexes: outbox `(id) WHERE status = 'pending'`, outbox `(sent_at) WHERE status = 'sent'`, inbox `(processed_at)`, idempotency `(created_at)`; check constraints restrict `outbox_messages.kind` to `event|command`, `status` to `pending|sent`, `idempotency_keys.status` to `in_progress|completed`.
- [ ] **Step 2: Write** `AddBloom_rejects_invalid_service_name` (`""`, `"Order"`, `"order service"` → `ArgumentException`).
- [ ] **Step 3: Run** `dotnet test --filter FullyQualifiedName~SchemaTests` — Expected: build fails.
- [ ] **Step 4: Implement** entities, `AddBloomReliabilityTables` (Fluent API, `HasFilter` for partial indexes, `HasCheckConstraint`, `HasDefaultValueSql("now()")`), options and `AddBloom`, then the test infrastructure. Postgres image: same exact tag as Compose in Task 11 (e.g. `postgres:17.6`; verify with `docker manifest inspect`).
- [ ] **Step 5: Run** — Expected: PASS. `docker ps -a` shows no leftover containers.
- [ ] **Step 6: Commit** — `feat(shared): reliability tables, AddBloom and test containers`

---

### Task 3: Errors, metrics, concurrency helpers

**Files:**
- Create: `Errors/BloomErrors.cs`, `Observability/BloomMetrics.cs`, `Concurrency/ConcurrentUpdates.cs`
- Modify: `BloomServiceCollectionExtensions.cs` (register `BloomMetrics` singleton, `ConcurrentUpdates` scoped)
- Packages: `Grpc.AspNetCore`, `Grpc.StatusProto`, `Google.Api.CommonProtos`
- Test: `ErrorTests.cs`, `ConcurrencyTests.cs`

**Interfaces:**
- Produces:
  - `public static class BloomErrors` — `const string Domain = "bloom"`; reason constants `ConcurrencyConflict = "CONCURRENCY_CONFLICT"`, `IdempotencyInProgress`, `IdempotencyKeyReused`, `IdempotencyKeyRequired`, `IdempotencyKeyInvalid`, `RoleNotAllowed`, `DlqBusy`, `DlqNotFound`, `DlqMessageNotFound` (values = §8 strings); `RpcException Create(StatusCode code, string reason, string message)` (packs `ErrorInfo{Reason, Domain}` into `Google.Rpc.Status.Details`, `.ToRpcException()`); `RpcException FailedPrecondition(string reason, string message)`.
  - `public sealed class BloomMetrics` — `const string MeterName = "Bloom.Shared"`; ctor `(IMeterFactory, BloomOptions)`; internal methods: `OutboxPublish(string result)`, `ConsumerMessage(string consumer, string result)`, `ConsumerHandle(string consumer, double seconds)`, `Idempotency(string operation, string result)`, `OptimisticConflict(string entity)`, `ConditionalRejection(string counter)`, `ReportOutboxBacklog(long count, double oldestAgeSeconds)`, `ReportDlqDepth(string queue, long depth)`. Instruments named exactly as §12; every measurement tagged `service = options.ServiceName`; `consumer_handle_seconds` histogram unit `s` with bucket advice `[0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10]`; the three gauges are `ObservableGauge`s reading the last reported values.
  - `public sealed class ConcurrentUpdates(DbContext db, BloomMetrics metrics)`:
    - `Task<bool> TryConditionalUpdateAsync(string counter, FormattableString sql, CancellationToken ct = default)` — runs the single guarded statement via `ExecuteSqlAsync`; true iff 1 row; 0 rows → `conditional_update_rejections_total{counter}` + false (§7.5).
    - `Task<long> UpdateVersionedAsync<T>(string entity, IQueryable<T> row, long expectedVersion, Action<UpdateSettersBuilder<T>> set, string notFoundReason, CancellationToken ct = default) where T : class` — one `ExecuteUpdateAsync` on `row.Where(version == expected)` that also sets `Version = Version + 1` (property `Version`, column `version`); 1 row → returns `expectedVersion + 1`; 0 rows and `row.AnyAsync()` → `optimistic_conflicts_total{entity}` + throw `ABORTED`/`CONCURRENCY_CONFLICT`; else throw `NOT_FOUND`/`notFoundReason` (R3). No retry (§7.6).

- [ ] **Step 1: Write the failing tests**

```csharp
// ErrorTests
[Fact] public void Create_carries_status_and_ErrorInfo()
// ex.StatusCode == Aborted; ex.GetRpcStatus()!.GetDetail<ErrorInfo>() has Reason "CONCURRENCY_CONFLICT", Domain "bloom"
[Fact] public void FailedPrecondition_uses_caller_reason()   // "SOLD_OUT", StatusCode.FailedPrecondition

// ConcurrencyTests (Containers fixture, TestApp)
[Fact] public async Task T17_fifty_concurrent_conditional_updates_on_one_unit()
// counters row (id "sku", held 0, allocation 1); 50 parallel scopes each run TryConditionalUpdateAsync("sku-held",
// $"UPDATE counters SET held = held + 1 WHERE id = 'sku' AND held + 1 <= allocation");
// exactly 1 true, 49 false; held == 1; MeterListener sees 49 conditional_update_rejections_total{counter="sku-held"}
[Fact] public async Task T18_stale_version_is_rejected()
// item version 0 → update with expected 0 succeeds (returns 1); second update with expected 0 → ABORTED / CONCURRENCY_CONFLICT;
// row name and version (1) unchanged by the stale call; optimistic_conflicts_total{entity="item"} == 1
[Fact] public async Task Versioned_update_on_missing_row_is_not_found()   // NOT_FOUND with the given reason "ITEM_NOT_FOUND"
```

- [ ] **Step 2: Run** `dotnet test --filter "FullyQualifiedName~ErrorTests|FullyQualifiedName~ConcurrencyTests"` — Expected: build fails.
- [ ] **Step 3: Implement** the three types and registrations.
- [ ] **Step 4: Run** — Expected: PASS.
- [ ] **Step 5: Commit** — `feat(shared): error helpers, metrics and concurrency helpers (T-17, T-18)`

---

### Task 4: Outbox writer

**Files:**
- Create: `Messaging/OutboxWriter.cs`, `Messaging/MessageScope.cs`, `Observability/BloomActivity.cs`
- Modify: `BloomServiceCollectionExtensions.cs` (register `OutboxWriter` scoped)
- Test: `OutboxWriterTests.cs`

**Interfaces:**
- Consumes: `BloomOptions`, `OutboxMessage`, `Envelope`, `MessageNames`.
- Produces:
  - `public sealed class OutboxWriter(DbContext db, BloomOptions options, IHttpContextAccessor http)`: `Guid PublishEvent<TPayload>(string messageType, TPayload payload)` (exchange `<ServiceName>.events`, kind `event`) and `Guid SendCommand<TPayload>(string targetService, string messageType, TPayload payload)` (exchange `<target>.commands`, kind `command`). Both validate `messageType` (`ArgumentException`), add one `OutboxMessage` to `db` (status `pending`, `routing_key` = message type, `id = Guid.CreateVersion7()`, `payload` = full envelope JSON, `headers` = `{"traceparent", "correlation_id", "causation_id"}` with null keys omitted) and return the id. They never call `SaveChanges` (§7.1).
  - `internal static class MessageScope { static Envelope? Current { get; set; } }` (AsyncLocal) — set by the consumer host (Task 7) while a handler runs.
  - `internal static class BloomActivity { static readonly ActivitySource Source = new("Bloom.Shared"); }`.
  - Context rules (spec §6.3): correlation = `MessageScope.Current.CorrelationId` when handling; else `x-bloom-request-id` request header when it parses as a UUID; else `Guid.CreateVersion7()`. causation = `MessageScope.Current?.MessageId`. traceparent = `Activity.Current?.Id` (W3C). producer = `ServiceName`. occurredAt per R8.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public async Task T01_rolled_back_transaction_leaves_no_outbox_row()
// BeginTransaction, PublishEvent("t.thing-happened.v1", new { a = 1 }), SaveChanges, Rollback → outbox count 0
[Fact] public async Task Committed_event_row_has_envelope_and_columns()
// commit; row: kind "event", exchange "<svc>.events", routing_key == message_type, status "pending", attempts 0;
// payload parses as Envelope with MessageId == row.Id, Producer == svc, Kind "event", Payload.a == 1
[Fact] public async Task Command_goes_to_target_commands_exchange()   // exchange "inventory.commands", kind "command"
[Fact] public async Task Correlation_comes_from_uuid_request_id()
// HttpContext with header x-bloom-request-id = <uuid> → envelope.CorrelationId == uuid; headers.correlation_id same
[Fact] public async Task Non_uuid_request_id_starts_new_flow()        // "abc" → CorrelationId is a fresh version-7 UUID
[Fact] public async Task Handling_a_message_sets_causation_and_correlation()
// MessageScope.Current = incoming envelope → CausationId == incoming.MessageId, CorrelationId == incoming.CorrelationId
[Fact] public async Task Traceparent_copied_from_current_activity()  // inside a started Activity, headers.traceparent == activity.Id
[Fact] public void Invalid_message_type_throws()                    // "Bad" → ArgumentException
```

- [ ] **Step 2: Run** `dotnet test --filter FullyQualifiedName~OutboxWriterTests` — Expected: build fails.
- [ ] **Step 3: Implement** the writer, scope and activity source.
- [ ] **Step 4: Run** — Expected: PASS.
- [ ] **Step 5: Commit** — `feat(shared): outbox writer (T-01)`

---

### Task 5: RabbitMQ connection, consumer registration, topology

**Files:**
- Create: `Messaging/RabbitMqConnection.cs`, `ConsumerBinding.cs`, `ConsumerCatalog.cs`, `IMessageHandler.cs`, `TopologyInitializer.cs`
- Modify: `BloomServiceCollectionExtensions.cs` (`AddConsumer`, `SendsCommandsTo`, `AddWorker` on `BloomBuilder`; register `RabbitMqConnection`, `ConsumerCatalog` singletons)
- Modify tests: `Infrastructure/Containers.cs` (add RabbitMQ container, `RabbitMqUri`, `Task<string> RabbitCtlAsync(params string[] args)` exec helper); `TestApp` sets `RabbitMqUri`
- Create tests: `Infrastructure/Rabbit.cs` (helpers: `BasicGetAsync(queue)`, `PublishAsync(exchange, routingKey, body, headers?)`, `MessageCountAsync(queue)`), `TopologyTests.cs`
- Packages: `RabbitMQ.Client` (lib); `Testcontainers.RabbitMq` (tests)

**Interfaces:**
- Produces:
  - `public interface IMessageHandler { Task HandleAsync(Envelope envelope, CancellationToken ct); }`
  - `public sealed record ConsumerBinding(string Kind, string MessageType, string? Producer = null)` with `static Event(string producer, string messageType)`, `static Command(string messageType)` (binds to this service's `.commands` exchange); `internal string Exchange(string serviceName)`.
  - `internal sealed record ConsumerDefinition(string Handler, string Queue, Type HandlerType, IReadOnlyList<ConsumerBinding> Bindings)`; `internal sealed class ConsumerCatalog { IReadOnlyList<ConsumerDefinition> Consumers; IReadOnlyList<string> CommandTargets; IEnumerable<string> DeadLetterQueues }`.
  - `BloomBuilder AddConsumer<THandler>(string handler, params ConsumerBinding[] bindings) where THandler : class, IMessageHandler` (queue = `MessageNames.ConsumerQueue(service, handler)`, handler scoped); `BloomBuilder SendsCommandsTo(params string[] services)`; `BloomBuilder AddWorker()` (registers `TopologyInitializer` as the first hosted service; Tasks 6, 7, 9 add theirs after it).
  - `internal sealed class RabbitMqConnection(BloomOptions)` : `Task<IConnection> GetAsync(CancellationToken)` — one shared connection, `AutomaticRecoveryEnabled = false`, `ClientProvidedName = ServiceName`, re-created when closed (R7); `Task<IChannel> CreateConfirmChannelAsync(CancellationToken)` (`publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true`). Disposes the connection on host shutdown.
  - `internal sealed class TopologyInitializer : IHostedService` — `StartAsync` declares (idempotent): `<service>.events` (topic, durable); `<service>.commands` (direct, durable) when any binding is a command; every `<producer>.events` it consumes from; every `<target>.commands` in `CommandTargets`; per consumer: queue with `x-queue-type=quorum`, bindings (routing key = message type), 4 retry queues with `RetryQueueArguments`, DLQ `{x-queue-type: quorum}`.
  - `internal static IDictionary<string, object?> RetryQueueArguments(string consumerQueue, int ttlMs)` → `{x-queue-type: "quorum", x-message-ttl: ttlMs, x-dead-letter-exchange: "", x-dead-letter-routing-key: consumerQueue}`.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public void Retry_queue_arguments_match_section_6()
// RetryQueueArguments("s.h", 30000) equals exactly the 4 keys above
[Fact] public async Task T07_topology_declares_retry_queues_with_ttl_and_dlq()
// AddConsumer<NoopHandler>("h", ConsumerBinding.Event("order", "order.paid.v1")); start TopologyInitializer;
// rabbitmqctl list_queues name type arguments --formatter json: s.h, s.h.retry.{1s,5s,30s,2m}, s.h.dlq all "quorum";
// retry TTLs 1000/5000/30000/120000, DLX "", DLRK "s.h"; DLQ has no x-message-ttl
[Fact] public async Task Event_binding_routes_to_consumer_queue()
// publish "order.paid.v1" to order.events → BasicGet from s.h returns it; "order.other.v1" does not arrive
[Fact] public async Task Command_binding_and_targets_are_declared()
// AddConsumer<NoopHandler>("cmd", ConsumerBinding.Command("s.do-thing.v1")).SendsCommandsTo("payment"):
// exchanges <svc>.commands and payment.commands exist (passive declare), command routes to <svc>.cmd
[Fact] public async Task Declarations_are_idempotent()   // StartAsync twice → no exception
```

- [ ] **Step 2: Run** `dotnet test --filter FullyQualifiedName~TopologyTests` — Expected: build fails.
- [ ] **Step 3: Implement** the types. RabbitMQ image: same exact tag as Compose (e.g. `rabbitmq:4.1.4-management`).
- [ ] **Step 4: Run** — Expected: PASS; no leftover containers.
- [ ] **Step 5: Commit** — `feat(shared): rabbitmq connection, consumer registration and topology`

---

### Task 6: Outbox relay

**Files:**
- Create: `Messaging/OutboxRelay.cs`
- Modify: `BloomServiceCollectionExtensions.cs` (`AddWorker` registers `OutboxRelay`)
- Test: `OutboxRelayTests.cs`

**Interfaces:**
- Consumes: `RabbitMqConnection.CreateConfirmChannelAsync`, `OutboxMessage`, `BloomMetrics.OutboxPublish`, `BloomActivity.Source`.
- Produces: `internal sealed class OutboxRelay : BackgroundService` with `internal Task<int> RunOnceAsync(CancellationToken ct)` returning the number of rows claimed. Loop: `RunOnceAsync`; if it returned 100 loop immediately, else wait 200 ms; on exception log, dispose channel, wait 1 s (R7).

`RunOnceAsync`, exactly §7.2: own scope + `DbContext`; `BEGIN`; `SELECT * FROM outbox_messages WHERE status = 'pending' ORDER BY id LIMIT 100 FOR UPDATE SKIP LOCKED` (`FromSql`); publish each row in `id` order to `exchange`/`routing_key` with body = `payload` column bytes as stored, properties `MessageId = id`, `Type = message_type`, `ContentType = "application/json"`, `DeliveryMode = Persistent`, header `traceparent` = the relay's producer activity id (activity `"<exchange> publish"`, kind Producer, parent parsed from the row's `traceparent`), or the stored value when no activity is created; await confirms with a **5 s** timeout for the batch; confirmed → `status='sent'`, `sent_at=now()`, `outbox_publish_total{result=confirmed}`; nacked / timed out / channel error → `attempts=attempts+1`, `last_error=<exception message>`, `{result=failed}`; `COMMIT`. Failure logs carry `message_id`, `message_type`, `correlation_id` scope.

- [ ] **Step 1: Write the failing tests** (test queue `q` bound to `<svc>.events` with `#`; `TopologyInitializer` run first)

```csharp
[Fact] public async Task T01_rolled_back_row_is_never_published()   // rolled-back write, RunOnce → q empty
[Fact] public async Task T02_confirmed_row_marked_sent()
// committed row → RunOnce == 1; row status "sent", sent_at not null; q has 1 message: body == payload JSON,
// MessageId/Type/ContentType/persistent per §5.2, header traceparent present
[Fact] public async Task T03_nacked_row_stays_pending()
// q2 declared with x-max-length 0 + x-overflow reject-publish, bound to the exchange → RunOnce;
// row status "pending", attempts 1, last_error not null
[Fact] public async Task T04_two_relays_on_200_rows_publish_each_once()
// 200 committed rows; two OutboxRelay instances call RunOnceAsync concurrently in loops until both return 0;
// q receives exactly 200 messages with 200 distinct MessageIds; all rows "sent"
[Fact] public async Task Missing_exchange_counts_as_failure_and_relay_recovers()   // Review Focus 1
// row A with exchange "nope.events" → RunOnce: A pending, attempts 1, last_error not null;
// then mark A sent manually, insert valid row B → RunOnce publishes B
[Fact] public async Task Full_batch_is_100_rows()   // 150 rows → first RunOnce == 100, second == 50
```

- [ ] **Step 2: Run** `dotnet test --filter FullyQualifiedName~OutboxRelayTests` — Expected: build fails.
- [ ] **Step 3: Implement** `OutboxRelay`.
- [ ] **Step 4: Run** — Expected: PASS.
- [ ] **Step 5: Commit** — `feat(shared): outbox relay with confirms (T-01 … T-04)`

---

### Task 7: Consumer host

**Files:**
- Create: `Messaging/ConsumerHost.cs`
- Modify: `BloomServiceCollectionExtensions.cs` (`AddWorker` registers `ConsumerHost`)
- Test: `ConsumerHostTests.cs` (test handlers: `RecordingHandler` writes an `effects` row; `FailingHandler` throws a configurable exception and records each attempt seen; `PermanentHandler` throws `PermanentFailureException`)

**Interfaces:**
- Consumes: `ConsumerCatalog`, `RabbitMqConnection`, `Envelope.Parse`, `MessageScope`, `BloomMetrics`, `BloomActivity`, `MessageNames.RetryDelays`.
- Produces: `internal sealed class ConsumerHost : BackgroundService` — one supervisor loop per `ConsumerDefinition`: open confirm channel, `BasicQos(prefetch 16)` (R9), manual-ACK `AsyncEventingBasicConsumer` on the queue, await channel shutdown, wait 1 s, repeat.

Per delivery, exactly §7.3:
1. `attempt` = header `x-attempt` (int) or 1.
2. `Envelope.Parse(body)`; failure → permanent, reason `"invalid envelope: <msg>"`. `messageType` not in the definition's bindings → permanent, reason `"unknown message type: <type>"`.
3. New scope; `BEGIN`; `INSERT INTO inbox_messages (consumer, message_id, message_type) VALUES (…) ON CONFLICT DO NOTHING` with `consumer` = queue name; 0 rows → `COMMIT`, ACK, `result=duplicate`.
4. Set `MessageScope.Current`; start consumer activity `"<queue> process"` (parent from `traceparent` header); log scope `message_id`, `message_type`, `correlation_id`, `consumer`; resolve handler from the scope; `HandleAsync`; `SaveChangesAsync`; `COMMIT`; ACK; `result=success`; record `consumer_handle_seconds`.
5. Exception → `ROLLBACK`. `PermanentFailureException` → DLQ. Else `attempt < 5` → publish to `<queue>.retry.<RetryDelays[attempt-1]>` with `x-attempt = attempt + 1`, `result=retry`; `attempt >= 5` → DLQ.
6. DLQ publish: default exchange, routing key `<queue>.dlq`, original body + properties + headers plus `x-failure-reason` (`"<ExceptionType>: <message>"`) and `x-failed-at` (RFC 3339 UTC), `result=dlq`. All retry/DLQ publishes await confirms, then ACK the original. Publish failure → close the channel, no ACK (R7).

- [ ] **Step 1: Write the failing tests** (host built with `AddConsumer<…>("h", ConsumerBinding.Event(svc, "<svc>.thing.v1")).AddWorker()`, started; messages produced through `OutboxWriter` + relay unless noted)

```csharp
[Fact] public async Task T05_duplicate_delivery_applies_once()
// publish the same envelope bytes twice directly to the queue → effects count 1; consumer_messages_total{result=duplicate} == 1
[Fact] public async Task T06_two_handlers_each_apply_once()
// two consumers "h1", "h2" bound to the same type → effects 1 per consumer; inbox rows (s.h1, id), (s.h2, id)
[Fact] public async Task T07_retry_routing_per_attempt()
// FailingHandler (transient). Publish directly to s.h with header x-attempt n; a failure at attempt n goes to RetryDelays[n-1]:
// x-attempt 2 → s.h.retry.5s with x-attempt 3; x-attempt 3 → s.h.retry.30s (4);
// x-attempt 4 → s.h.retry.2m (5); x-attempt 5 → s.h.dlq with x-failure-reason starting "InvalidOperationException";
// each checked by Rabbit.BasicGetAsync within 5 s
[Fact] public async Task T07_real_round_trip_through_1s_retry_queue()
// no x-attempt → handler sees attempts [1, 2] within 10 s (via the 1 s TTL queue); message then in s.h.retry.5s with x-attempt 3
[Fact] public async Task T08_permanent_failure_goes_straight_to_dlq()
// PermanentHandler → s.h.dlq has the message with x-failure-reason, x-failed-at (parses as UTC); handler called once; no retry queue has it; no inbox row
[Fact] public async Task Invalid_envelope_goes_to_dlq()          // Review Focus 2: body "not json" and body without messageId
[Fact] public async Task Unknown_message_type_goes_to_dlq()      // Review Focus 2: valid envelope, type "<svc>.other.v1" published to the queue
[Fact] public async Task Payload_missing_required_field_goes_to_dlq() // Review Focus 2: handler calls GetPayload<T>() on {} → DLQ, one attempt
[Fact] public async Task Handler_outbox_writes_commit_with_inbox()
// handler PublishEvent(...) → after processing, outbox row exists with causation_id = incoming messageId
[Fact] public async Task Consumer_continues_trace_from_traceparent()
// ActivityListener on "Bloom.Shared"; write outbox row inside an Activity with known TraceId → handler sees Activity.Current.TraceId == that TraceId
[Fact] public async Task Log_scope_carries_message_fields()
// capturing ILoggerProvider; handler logs once → scope has message_id, message_type, correlation_id, consumer
[Fact] public async Task Reconnects_after_connection_drop()      // Review Focus 4
// process one message; Containers.RabbitCtlAsync("close_all_connections", "test"); publish another through the outbox → processed within 15 s
```

- [ ] **Step 2: Run** `dotnet test --filter FullyQualifiedName~ConsumerHostTests` — Expected: build fails.
- [ ] **Step 3: Implement** `ConsumerHost`.
- [ ] **Step 4: Run** — Expected: PASS. T-07 tests finish in well under 30 s total (no waiting on the 5 s/30 s/2 m TTLs).
- [ ] **Step 5: Commit** — `feat(shared): consumer host with inbox, retries and DLQ (T-05 … T-08)`

---

### Task 8: Idempotency guard

**Files:**
- Create: `Idempotency/IdempotencyGuard.cs`
- Modify: `BloomServiceCollectionExtensions.cs` (register scoped)
- Create tests: `Protos/test.proto` (`ChargeRequest { string sku = 1; int32 quantity = 2; }`, `ChargeResponse { string charge_id = 1; }`, csharp_namespace `Bloom.Shared.Tests.Protos`, compiled with Grpc.Tools `GrpcServices="None"`), `Infrastructure/FakeServerCallContext.cs` (method + request headers), `IdempotencyTests.cs`

**Interfaces:**
- Consumes: `IdempotencyKey`, `BloomErrors`, `BloomMetrics.Idempotency`.
- Produces: `public sealed class IdempotencyGuard(DbContext db, BloomMetrics metrics)`:
  - `Task<TResponse> ExecuteAsync<TRequest, TResponse>(TRequest request, ServerCallContext context, Func<CancellationToken, Task<TResponse>> handler) where TRequest : IMessage<TRequest> where TResponse : IMessage<TResponse>, new()` — single-transaction form.
  - `Task<TResponse> ExecuteWithProviderAsync<TRequest, TProviderResult, TResponse>(TRequest request, ServerCallContext context, Func<string, CancellationToken, Task<TProviderResult>> callProvider, Func<TProviderResult, CancellationToken, Task<TResponse>> apply)` — two-phase form; `callProvider` receives `bloom:<operation>:<user_id>:<key>`.
  - XML doc on both: request messages of idempotent operations must not use map fields (spec §6.7); the guard owns the transaction(s), the handler must not begin one.

Pinned behaviour (§7.4, LLD-ADR-001, spec §6.7):
- Inputs: header `idempotency-key` (missing → `INVALID_ARGUMENT`/`IDEMPOTENCY_KEY_REQUIRED`; length > 36 or not `Guid.TryParseExact(…, "D")` → `IDEMPOTENCY_KEY_INVALID`; both before any SQL); key stored as `guid.ToString()` (R4); `user_id` = `x-bloom-user-id` header parsed as UUID (missing/invalid → `InvalidOperationException`, a gateway misconfiguration); `operation` per R4; `request_hash = SHA256.HashData(request.ToByteArray())`.
- Claim: `INSERT … (status 'in_progress', locked_until = now() + interval '60 seconds') ON CONFLICT DO NOTHING`. 0 rows → read the row with `locked_until > now()` computed in SQL, then the decision table: operation/hash differ → `IDEMPOTENCY_KEY_REUSED` (`reused`); `completed` → replay (`replay`); in progress and locked → `ABORTED`/`IDEMPOTENCY_IN_PROGRESS` (`in_progress`); stale → `UPDATE idempotency_keys SET locked_until = now() + interval '60 seconds' WHERE user_id = … AND key = … AND status = 'in_progress' AND locked_until <= now()` — 1 row → resume (`takeover`), 0 → `IDEMPOTENCY_IN_PROGRESS`. Fresh claim → `new`.
- Single form: claim, savepoint, handler, `SaveChanges`, store outcome, `COMMIT` — all in one transaction. Two-phase: tx 1 claim + `COMMIT`; provider call; tx 2 = savepoint, apply, `SaveChanges`, store outcome, `COMMIT`.
- Outcome: success → `status='completed'`, `response_status=0`, `response_body=response.ToByteArray()`, `completed_at=now()`. `RpcException` with `FAILED_PRECONDITION`, `INVALID_ARGUMENT`, `NOT_FOUND`, `ALREADY_EXISTS`, `PERMISSION_DENIED` → roll back to the savepoint, store `completed` with `response_status = code`, `response_body = google.rpc.Status` bytes (`GetRpcStatus()` or `{code, message}`), rethrow. Anything else (incl. provider failures and non-`RpcException`) → roll back; the row is gone (single form) or deleted with `DELETE … WHERE user_id, key AND status = 'in_progress'` (two-phase); rethrow.
- Replay: `response_status = 0` → `TResponse.Parser`-equivalent parse of `response_body`; else `Google.Rpc.Status.Parser.ParseFrom(body).ToRpcException()`.

- [ ] **Step 1: Write the failing tests** (handlers count invocations and insert an `effects` row; user/key headers via `FakeServerCallContext`)

```csharp
[Fact] public async Task T10_new_key_executes_and_completes()      // row completed, response_status 0; idempotency_requests_total{result=new}
[Fact] public async Task T11_same_key_same_request_replays()       // same ChargeResponse bytes; handler called once; effects 1; result=replay
[Fact] public async Task T12_same_key_different_request_is_reused() // quantity changed → INVALID_ARGUMENT / IDEMPOTENCY_KEY_REUSED
[Fact] public async Task Same_key_different_operation_is_reused()  // other method name → IDEMPOTENCY_KEY_REUSED (§10)
[Fact] public async Task T13_single_tx_concurrent_duplicate_gets_replay()
// two scopes, handler 1 waits on a gate; second call blocks; release → both return the same response; handler called once
[Fact] public async Task T13_two_phase_concurrent_duplicate_in_progress()
// provider call 1 waits on a gate; second call → ABORTED / IDEMPOTENCY_IN_PROGRESS
[Fact] public async Task T14_crash_after_provider_call_is_resumed_after_lock_expiry()
// fake provider (dedupes by key, counts distinct effects) already called with the key; insert the stale in_progress row the crash
// would leave (locked_until = now() - 1 s); retry → result=takeover, provider returns the same result, row completed, provider effects 1;
// with locked_until in the future instead → IDEMPOTENCY_IN_PROGRESS
[Fact] public async Task Stale_key_taken_over_by_exactly_one()     // §10: two concurrent retries on a stale row → one resumes, one IDEMPOTENCY_IN_PROGRESS
[Fact] public async Task T15_business_rejection_is_replayed()
// handler throws BloomErrors.FailedPrecondition("SOLD_OUT", …) → retry → FAILED_PRECONDITION reason SOLD_OUT; handler called once;
// handler's own effects row from the failed attempt not committed; same for the two-phase form (apply throws)
[Fact] public async Task T16_transient_failure_releases_key()
// handler throws RpcException(Unavailable) then succeeds → retry executes; also InvalidOperationException; also two-phase provider throws Unavailable
[Fact] public async Task Malformed_keys_rejected_before_database()   // Review Focus 3
// missing → IDEMPOTENCY_KEY_REQUIRED; "", "abc", 37 chars, "{<uuid>}", non-hex 36 chars → IDEMPOTENCY_KEY_INVALID; idempotency_keys count 0
[Fact] public async Task Key_case_variants_are_one_key()            // uppercase then lowercase of same UUID → second is a replay
[Fact] public async Task Same_key_two_users_do_not_interfere()      // §10: both execute
[Fact] public async Task Provider_key_format()                       // callProvider receives "bloom:<operation>:<user_id>:<key>"
```

- [ ] **Step 2: Run** `dotnet test --filter FullyQualifiedName~IdempotencyTests` — Expected: build fails.
- [ ] **Step 3: Implement** `IdempotencyGuard`.
- [ ] **Step 4: Run** — Expected: PASS.
- [ ] **Step 5: Commit** — `feat(shared): idempotency guard, single and two-phase (T-10 … T-16)`

---

### Task 9: Retention purger and gauge poller

**Files:**
- Create: `Retention/RetentionPurger.cs`, `Observability/GaugePoller.cs`
- Modify: `BloomServiceCollectionExtensions.cs` (`AddWorker` registers both)
- Test: `RetentionTests.cs`, `GaugeTests.cs`

**Interfaces:**
- Consumes: `ConsumerCatalog.DeadLetterQueues`, `RabbitMqConnection`, `BloomMetrics.ReportOutboxBacklog`/`ReportDlqDepth`.
- Produces:
  - `internal sealed class RetentionPurger : BackgroundService` — runs `PurgeOnceAsync` at start, then every 24 h (`PeriodicTimer`). `internal Task PurgeOnceAsync(CancellationToken)`: per table, repeat `DELETE … WHERE <key> IN (SELECT <key> … WHERE <rule> LIMIT 1000)` until fewer than 1,000 rows deleted. Rules exactly §13: outbox `status = 'sent' AND sent_at < now() - interval '7 days'`; inbox `processed_at < now() - interval '14 days'`; idempotency `created_at < now() - interval '24 hours'`.
  - `internal sealed class GaugePoller : BackgroundService` — every 15 s (R9) `PollOnceAsync`: `SELECT count(*), coalesce(extract(epoch from now() - min(created_at)), 0) FROM outbox_messages WHERE status = 'pending'` → `ReportOutboxBacklog`; passive declare of each DLQ → `ReportDlqDepth` (missing queue → 0).

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public async Task T19_purge_removes_only_rows_past_retention()
// seed (timestamps set by SQL relative to now()): outbox sent 8 d ago (gone), sent 6 d ago (kept), pending created 30 d ago (kept);
// inbox 15 d (gone) / 13 d (kept); idempotency 25 h (gone) / 23 h (kept)
[Fact] public async Task Purge_deletes_more_than_one_batch()   // 2,500 expired inbox rows → all gone after one PurgeOnceAsync
[Fact] public async Task Gauges_report_backlog_and_dlq_depth()
// 3 pending rows (oldest created now() - 10 s) + 2 messages in s.h.dlq → MeterListener observes
// outbox_pending_count 3, outbox_oldest_pending_age_seconds >= 10, dlq_depth{queue="s.h.dlq"} 2, all with service tag
```

- [ ] **Step 2: Run** `dotnet test --filter "FullyQualifiedName~RetentionTests|FullyQualifiedName~GaugeTests"` — Expected: build fails.
- [ ] **Step 3: Implement** both services.
- [ ] **Step 4: Run** — Expected: PASS.
- [ ] **Step 5: Commit** — `feat(shared): retention purger and backlog/DLQ gauges (T-19)`

---

### Task 10: DLQ admin

**Files:**
- Create: `DeadLetters/Protos/dead_letter_admin.proto`, `DeadLetterAdminService.cs`, `DeadLetterPass.cs`, `StaffActionRecorded.cs`
- Modify: `Bloom.Shared.csproj` (`<Protobuf Include="DeadLetters/Protos/dead_letter_admin.proto" GrpcServices="Both" />`), `BloomServiceCollectionExtensions.cs` (`public static IEndpointRouteBuilder MapBloomDeadLetterAdmin(this IEndpointRouteBuilder endpoints)` → `MapGrpcService<DeadLetterAdminService>()`)
- Test: `DeadLetterAdminTests.cs` (TestServer host: `AddGrpc().AddJsonTranscoding()`, `AddBloom`, consumer `h`, `MapBloomDeadLetterAdmin()`; gRPC client over `server.CreateHandler()`; trusted headers `x-bloom-user-type: staff`, `x-bloom-role: Admin`, `x-bloom-user-id`, `x-bloom-request-id`)
- Packages (tests): `Microsoft.AspNetCore.TestHost`, `Grpc.Net.Client`, `Microsoft.AspNetCore.Grpc.JsonTranscoding`

**Interfaces:**
- Consumes: `ConsumerCatalog`, `RabbitMqConnection.CreateConfirmChannelAsync`, `OutboxWriter.PublishEvent`, `BloomErrors`.
- Produces:
  - Proto `package bloom.shared.v1;` `option csharp_namespace = "Bloom.Shared.DeadLetters";` imports `google/api/annotations.proto`. Service `DeadLetterAdmin`:

    | RPC | HTTP | Request fields | Response |
    |---|---|---|---|
    | `ListDeadLetterQueues` | `get: /api/admin/dlq/{service}/queues` | `service` | `repeated DeadLetterQueue queues` (`name`, `uint32 message_count`) |
    | `PeekDeadLetters` | `get: /api/admin/dlq/{service}/queues/{queue}/messages` | `service`, `queue`, `int32 limit` | `repeated DeadLetter messages` (`message_id`, `message_type`, `envelope_json`, `failure_reason`, `failed_at`) |
    | `ReplayDeadLetter` | `post: /api/admin/dlq/{service}/queues/{queue}/messages/{message_id}:replay` | `service`, `queue`, `message_id` | empty |
    | `ReplayAllDeadLetters` | `post: /api/admin/dlq/{service}/queues/{queue}:replay-all` | `service`, `queue` | `uint32 replayed_count` |
    | `DiscardDeadLetter` | `delete: /api/admin/dlq/{service}/queues/{queue}/messages/{message_id}` | `service`, `queue`, `message_id` | empty |

    `queue` = full DLQ name (e.g. `inventory.order-paid.dlq`).
  - `public sealed record StaffActionRecorded(Guid ActorId, string ActorRole, string Service, string Action, string TargetType, string TargetId, JsonElement Details)` — published as `identity.staff-action-recorded.v1` on `<service>.events` (spec §6.11), `Action` = `<service>.dlq-replayed` | `<service>.dlq-discarded`, `TargetType` = `dlq_message`, `TargetId` = messageId, `Details` = `{queue, messageType, failureReason}`.
  - `internal sealed class DeadLetterPass` — `Task RunAsync(string dlq, Func<BasicDeliverEventArgs, PassAction> decide, Func<BasicDeliverEventArgs, PassAction, Task> audit, CancellationToken)`; `enum PassAction { Keep, Replay, Discard }`.

Pinned behaviour (§7.7, LLD-ADR-009):
- Every RPC, in order: role check (`x-bloom-user-type == "staff"` and `x-bloom-role == "Admin"`, ordinal) → else `PERMISSION_DENIED`/`ROLE_NOT_ALLOWED`; `service == ServiceName` → else `NOT_FOUND`/`DLQ_NOT_FOUND` (R5); `queue` in `ConsumerCatalog.DeadLetterQueues` → else `DLQ_NOT_FOUND`.
- Pass: confirm channel; `count` = passive declare message count (404 → `DLQ_NOT_FOUND`, R6); `BasicQos(prefetch 1)`; `BasicConsume(exclusive: true, autoAck: false)` (403 ACCESS_REFUSED → `ABORTED`/`DLQ_BUSY`) — always, even when `count == 0`, so a running operation is always detected; `count == 0` → cancel and stop; else take exactly `count` deliveries. Per message: (1) Replay → publish body + properties to the consumer queue (DLQ name minus `.dlq`, default exchange) with headers minus `x-attempt`, `x-failure-reason`, `x-failed-at`; Keep → publish unchanged to the DLQ tail; await confirm. (2) Replay/Discard → `OutboxWriter.PublishEvent(StaffActionRecorded)` + `SaveChangesAsync` (commits the audit row). (3) On the last message cancel the consumer first, then ACK; otherwise ACK. Never nack/requeue.
- Peek: all Keep, return the first `limit` (R2: 0 → 20, clamp 1–100). Replay/Discard one: first delivery whose `MessageId` equals `message_id`, others Keep; none matched → `NOT_FOUND`/`DLQ_MESSAGE_NOT_FOUND` after the pass. ReplayAll: all Replay. List: catalog DLQs with counts (R6).

- [ ] **Step 1: Write the failing tests** (DLQ messages seeded by publishing envelopes directly to `s.h.dlq` with `x-attempt 5`, `x-failure-reason`, `x-failed-at`, unless noted)

```csharp
[Fact] public async Task T09_replay_after_fix_processes_once_with_one_audit()
// worker running; handler fails permanently while flag set → message lands in s.h.dlq; clear flag; ReplayDeadLetter →
// effects 1 within 10 s; outbox has 1 identity.staff-action-recorded.v1 row: action "<svc>.dlq-replayed", target_type "dlq_message",
// target_id = messageId, actorId/actorRole from headers, details.queue/messageType/failureReason; DLQ empty
[Fact] public async Task T20_replay_all_three()
// no worker; 3 messages → replayed_count 3; s.h has 3 messages in original order, none with x-attempt/x-failure-reason/x-failed-at,
// traceparent kept; 3 audit rows; DLQ count 0
[Fact] public async Task T21_discard_middle_of_three()
// discard #2 → Peek shows #1, #3 in that order; 1 audit row with action "<svc>.dlq-discarded"
[Fact] public async Task T22_peek_twice_is_stable()
// Peek(limit 0) twice → same 3 message ids, same order, failure_reason/failed_at filled; DLQ count 3; 0 audit rows; Peek(limit 2) → first 2
[Fact] public async Task T23_second_operation_gets_dlq_busy()
// test holds its own exclusive consumer (prefetch 1, never ACKs) on s.h.dlq → ReplayAll → ABORTED / DLQ_BUSY;
// test closes its channel → DLQ count back to 3 → ReplayAll succeeds (replayed_count 3)
[Fact] public async Task T24_support_role_is_not_allowed()
// role "Support" → PERMISSION_DENIED / ROLE_NOT_ALLOWED for every RPC; user-type "customer" same; DLQ count unchanged; 0 audit rows
[Fact] public async Task Unknown_service_or_queue_is_dlq_not_found()   // service "other" → NOT_FOUND/DLQ_NOT_FOUND; queue "s.x.dlq" → DLQ_NOT_FOUND
[Fact] public async Task Missing_message_id_is_dlq_message_not_found() // 3 messages, unknown id → DLQ_MESSAGE_NOT_FOUND; 3 remain in order
[Fact] public async Task Empty_dlq_returns_promptly()                  // Review Focus 5: Replay on empty DLQ → DLQ_MESSAGE_NOT_FOUND in < 2 s
[Fact] public async Task Late_arrivals_are_untouched()                 // Review Focus 5: decide callback publishes a 4th message to the DLQ
//   during the pass (DeadLetterPass test seam) → ReplayAll replays 3; the 4th stays in the DLQ
[Fact] public async Task List_returns_dlqs_with_counts()               // s.h.dlq with 2 → [{name "s.h.dlq", count 2}]
[Fact] public async Task Http_route_is_transcoded()
// HttpClient GET /api/admin/dlq/<svc>/queues with trusted headers → 200, JSON contains "s.h.dlq"
```

- [ ] **Step 2: Run** `dotnet test --filter FullyQualifiedName~DeadLetterAdminTests` — Expected: build fails.
- [ ] **Step 3: Implement** proto, pass, service, `MapBloomDeadLetterAdmin`. `google/api/annotations.proto` comes from `Google.Api.CommonProtos` (already referenced); if Grpc.Tools can't find it, add its proto directory via `AdditionalImportDirs`.
- [ ] **Step 4: Run** — Expected: PASS.
- [ ] **Step 5: Commit** — `feat(shared): DeadLetterAdmin gRPC service (T-09, T-20 … T-24)`

---

### Task 11: Shared telemetry, dev Compose, Prometheus name check

**Files:**
- Create: `Observability/BloomTelemetryExtensions.cs`
- Create: `deploy/compose/docker-compose.yml`, `otel-collector.yaml`, `tempo.yaml`, `prometheus.yml`, `loki.yaml`, `grafana/datasources.yaml`
- Test: `TelemetryTests.cs`, `MetricNamesTests.cs`
- Packages (lib): `OpenTelemetry.Extensions.Hosting`, `OpenTelemetry.Exporter.OpenTelemetryProtocol`, `OpenTelemetry.Instrumentation.AspNetCore`, `OpenTelemetry.Instrumentation.GrpcNetClient`, `OpenTelemetry.Instrumentation.Http`, `OpenTelemetry.Instrumentation.EntityFrameworkCore`, `Npgsql.OpenTelemetry`

**Interfaces:**
- Consumes: `BloomMetrics.MeterName`, `BloomActivity.Source` name `Bloom.Shared`.
- Produces: `public static IHostApplicationBuilder AddBloomTelemetry(this IHostApplicationBuilder builder, string serviceName)` — resource `service.name = serviceName`; tracing: ASP.NET Core, gRPC client, HttpClient, EF Core, Npgsql, source `Bloom.Shared`; metrics: ASP.NET Core, HttpClient, meter `Bloom.Shared`; logging via OpenTelemetry with `IncludeScopes = true`, `IncludeFormattedMessage = true`; `UseOtlpExporter()` (endpoint from standard `OTEL_EXPORTER_OTLP_*` env vars).

Compose (spec §8.1, HLD §9.5): services `postgres`, `rabbitmq` (management image, ports 5672/15672), `redis`, `mailpit` (1025/8025), `otel-collector` (4317/4318), `tempo`, `prometheus` (scrapes `otel-collector:8889`), `loki`, `grafana` (3000, datasources Tempo/Prometheus/Loki provisioned). Exact image tags (no `latest`, no major-only; verify each with `docker manifest inspect`). Dev-only credentials inline. Single RabbitMQ node, single PostgreSQL. Collector: OTLP receiver; `otlp` → `tempo:4317`; `prometheus` exporter on `0.0.0.0:8889` with `add_metric_suffixes: false` (R10); `otlphttp` → `http://loki:3100/otlp`.

- [ ] **Step 1: Write the failing tests**

```csharp
// TelemetryTests
[Fact] public void AddBloomTelemetry_registers_providers()
// host with AddBloomTelemetry("svc") → TracerProvider and MeterProvider resolvable; an activity from "Bloom.Shared" is recorded
// (in-memory exporter added in the test), with resource service.name "svc"

// MetricNamesTests
[Fact] public async Task Prometheus_names_match_section_12()
// start otel-collector container (same tag as Compose) with deploy/compose/otel-collector.yaml mounted; MeterProvider with
// OTLP exporter to it; BloomMetrics("svc") records one of each §12 metric; ForceFlush; poll GET :8889/metrics (≤ 30 s):
// lines exist for outbox_pending_count, outbox_oldest_pending_age_seconds, outbox_publish_total{result=confirmed},
// consumer_messages_total{consumer,result}, consumer_handle_seconds_bucket{consumer}, dlq_depth{queue},
// idempotency_requests_total{operation,result}, optimistic_conflicts_total{entity}, conditional_update_rejections_total{counter},
// each with service="svc"; no line starts with "outbox_publish_total_total" or "consumer_handle_seconds_seconds"
```

- [ ] **Step 2: Run** `dotnet test --filter "FullyQualifiedName~TelemetryTests|FullyQualifiedName~MetricNamesTests"` — Expected: build fails.
- [ ] **Step 3: Implement** `AddBloomTelemetry` and the Compose files.
- [ ] **Step 4: Run** the filter — Expected: PASS. Then `docker compose -f deploy/compose/docker-compose.yml config --quiet` — Expected: exit 0, no output.
- [ ] **Step 5: Full suite** `dotnet test -c Release` — Expected: all pass; `docker ps -a` shows no containers from the run.
- [ ] **Step 6: Commit** — `feat(shared): telemetry setup, dev compose and metric name check`
