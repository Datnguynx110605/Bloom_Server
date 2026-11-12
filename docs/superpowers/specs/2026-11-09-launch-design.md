# Launch service: design

| | |
|---|---|
| **Status** | Approved (2026-11-09) |
| **Date** | 2026-11-09 |
| **Branch / PR base** | `feat/launch` → `fix/test-clock-precision` |
| **Implements** | [01-launch](../../../doc/low-level-design/01-launch.md) (all sections except L-17, L-18), LCH-R01 … R28 (launch side), BI-01, BI-03, HLD §5, §8.1, HLD-ADR-009, HLD-ADR-012, HLD-ADR-014 (API/worker split) |
| **Builds on** | `Bloom.Shared` ([shared-library spec](2026-10-31-shared-library-design.md)), gateway routes already in place ([api-gateway spec](2026-11-01-api-gateway-design.md): `GET /api/launches/{id}` (`public-optional-user`), `POST`/`GET /api/launches/{id}/waiting-room` (customer, `wr-join` / `wr-status` limits), `/api/admin/launches/**` (catalog_manager, admin), `/api/admin/dlq/launch/**` (admin)), the existing `Bloom.Launch.Contracts` (`launch_internal.proto`, `LaunchStageChangedV1`, `ReleaseAllocationHoldV1`, `HoldReleasedV1`), `OrderPaidV1` in `Bloom.Order.Contracts`, `StaffActionRecorded` in `Bloom.Identity.Contracts`, catalog's `GetSku` in `Bloom.Catalog.Contracts` |

## 1. Intent

`launch` runs launch events. It lets a limited number of customers through a fair waiting room and guarantees that pre-order allocation (BI-01) and per-account purchase limits (BI-03) are never exceeded (01-launch §1).

01-launch is the approved design, and this spec doesn't restate it. It records only the owner decisions and the builder calls the LLD leaves open. Where this spec is silent, 01-launch decides.

## 2. Goals

1. Launch **API** deployment:
   - Customer and public: `GetLaunch`, `JoinWaitingRoom`, `GetWaitingRoomStatus` (gRPC transcoded to JSON)
   - Hold: `HoldAllocation` for order (plain gRPC, existing `launch_internal.proto`, which already generates server stubs)
   - Admin: the nine admin methods of 01-launch §5.1, plus `AdminGetLaunch` and `AdminListLaunches` (§4)
   - the shared DLQ admin service (`MapBloomDeadLetterAdmin`, LLD-ADR-009)
2. Launch **worker** deployment:
   - the outbox relay
   - the admission worker (every 1 s per active launch, §6.4)
   - the stage scheduler (every 5 s, §6.1)
   - the consumers `launch.order-paid` and `launch.release-allocation-hold` (§6.6)
   - the stuck-hold monitor
   - the shared retention purger
3. Automated tests for L-01 … L-16 and L-19 … L-22, plus every edge case of 01-launch §8.

**Success criteria:** `dotnet build` passes with warnings as errors. `dotnet test` passes locally (Colima running) and in the existing CI workflow. Each test case in goal 3 has at least one test named after its ID. L-01 (1,000 concurrent holds, allocation 100) ends with exactly 100 holds and `held + sold = 100`.

## 3. Non-goals and anti-goals

**Non-goals:**
- **Load tests:** L-17 (100k queued, 20k status req/s) and L-18 (200 holds/s). These come later, as a separate project. The code must not rule them out: status never queries Postgres per request (LR-04), there's no per-customer state in process memory, and a hold is one transaction.
- **Infrastructure:** a Dockerfile, Compose entries, Kubernetes manifests, HPA, Linkerd allow rules, Sealed Secrets, and alert rules. Here, alerts are metrics plus error logs.
- **Client:** no browser client. Poll jitter is applied on the server (`poll_after_seconds`), and the client's use of it isn't built.
- **Cross-service tests:** no end-to-end tests across services. Catalog is a test-double gRPC server. Order's messages are published to the broker by the tests.
- **Other projects:** any change to `Bloom.Shared`, the gateway, or any other service, including the records in other services' `.Contracts` projects. `Bloom.Launch.Contracts` doesn't change: its proto already generates server stubs (`GrpcServices="Both"`).
- **`doc/`:** any change under it. The proposed edits are in §9.

