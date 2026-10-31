# Shared library and repo foundation: design

| | |
|---|---|
| **Status** | Draft, pending owner approval |
| **Date** | 2026-10-31 |
| **Branch / PR base** | `feat/shared-library` → `main` |
| **Implements** | [00-reliability](../../../doc/low-level-design/00-reliability.md) (all modules, including DLQ admin §7.7 / [LLD-ADR-009](../../../doc/low-level-design/adr/LLD-ADR-009-dlq-admin-endpoint.md)), HLD §9.3 / [HLD-ADR-015](../../../doc/high-level-design/adr/HLD-ADR-015-observability.md) (telemetry), HLD §9.5 / [HLD-ADR-014](../../../doc/high-level-design/adr/HLD-ADR-014-deployment.md) (monorepo, Compose, CI) |
| **Follow-up project** | api-gateway ([07-api-gateway](../../../doc/low-level-design/07-api-gateway.md)), separate spec |

## 1. Intent

Bloom has no code yet. This is the first project. It lays down the repo and the shared plumbing every service builds on, so that each later service gets reliable messaging, idempotent requests, safe counters, one error format and end-to-end telemetry without re-implementing them (00-reliability §1). The API gateway is the next project and builds on this one.

`doc/` is the approved design. This project builds what it says and nothing more (AGENTS.md §1). Every behaviour below traces to a requirement in 00-reliability (`RL-xx`, `T-xx`) or an HLD/LLD ADR.

## 2. Goals

1. A .NET 10 monorepo skeleton that later services drop into.
2. The shared library, implementing 00-reliability §4–§13, including DLQ admin (§7.7).
3. A shared OpenTelemetry startup call used by every service.
4. A dev Docker Compose stack with everything HLD §9.5 lists for development.
5. A GitHub Actions workflow that builds and tests on every push and PR.

**Success criteria:** test cases T-01 … T-24 (00-reliability §14) exist as automated tests and pass locally and in CI. `docker compose config` validates the Compose file.

## 3. Non-goals and anti-goals