**Anti-goals** (a failure even if it technically works):
- Any method, message, field or error reason that neither 01-launch nor this spec lists (AGENTS.md §1).
- `sold + held > allocation` for any launch SKU, or `sold + held > purchase_limit` for any (account, product), at any moment (LR-01, LR-02).
- A pass that enables two successful holds, or a failed hold that uses up a pass (LR-03).
- A customer popped from the queue without a pass record (§6.4).
- `GetWaitingRoomStatus` querying Postgres on the request path (LR-04).
- `launch` releasing a hold on its own (LR-07).
- A draft launch visible through `GetLaunch`.
- `account_id` taken from a request body (§9).
- A pass signing key in a log, a response, or an event.
- A test that reaches the internet.

## 4. Owner decisions

| Topic | Decision |
|---|---|
| Scope | All of 01-launch except L-17 and L-18 (load tests, later). Code and tests only. |
| Staff reads | Two new admin methods. `AdminGetLaunch` (`GET /api/admin/launches/{launch_id}`) returns a launch in any stage. `AdminListLaunches` (`GET /api/admin/launches`) lists launches in every stage, newest `created_at` first, paged with `page_size` (default 20, max 100) and an opaque `page_token`. Both return the **admin view**: every `launches` column (including `version` and `waiting_room_closed_at`), the products with `purchase_limit`, and the SKUs with `allocation`, `held` and `sold`. The list returns the same view without products and SKUs. Public `GetLaunch` returns `NOT_FOUND` for a draft or unknown launch. Doc edit §9.1. |
| Status when not queued | When the room is open and the customer has no pass and no queue position, `GetWaitingRoomStatus` returns `NOT_FOUND` with reason **`NOT_IN_QUEUE`**. The client treats it as "offer to join". The reason name is the builder's call. Doc edit §9.2. |
| One launch per product | A catalog product belongs to at most one launch, ever: a unique index on `launch_products.product_id`, like `sku_id`. `AddLaunchProduct` for a product already in any launch returns `VALIDATION_FAILED` (field `product_id`). `PublishLaunch` also requires at least one SKU per product. That check, together with `AddLaunchSku`'s catalog check that the SKU belongs to the product, means every published product is known to catalog, so `AddLaunchProduct` needs no catalog call. Reason: catalog keeps one launch per product (`LaunchStageChangedConsumer`). Doc edit §9.6. |
| Branch | `feat/launch` off `fix/test-clock-precision`, with the PR into `fix/test-clock-precision` (workflow rule: the base is the branch checked out when the work started). |

## 5. Layout and dependencies (builder's call)

```
src/Launch/
  Bloom.Launch/            # one ASP.NET Core app; runs as launch-api or launch-worker by config
  Bloom.Launch.Contracts/  # existing, unchanged: launch_internal.proto already generates server stubs
  Bloom.Launch.Tests/      # xUnit v3 + Testcontainers (PostgreSQL, RabbitMQ, Redis) + test-double catalog gRPC server
```

- One app with a mode setting (`api` / `worker`), like payment and notification (HLD-ADR-014).
- `AddBloom<LaunchDbContext>` provides the outbox, inbox, consumer host, retention purger, error mapping and metrics. `AddBloomTelemetry` provides OTel.
- EF Core migrations: the seven tables of 01-launch §4.1 exactly, with the `CHECK (held + sold <= allocation)`, the `≥ 0` checks, the unique `holds.order_id`, the unique `launch_skus.sku_id`, the unique `launch_products.product_id` (§4), the FK from `launch_skus (launch_id, product_id)` to `launch_products`, and the partial index on `holds (status, created_at) WHERE status = 'held'`, plus `AddBloomReliabilityTables`. Added index: `launches (stage)` for the scheduler and the admission worker's scan.
- Customer, public and admin protos live in the service (package `bloom.launch.v1`). `HoldAllocation` uses the existing `launch_internal.proto`.
- Redis: `StackExchange.Redis` (already in the repo). No new NuGet packages. New projects are added to `Bloom.slnx`.

## 6. Routes (builder's call)

| Surface | Methods → path |
|---|---|
| Public | `GetLaunch` `GET /api/launches/{launch_id}` |
| Customer | `JoinWaitingRoom` `POST /api/launches/{launch_id}/waiting-room`; `GetWaitingRoomStatus` `GET /api/launches/{launch_id}/waiting-room` |
| Admin (catalog_manager, admin) | `AdminListLaunches` `GET /api/admin/launches`; `CreateLaunch` `POST /api/admin/launches`; `AdminGetLaunch` `GET /api/admin/launches/{launch_id}`; `AddLaunchProduct` `POST …/{launch_id}/products`; `AddLaunchSku` `POST …/{launch_id}/skus`; `SetAllocation` `PUT …/{launch_id}/skus/{sku_id}/allocation`; `SetPurchaseLimit` `PUT …/{launch_id}/products/{product_id}/purchase-limit`; `SetReleaseDate` `PUT …/{launch_id}/release-date`; `SetLaunchTime` `PUT …/{launch_id}/launch-time`; `SetAdmissionRate` `PUT …/{launch_id}/admission-rate`; `PublishLaunch` `POST …/{launch_id}/publish` |
| `/api/admin/dlq/launch/**` | Shared DLQ admin |
| Internal, not routed | `HoldAllocation` (plain gRPC) |

- The bare `/api/admin/launches` sits behind the existing `/api/admin/launches/{**rest}` route, as order's `/api/admin/coupons` does. The builder confirms this with a request through a test gateway, or else with order's precedent. If it doesn't match, that's a gateway change, and it goes back to the owner.
- **Role strings** are the lowercase ones that the gateway and catalog use (`catalog_manager`, `admin`). The shared DLQ admin's own check is unchanged.
- **Roles** come from the trusted context (LLD 07 §4.3) and are checked again in launch. Join and status need `user_type = customer`. Admin methods need staff with `catalog_manager` or `admin`. `HoldAllocation` needs a forwarded customer context. A refusal returns `PERMISSION_DENIED` / `ROLE_NOT_ALLOWED`.
- **Every admin write returns the admin view** (§4) with the new `version`.

## 7. Implementation notes (builder's calls unless marked)

### Waiting room, state cache and passes

- **State cache `wr:{L}:state`** (§4.2): a JSON snapshot of the public view (id, name, stage, launch and release times, products, SKUs), `waiting_room_closed_at`, and a `sold_out` flag (every SKU has `held + sold = allocation`). TTL 5 s. A reader that misses reloads it from Postgres, with one load per pod per launch in flight at a time. This is the "refreshed from Postgres" of §4.2: at most one Postgres read per pod per launch every 5 s, never one per request (LR-04). Draft launches are cached with their stage and treated as not found.
- **`GetLaunch`** returns the public view plus the derived `waiting_room_state` (`not_open` \| `open` \| `sold_out` \| `closed`, §6.1). It has no counts and no `version`.
- **Join and status** each follow their own step order. Join (§6.2) checks the room's state first (`WAITING_ROOM_NOT_OPEN`, `WAITING_ROOM_CLOSED`, `sold_out`), then an existing pass. Status (§6.3) checks the pass first, so a live pass shows `admitted` even after the room closes. The hold then returns `LAUNCH_NOT_OPEN`, as 01-launch has it. Unknown and draft launches return `NOT_FOUND`. Join on `sold_out` returns `sold_out` and does not queue (§6.2 step 2). `poll_after_seconds` is a uniform random integer in 4 … 6.
- **Clock:** the app clock (`TimeProvider`) is used for join scores, the 15-min opening, and the derived state on the API. Database `now()` is used for every check inside a transaction: pass `exp` in the hold, the scheduler, the admission tick's `exp`, and admin date rules (00-reliability §10).
- **Pass keys** (§4.3): config `Launch:PassKeys` is a list of `{ kid, key }`, where `key` is base64 with at least 32 bytes, plus `Launch:CurrentPassKid`. New tokens are signed with the current key. A token verifies with any listed key. Rotating means adding the new key, switching the current kid, and removing the old key after 10 minutes. Startup fails if the current kid isn't listed or a key is too short. Comparison is constant-time. Keys are never logged.
- **Token parsing:** a malformed token, unknown `kid`, bad signature, other `aid` or `lid`, or a `lid` that names no launch all return `PASS_INVALID`. The launch for a hold comes from `lid`.
- **Room close cleanup:** after the transaction that sets `waiting_room_closed_at` (the admission tick or the scheduler) commits, delete `wr:{L}:q`, `wr:{L}:passes` and `wr:{L}:state`, best effort. Each worker tick retries the delete for closed launches that still have the keys.