**Non-goals (later projects):** the api-gateway (including its `/api/admin/dlq/{service}/**` routes and its own revocation consumer's DLQ), any domain service, message contract packages per producer, `.proto` service contracts other than `DeadLetterAdmin`, Kubernetes manifests, image build/push and deploy steps in CI, per-service broker credentials (§11 of 00-reliability: a Kubernetes/broker-config concern).

**Anti-goals** (failure even if it technically works):
- Any domain logic in the library (00-reliability §1: "plumbing only").
- Publishing to RabbitMQ from a handler instead of through the outbox (§7.1).
- Different table names, queue names, headers or retry schedule from 00-reliability. Dashboards, the purger and DLQ admin rely on them being fixed.
- Behaviour, endpoints, fields or options the docs don't describe (AGENTS.md §1).

## 4. Owner decisions

| Topic | Decision |
|---|---|
| Foundation scope | Full shared library before the gateway |
| .NET | .NET 10 (LTS) for the whole repo |
| Data access | EF Core + Npgsql. The library joins the caller's `DbContext` transaction. Raw SQL through EF where the design needs it (`FOR UPDATE SKIP LOCKED`, `ON CONFLICT DO NOTHING`, conditional and versioned updates). |
| RabbitMQ client | Official `RabbitMQ.Client` (v7, async API). No MassTransit / Wolverine. |
| Tests | xUnit + Testcontainers (real PostgreSQL and RabbitMQ through Docker on Colima) |
| DLQ tool | Admin gRPC service per service (LLD-ADR-009, 00-reliability §7.7); route `/api/admin/dlq/{service}/**`; list, peek, replay one, replay all, discard one |
| Docker Compose | Everything in HLD §9.5 for development: PostgreSQL, RabbitMQ (management UI), Redis, Mailpit, Grafana, Tempo, Prometheus, Loki, OpenTelemetry Collector |
| CI | GitHub Actions: build + test only |
| Telemetry | Shared OpenTelemetry startup call in the library |
| Branch | `feat/shared-library` off `main`, PR into `main` |
| Idempotency outcome rule | Classified by gRPC status (§6.7) |

## 5. Repo layout (builder's call, proposed)

```
Bloom.slnx
global.json                    # pins .NET 10 SDK
Directory.Build.props          # net10.0, nullable, warnings as errors, implicit usings
Directory.Packages.props       # central package versions
src/
  Shared/
    Bloom.Shared/              # the shared library (one project)
    Bloom.Shared.Tests/        # xUnit + Testcontainers
  <service>/                   # one folder per service, added by later projects
deploy/
  compose/
    docker-compose.yml
    <config files for collector, Prometheus, Tempo, Loki, Grafana datasources>
.github/workflows/ci.yml
```

One library project, not one per module. Services reference it with a project reference (monorepo, HLD-ADR-014). Internal folders inside the project follow the modules in §6.

## 6. Library modules

Each module follows 00-reliability exactly. This section only says what the library exposes and where a choice was needed.

### 6.1 Persistence model (§4)

- EF Core entity configurations for `outbox_messages`, `inbox_messages`, `idempotency_keys`, with the columns, types, constraints and indexes of §4.1–§4.3 exactly (snake_case names, partial index on pending outbox rows).
- A `ModelBuilder` extension a service calls in `OnModelCreating`. Each service's own EF migrations then create the tables. The library ships no migrations of its own.
- UUIDv7 for outbox IDs: `Guid.CreateVersion7()` (.NET 9+).

### 6.2 Message contracts (§5)

- Envelope type with the §5.2 fields; JSON with `System.Text.Json`, camelCase, unknown fields ignored (§5.3 rule 2).
- Name helpers for exchanges, queues, retry queues and DLQs (§5.1), so services never hand-build names.
- AMQP properties and headers exactly as §5.2.

### 6.3 Outbox writer (§7.1)

- Adds an outbox row to the caller's `DbContext`, so it commits or rolls back with the business change (RL-01).
- Fills `traceparent` from the current `Activity`, `correlation_id` from the current context, and `causation_id` when called while handling a message.
- How the correlation ID reaches the writer for API requests (for example from `x-bloom-request-id`, 07-api-gateway §4.3) is the builder's call. It must not add a new header or field.
- **`payload` column holds the whole envelope** (§4.1 "Message body (§5.2)", and §5.2 calls the envelope "the JSON body of every message"). The writer builds it at insert time; the relay publishes the column as-is.
- *My call:* `producer` = the service name the library is configured with. `occurredAt` = the insert time in the caller's transaction, which is the closest knowable value to the commit time.
- *My call:* for events the writer derives `<producer>.events` itself; for commands the caller names the target service and the writer derives `<target>.commands`. `exchange` and `routing_key` are stored in their columns (§4.1), so the relay never derives anything.
- *My call:* correlation ID. In a message handler it is copied from the incoming envelope. In an API request it is `x-bloom-request-id` when that is a UUID. Otherwise (no context, or a non-UUID request ID) the writer starts a new flow with a fresh UUIDv7. The gateway project should generate UUID request IDs so the request ID and the correlation ID match.

### 6.4 Outbox relay (§7.2)

- A hosted background service for worker processes. Batch 100, `FOR UPDATE SKIP LOCKED`, confirms with a 5 s timeout, 200 ms idle sleep, `attempts` / `last_error` on failure. Safe with several instances (T-04).

### 6.5 Topology (§6)

- Idempotent declarations at worker startup: the service's event exchange, the command exchanges it publishes to or consumes from, each consumer queue (quorum) with its bindings, its four retry queues and its DLQ, with the arguments in the §6 table.
- *Builder's call:* a worker declares every exchange it publishes to as well as the ones it consumes from. Declarations are idempotent, so redundancy is harmless, and the relay never publishes to a missing exchange.

### 6.6 Consumer host (§7.3)

- Hosted service, manual ACK, bounded prefetch (value: builder's call), one consumer per registered handler.
- Inbox insert + handler + outbox writes in one transaction; duplicate → ACK; permanent vs transient classification; retry with `x-attempt`; DLQ with `x-failure-reason`, `x-failed-at`; all retry/DLQ publishes with confirms.
- A permanent-failure exception type handlers throw (§7.3 step 8).
- Continues the trace from `traceparent` (§12).

### 6.7 Idempotency guard (§7.4, LLD-ADR-001)

- Both execution forms: single-transaction, and two-phase with provider call.
- Full decision table, including stale takeover by conditional update, header validation (UUID, ≤ 36 characters, rejected before touching the database, §11), outcome rules (success and deterministic rejections stored; transient failures delete the row), and the provider key `bloom:<operation>:<user_id>:<key>`.
- **Outcome classification (owner decision):** by gRPC status. Stored and replayed: success, plus `FAILED_PRECONDITION`, `INVALID_ARGUMENT`, `NOT_FOUND`, `ALREADY_EXISTS`, `PERMISSION_DENIED`. Released (row deleted): every other status (`UNAVAILABLE`, `DEADLINE_EXCEEDED`, `INTERNAL`, …) and any exception that is not an `RpcException`. The two-phase form follows the same rule in tx 2.
- `user_id` comes from the trusted identity context (`x-bloom-user-id` as gRPC metadata), never from the request (§11).
- `request_hash` = SHA-256 of the request's deterministic protobuf serialization. The library depends on `Google.Protobuf`. C# protobuf has no deterministic-serialization switch; field order is stable, but map fields are not guaranteed. *My call:* document that request messages of idempotent operations must not use map fields.
- The key arrives as the `Idempotency-Key` HTTP header, which reaches the service as gRPC metadata `idempotency-key`.
- All time comparisons use the database `now()` (§10).

### 6.8 Concurrency helpers (§7.5, §7.6)

- Conditional update: runs a guarded single-statement `UPDATE` and returns whether one row was affected. The caller maps 0 rows to its own domain rejection.
- Optimistic lock: versioned `UPDATE … WHERE version = @expected`; 0 rows → `CONCURRENCY_CONFLICT` if the row exists, `NOT_FOUND` if not. No automatic retry.
- Exactly how a service expresses the guarded update (raw SQL via EF, or `ExecuteUpdateAsync` with a `Where`) is the builder's call, as long as it is one statement.

### 6.9 Error mapping (§8, LLD-ADR-003)

- A gRPC server interceptor that turns library failures into gRPC status + `google.rpc.ErrorInfo` (`domain = "bloom"`) with the §8 reasons.
- A helper services use to raise `FAILED_PRECONDITION` with their own reason (for example `SOLD_OUT`).

### 6.10 Retention purger (§13)

- Daily worker job, deletes in batches of 1,000, with the exact §13 rules. Pending outbox rows are never deleted (T-19).

### 6.11 DLQ admin (§7.7, LLD-ADR-009)

- A `DeadLetterAdmin` gRPC service in a `.proto` shipped by the library, with HTTP annotations under `/api/admin/dlq/{service}/…`. A service hosts it with one registration call in its API. Exact HTTP paths and verbs under the prefix: builder's call.
- Everything else exactly as §7.7: Admin check from trusted headers, `{service}` must match, one pass with an exclusive consumer and no requeue, replay strips `x-attempt` / `x-failure-reason` / `x-failed-at`, the publish → audit commit → ACK order, the error reasons, the peek limit.
- The API process needs a RabbitMQ connection for this (previously workers only).
- *My call:* the library holds the payload record for `identity.staff-action-recorded.v1` (fields from 06-identity §4.1 `audit_log`: actor ID and role, service, action, target type and ID, details). A service publishes it on its own `<service>.events` exchange with routing key `identity.staff-action-recorded.v1`. How identity binds to every service's exchange belongs to the identity project.

### 6.12 Metrics, logs, traces (§12)

- All metrics in §12 with the listed names and labels, through `System.Diagnostics.Metrics`, labelled by `service`.
- *My call:* the names **as they appear in Prometheus** must be exactly the §12 names, with a `service` label. OTLP-to-Prometheus naming can add suffixes (`_total_total`, unit suffixes) and turn `service.name` into `job`, so the builder sets instrument names, units and collector settings to make the result match. One test or script checks the final names.
- `dlq_depth`: *builder's call* on how it is read (for example a passive queue declare per DLQ on an interval).
- Structured logs always carry `message_id`, `message_type`, `correlation_id`, `consumer` (when consuming) and trace IDs.
- Alert rules are not part of this project (no alerting config exists yet).

## 7. Shared telemetry setup (HLD §9.3, HLD-ADR-015)

- One startup extension every service calls with its service name.
- Traces, metrics and logs via OpenTelemetry; instrumentation for ASP.NET Core, gRPC client, EF Core / Npgsql and HttpClient, plus the library's own meters and activity sources.
- *Builder's call:* export everything over OTLP to the OpenTelemetry Collector. The collector forwards traces to Tempo, metrics to Prometheus and logs to Loki. The endpoint comes from standard OpenTelemetry environment variables.
- Trace context through RabbitMQ headers is the consumer host's and relay's job (§6.4, §6.6), so it uses the same `traceparent` the outbox stores.

## 8. Dev environment and CI

### 8.1 Docker Compose

- `deploy/compose/docker-compose.yml` with: PostgreSQL, RabbitMQ (management plugin), Redis, Mailpit, OpenTelemetry Collector, Tempo, Prometheus, Loki, Grafana (datasources provisioned for Tempo, Prometheus and Loki).
- *Builder's call:* a single RabbitMQ node (quorum queues work on one node; the 3-node cluster is the Kubernetes topology). One PostgreSQL instance; per-service databases are created by later projects.
- Pinned image versions, dev-only credentials in the file (no secrets exist yet).
- Verified with `docker compose config` only. The owner's rules forbid starting long-running services without an explicit request, so the stack is not started during the build.

### 8.2 CI

- `.github/workflows/ci.yml`: on push and pull request, set up .NET 10, restore, build, run the tests (Testcontainers on the Ubuntu runner).
- No path filters yet (there is only one buildable unit). Path-filtered per-service jobs arrive with the first service (HLD-ADR-014).

## 9. DLQ tool decision

Both documented forms of the DLQ tool conflicted with other docs. The owner chose an admin gRPC service per service and approved the design change on 2026-10-31: [LLD-ADR-009](../../../doc/low-level-design/adr/LLD-ADR-009-dlq-admin-endpoint.md), 00-reliability §7.7, §8, §11, §14 (T-20 … T-24), and the new route row in 07-api-gateway §5.1. The doc edits are on this branch.

## 10. Testing

- xUnit + Testcontainers with real PostgreSQL and RabbitMQ. One container of each per test run, isolated per test by database / vhost or unique names (builder's call).
- Every case T-01 … T-24 has at least one test named after its ID.
- **T-07** (5 attempts over ~2 min 36 s): *builder's call:* assert the routing per attempt (message lands in the right `.retry.<delay>` queue with the right `x-attempt`, and the 5th failure lands in the DLQ), verify the retry queues' TTL arguments, and run one real TTL round trip on the 1 s queue. This keeps the suite fast without making retry delays configurable.
- **T-09, T-20 … T-24** run through the `DeadLetterAdmin` gRPC service in-process (no gateway), with trusted headers set by the test.
- **T-14** (crash between provider call and tx 2): simulated by aborting after the provider call. The provider is a fake that deduplicates by key.
- Tests need Colima running locally. All test containers are removed after the run.

## 11. Left to the builder

Internal names, folder layout inside the project, public API shapes, prefetch value, how `dlq_depth` is read, test isolation strategy, package versions (latest stable compatible with .NET 10). Anything visible outside the library or affecting the design that the docs don't cover goes back to the owner (AGENTS.md §7).