### Admission and scheduler

- The admission worker scans `launches` with `stage = preorder_open AND waiting_room_closed_at IS NULL` every 1 s and runs §6.4 for each launch, exactly as written. The Lua script is the only code that pops the queue.
- The scheduler runs §6.1 every 5 s, exactly as written. Each transition is its own transaction.
- **Admission ceiling:** config `Launch:AdmissionCeiling`, default 200. It's checked by `CreateLaunch` and `SetAdmissionRate` (`VALIDATION_FAILED` above it, which is L-22's `INVALID_ARGUMENT`).

### Holds and settlement

- **`HoldAllocation`** follows §6.5 exactly, in one transaction, in its step order. Request validation (below) runs before step 1. A retry after the pass has expired returns `PASS_EXPIRED` even when the hold exists. Order's resumed checkout already compensates on a rejection (`CheckoutService.PlaceAsync`, `compensate: existed`), so the hold is released, not leaked. Validation (`VALIDATION_FAILED`, with the field in `ErrorInfo` metadata): `order_id` is a UUID; there's at least one line; no duplicate `sku_id`; every `sku_id` is a UUID; `quantity ≥ 1` (§9). `SKU_NOT_IN_LAUNCH` covers SKUs of other launches. The response is `hold_id` plus the stored lines. An idempotent retry compares lines as a set of `(sku_id, quantity)`.
- **Consumers** use the shared inbox. Queue names and bindings follow 00-reliability §6, with permanent failures sent to the DLQ. `order.paid.v1` with a null `LaunchId` is acknowledged with no effect. The release command writes `launch.hold-released.v1` in every case, including no-ops (§5.3).
- **Guarded updates** follow §6.5 and §6.6: single-statement conditional `UPDATE`s, an affected-row check, and `version + 1`, `updated_at = now()` on `holds`.
- **Stuck-hold monitor:** every 1 min, counts `held` holds older than 30 min per launch, sets `stuck_holds{launch}`, and logs an error when the count is above 0.

### Admin

- **Version:** every write to `launches`, including the scheduler's transitions and setting `waiting_room_closed_at`, adds 1 to `version` (00-reliability §7.5). Every admin write, including `AddLaunchProduct` and `AddLaunchSku` (which take no `version`), adds 1 to `launches.version`. Writes that take a `version` are conditional on it, and a mismatch returns `CONCURRENCY_CONFLICT`. The version is checked before the stage rules, because a stale version means the staff member's view of the stage may be stale too. So the `version` a staff member holds stays meaningful after any change to the launch.
- **Stage rules** follow §6.7. `INVALID_STAGE` covers a stage that doesn't allow the action. `ALLOCATION_DECREASE_NOT_ALLOWED` and `PURCHASE_LIMIT_LOCKED` follow §5.4.
- **`AddLaunchSku`:** calls catalog `GetSku` with a 3 s deadline (under the gateway's 5 s route timeout). The call fails with `VALIDATION_FAILED` (field `sku_id`) if catalog returns `NOT_FOUND`, if the SKU's `product_id` differs from the request's, if the product isn't in this launch yet, or if the SKU is already in a launch (catalog's `launch_id` is set, or the unique `sku_id` conflicts). Catalog unreachable returns `UNAVAILABLE` (§5.2).
- **Other validation** (`VALIDATION_FAILED`): a non-empty name, valid UUIDs, `purchase_limit > 0`, `allocation ≥ 0`, and the date rules of §6.7.
- **Audit:** every admin write puts `identity.staff-action-recorded.v1` in the outbox, in the same transaction, with the shape that the other services already send.
- **`launch.stage-changed.v1`:** written in the same transaction by every stage transition. From `announced` on, it is also written by every admin write that changes a field in its payload (products and their limits, SKUs, release date). Draft changes publish nothing. Doc edit §9.4.

### Metrics

Every metric in §10, with its labels. `waiting_room_size` and `passes_outstanding` are read by the admission tick. `allocation_available` is set after holds, settlements and admin changes. `admission_ticks_total{result}` takes `admitted` \| `skipped_lock` \| `none`.

## 8. Testing

- xUnit v3 with Testcontainers for PostgreSQL, RabbitMQ and Redis. A test-double catalog gRPC server can be scripted for `GetSku`: found, not found, other product, in a launch, unavailable.
- Tests call the API in process (gRPC and transcoded JSON, with trusted-context headers) and run worker jobs directly or as hosted services.
- Tests publish `order.paid.v1` and `launch.release-allocation-hold.v1` to the broker, and read `launch.stage-changed.v1`, `launch.hold-released.v1` and the audit event off it.
- **L-01, L-03:** real concurrency against one PostgreSQL instance (parallel tasks, separate connections), followed by an invariant check.
- **L-05:** 10,000 accounts join before launch time. The order of admission must differ from the order of joining, and their rank correlation must be close to 0.
- **L-07:** two worker instances tick the same launch at the same moment.
- **Time:** `FakeTimeProvider` drives the app clock. Rows that a check compares with the database's `now()` are seeded relative to it, at whole microseconds (see the recent clock-precision fix).
- **Coverage:** edge cases §8, the two new admin methods, `NOT_IN_QUEUE`, a draft returning `NOT_FOUND` from `GetLaunch`, pass key rotation (an old key still verifies; an unlisted kid returns `PASS_INVALID`), and room cleanup.
- Tests need Colima running locally. All containers are removed after the run.

## 9. Proposed doc edits (not applied)

No edit under `doc/` is made in this project (AGENTS.md §9). These are for the owner to approve separately:

1. **01-launch §5.1:** add `AdminGetLaunch` and `AdminListLaunches` (Catalog manager, Admin; the admin view of §4). `GetLaunch` returns `NOT_FOUND` for a draft or unknown launch.
2. **01-launch §5.4 and §6.3:** add `NOT_IN_QUEUE` (`NOT_FOUND`). The room is open and the customer has no pass and no position. It replaces "otherwise `not_open`/'not in queue'" in §6.3 step 3.
3. **01-launch §5.4:** the shared reasons used are `NOT_FOUND`, `VALIDATION_FAILED`, `ROLE_NOT_ALLOWED`, `CONCURRENCY_CONFLICT`. Admission rate above the ceiling is `VALIDATION_FAILED` (L-22's `INVALID_ARGUMENT`).
4. **01-launch §6.7:** every write to `launches` adds 1 to `version`, including `AddLaunchProduct`, `AddLaunchSku` and the scheduler's transitions. The version is checked before the stage rules. From `announced` on, a change to a limit or to the release date also publishes `launch.stage-changed.v1`, because both are in its payload.
5. **01-launch §4.2:** `wr:{L}:state` holds the public view and the `sold_out` flag. A miss reloads it once per pod per launch.
6. **01-launch §4.1 and §6.7:** `launch_products.product_id` is unique across launches, and a product can never be in two launches. `AddLaunchProduct` rejects a product that is already in a launch. `PublishLaunch` also requires ≥ 1 SKU per product.

## 10. Left to the builder

Internal names, class and proto layout, request and response shapes beyond the fields 01-launch and this spec name, config key names other than those named here, page-token format, the Lua script's text, and test isolation. Anything visible outside launch, or anything affecting the design, that 01-launch and this spec don't cover goes back to the owner (AGENTS.md §7).

## 11. Open items (owner)

None. The builder check raised one owner question (one launch per product, §4). The other points were settled as builder calls: join and status check order, the hold's step order on an expired-pass retry, version bumps by the scheduler, version checked before stage, a 3 s catalog deadline, and no change to the contracts project.
