# Local load-test stack and k6 load / spike / stress — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** One Compose overlay that runs all 9 Bloom services (API + worker each) with fakes on this machine, and k6 load / spike / stress scripts with design latency thresholds at machine-fitted, env-driven rates.

**Architecture:** One multi-stage Dockerfile builds every service image and a migrator image (EF bundles). `deploy/loadtest/docker-compose.yml` is layered on the untouched dev file and adds per-service Redis, one-shots (db-init, migrate, seed-staff), the 18 service containers, a small ASP.NET fakes app (Stripe + Google), k6 and an invariant `verify` one-shot. k6 runs in a CPU-capped container on the Compose network and spreads synthetic client IPs through the gateway's trusted-proxy `X-Forwarded-For`.

**Tech Stack:** .NET 10 (sdk/aspnet 10.0 Debian images), Docker Compose v2 (installed: v5.7.0), PostgreSQL 17.6, RabbitMQ 4.1.4, Redis 8.2.3, k6 (`grafana/k6`, pinned), Prometheus remote write, xunit.v3.

**Spec:** `docs/superpowers/specs/2026-11-17-loadtest-stack-design.md` (approved). Read it before any task; §-references below are to it.

## Global Constraints

- Never edit `deploy/compose/*` or anything under `doc/` (spec §6, AGENTS.md §9). The overlay overrides what it needs.
- No change to service code, endpoints, messages, data model or `appsettings.json` (spec §2 non-goals). Everything is env config.
- Gateway rate limits stay at production values; never override `Gateway__RateLimits__*` (spec §2 anti-goals, §5.3).
- Docker VM budget: all non-k6 containers together **≤ 6 CPUs and ≤ 9 GB**; k6 **2 CPUs / 1.5 GB** (spec §5.2). Every container sets `cpus` and `mem_limit`.
- .NET containers: `DOTNET_gcServer=0` (spec §5.2).
- Every service: `Kestrel__Endpoints__Http__Url=http://+:8080`, `…__Http__Protocols=Http1`, `Kestrel__Endpoints__Grpc__Url=http://+:8081`, `…__Grpc__Protocols=Http2` (spec §5.2, §7.5). Peer gRPC URLs use `:8081`; gateway clusters stay `http://<svc>:8080` via network aliases (spec §7.7).
- Origin sent by k6 and allowed everywhere: `http://loadtest.bloom.test`.
- Every rate, VU/user count and duration in k6 comes from `__ENV` with a default (spec G3). No machine number is hard-coded outside a default.
- Thresholds: `http_req_duration{flow:catalog}` p95 < 150, `{flow:checkout_launch}` p95 < 300, `{flow:wr_status}` p95 < 50 (ms), `http_req_failed` < 1 % in load (spec §5.8).
- Throwaway keys in `loadtest.env` are labelled dev-only at the top of the file (spec §5.6).
- Compose resolves **every relative path in every `-f` file against the first file's directory** (`deploy/compose/`). In the overlay, the repo root is `../..` and loadtest files are `../loadtest/...`.
- All compose commands in this plan use (Git Bash):
  `LT="docker compose -p bloom-lt --env-file deploy/loadtest/loadtest.env -f deploy/compose/docker-compose.yml -f deploy/loadtest/docker-compose.yml"`
- Docker usage: Docker Desktop here (not Colima). Never touch the engine. Bring the stack down between tasks if not needed; Task 7 ends with full cleanup (`$LT --profile loadtest down -v --remove-orphans`, remove `bloom-lt-*` images).
- Fakes app and its test project are **not** added to `Bloom.slnx`. Central package management (`Directory.Packages.props`) and `TreatWarningsAsErrors` apply to them: versionless `PackageReference`s only.

## Decisions made in this plan (owner reviews with the plan)

- P1. Dockerfile build args are `PROJECT` (project dir) and `ASSEMBLY` (dll name) instead of `SERVICE`, so the fakes app reuses the same Dockerfile (spec §9 lets the builder adjust names).
- P2. Databases are named `bloom_<svc>` (`order` is a reserved word). Inventory's app role: `inventory_app` / `inventory-app-loadtest`.
- P3. RabbitMQ gets `RABBITMQ_DEFAULT_USER/PASS=bloom/bloom` in the overlay and every service uses `amqp://bloom:bloom@rabbitmq:5672` — the default `guest` user can't connect from another container.
- P4. Customers sign in **lazily, once per VU** in its first iteration (tag `flow:auth`, no latency threshold), not in `setup()`: setup data is copied to every VU, and thousands of tokens there would cost the generator more than the sign-ins. Accounts are still pre-created by `seed.js` (spec §5.5).
- P5. Fake Stripe sends webhooks with `X-Forwarded-For` taken round-robin from a pool of `Fakes__Stripe__WebhookIps` (default 12) synthetic addresses, standing in for Stripe's own pool of webhook source IPs; the `webhook` limit (600/min per IP) stays as is. It retries a failed delivery 3 times (1 s, 2 s, 4 s) like Stripe does, and counts failures at `GET /_stats`; k6 `teardown()` fails the run if any webhook was finally undelivered.
- P6. Service images install `curl` for Compose healthchecks (the aspnet image has none).
- P7. `verify` waits `VERIFY_SETTLE_SECONDS` (default 30) before querying, so in-flight sagas settle.

## Review Focus

1. **`X-Forwarded-For` silently ignored** (trusted subnet mismatch): every request then shares k6's one IP, the global 300/min limit trips, and results measure 429s. Expected: a stack-level check proves per-IP budgets are independent before any test runs (Task 3, Step 6).
2. **Token expiry in runs longer than the access-token lifetime** (stress): expected per-VU refresh, never a 401 storm. Pinned by a forced-refresh short run (Task 5, Step 6).
3. **Generator saturation:** k6 dropping iterations means the numbers measure k6. Expected: `dropped_iterations` threshold `count==0` in load and spike-recovery (Task 5, Task 6).
4. **Re-running on an already-seeded stack:** expected `seed.js` is idempotent and every test run creates a fresh launch product/SKU (a SKU can be in one launch ever). Pinned by running seed twice and load twice (Task 4 Step 5, Task 5 Step 5).
5. **Webhook delivery failing** (429 or 5xx): orders would silently expire and BI checks still pass while the payment saga is untested. Expected: run fails (P5) (Task 2 test, Task 5 teardown).

---

### Task 1: Container images (services + migrator)

**Files:**
- Create: `deploy/docker/Dockerfile`
- Create: `deploy/docker/migrate.sh`
- Create: `.dockerignore`

**Interfaces:**
- Produces: image targets `app` (build args `PROJECT`, `ASSEMBLY`; listens per env; runs `dotnet $ASSEMBLY.dll`) and `migrate` (runs `migrate.sh`, which applies the 8 EF bundles). `migrate.sh` reads `PG_HOST`, `PG_USER`, `PG_PASSWORD` and migrates databases `bloom_identity bloom_catalog bloom_inventory bloom_launch bloom_order bloom_payment bloom_fulfillment bloom_notification` with `./<svc> --connection "Host=$PG_HOST;Database=bloom_<svc>;Username=$PG_USER;Password=$PG_PASSWORD"`.

- [ ] **Step 1: Write `.dockerignore`** excluding `**/bin`, `**/obj`, `.git`, `doc`, `docs`, `**/TestResults`, `.vs`, `.idea`, `deploy/loadtest/results`, `deploy/loadtest/k6`.

- [ ] **Step 2: Write `deploy/docker/Dockerfile`**

  - `build` stage `mcr.microsoft.com/dotnet/sdk:10.0`: copy repo, `dotnet publish $PROJECT -c Release -o /out` with `RUN --mount=type=cache,target=/root/.nuget/packages`.
  - `app` stage `mcr.microsoft.com/dotnet/aspnet:10.0`: `apt-get install -y --no-install-recommends curl` (P6), copy `/out`, `ENV APP_DLL=${ASSEMBLY}.dll`, `ENTRYPOINT ["sh","-c","exec dotnet \"$APP_DLL\""]`.
  - `migrate-build` stage (sdk): `dotnet tool restore` (uses `.config/dotnet-tools.json`, dotnet-ef 10.0.15), then for each of Identity Catalog Inventory Launch Order Payment Fulfillment Notification: `dotnet ef migrations bundle --project src/<Svc>/Bloom.<Svc> --configuration Release --output /bundles/<svc-lower> --force`.
  - `migrate` stage (aspnet): copy `/bundles` and `migrate.sh`, entrypoint `migrate.sh`.
  - Catalog's and Identity's design-time factories ignore env; `--connection` overrides them, so all 8 bundles take it.

- [ ] **Step 3: Write `deploy/docker/migrate.sh`** (`#!/bin/sh`, `set -eu`, loop as in Interfaces, echo each database before migrating). Commit it with LF line endings (`git add --chmod=+x`).

- [ ] **Step 4: Build and smoke the images**

  Run: `docker build -f deploy/docker/Dockerfile --target app --build-arg PROJECT=src/Order/Bloom.Order --build-arg ASSEMBLY=Bloom.Order -t bloom-lt-order .`
  Then: `docker run --rm bloom-lt-order`
  Expected: exits non-zero with a message containing `Order:Mode` or `Order:Database` (app loaded and validated config). Not a missing-assembly or libsodium error.

  Run: `docker build -f deploy/docker/Dockerfile --target migrate -t bloom-lt-migrate .`
  Expected: build succeeds; `docker run --rm --entrypoint ls bloom-lt-migrate /bundles` lists 8 bundles.

- [ ] **Step 5: Commit**

```bash
git add .dockerignore deploy/docker
git commit -m "build(loadtest): service and migrator images"
```

---

### Task 2: Fakes app (Stripe + Google)

**Files:**
- Create: `deploy/loadtest/fakes/Bloom.LoadTest.Fakes/Bloom.LoadTest.Fakes.csproj` (Sdk.Web)
- Create: `deploy/loadtest/fakes/Bloom.LoadTest.Fakes/Program.cs`, `StripeFake.cs`, `Webhooks.cs`, `GoogleFake.cs`
- Test: `deploy/loadtest/fakes/Bloom.LoadTest.Fakes.Tests/Bloom.LoadTest.Fakes.Tests.csproj`, `FakesTests.cs`

**Interfaces:**
- Consumes: Stripe request/response shapes of `src/Payment/Bloom.Payment.Tests/Infrastructure/FakeStripe.cs` (port its store, `CreateSession`, `Replay`, `List`, JSON shapes; drop `Fail`, `TimeoutNext`, `Before`, `Unserved`); event shape and signing of `StripeEvents.Json/Sign/CompletedSession`; Google token shape of `src/Identity/Bloom.Identity.Tests/Infrastructure/FakeGoogle.cs`.
- Produces (HTTP, port 8080):
  - The 8 Stripe endpoints under `/v1/...` exactly as FakeStripe serves them (`checkout/sessions` create/get/expire, `refunds` create/get/list, `payment_intents` list), real clock.
  - `GET /google/certs` → JWKS; `POST /google/id-token` body `{"email":"…","name":"…"}` → `{"idToken":"…"}` (RS256, `iss=https://accounts.google.com`, `aud=Fakes:Google:ClientId`, `sub="lt-"+lowercase email`, `email_verified=true`, 1 h expiry).
  - `GET /_stats` → `{"sessionsCreated":n,"webhooksDelivered":n,"webhooksFailed":n}`.
- Config: `Fakes:Stripe:WebhookUrl`, `Fakes:Stripe:WebhookSecret`, `Fakes:Stripe:PayFraction` (default 1.0), `Fakes:Stripe:PayDelayMs` (default 2000), `Fakes:Stripe:WebhookIps` (default 12), `Fakes:Google:ClientId`.
- Behaviour: on each new session (not on idempotent replays), with probability `PayFraction`, after `PayDelayMs` the `Webhooks` background service marks the session `complete`/`paid`, adds a succeeded payment intent, and POSTs `checkout.session.completed` (body per `StripeEvents.Json` + `CompletedSession` with the session's `metadata`), `Stripe-Signature` per `StripeEvents.Sign`, `X-Forwarded-For: 100.127.0.<1..WebhookIps>` round-robin. Non-2xx or exception → retry after 1 s, 2 s, 4 s; then `webhooksFailed++`.

- [ ] **Step 1: Write the failing tests** (`FakesTests.cs`, host the app with `Microsoft.AspNetCore.TestHost`; the test project references `src/Payment/Bloom.Payment/Bloom.Payment.csproj` for `WebhookSignature` and the fakes project; packages as `Bloom.Payment.Tests.csproj`: `xunit.v3`, `Microsoft.AspNetCore.TestHost`, `Microsoft.EntityFrameworkCore.Design`, plus `Microsoft.IdentityModel.JsonWebTokens`). A webhook receiver is a Kestrel app on `127.0.0.1:0` inside the test.

```csharp
[Fact] CreateSession_rejects_expiry_under_30_minutes()      // expires_at = now+29min → 400, error.code "parameter_invalid_integer"
[Fact] CreateSession_replays_same_idempotency_key()         // same key + same form twice → same id, sessionsCreated == 1
[Fact] CreateSession_same_key_other_body_is_idempotency_error() // 400, error.type "idempotency_error"
[Fact] Paid_session_posts_signed_completed_webhook()
    // PayDelayMs=0; create with metadata[order_id], metadata[payment_id]; receiver gets 1 POST within 5 s:
    // WebhookSignature.Verify(header, body, secret, DateTimeOffset.UtcNow) == true;
    // body type == "checkout.session.completed", data.object.metadata.order_id == the sent one;
    // X-Forwarded-For starts with "100.127.0."; GET v1/checkout/sessions/{id} then shows status "complete".
[Fact] PayFraction_zero_sends_no_webhook()                  // receiver gets nothing within 1 s; session stays "open"
[Fact] Undeliverable_webhook_is_counted_after_retries()     // receiver answers 429; within 10 s /_stats webhooksFailed == 1 and receiver saw 4 attempts
[Fact] Google_id_token_validates_against_jwks()
    // JsonWebTokenHandler.ValidateTokenAsync with keys from GET /google/certs, ValidIssuer accounts.google.com,
    // ValidAudience = ClientId → IsValid; claims email, email_verified == true, sub == "lt-" + email.ToLowerInvariant()
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test deploy/loadtest/fakes/Bloom.LoadTest.Fakes.Tests`
Expected: FAIL (project / types missing).

- [ ] **Step 3: Implement** the four files per Interfaces. `Program.cs` exposes `public partial class Program` for TestHost. Delivery uses a `Channel<string>` of session ids and a named `HttpClient`.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test deploy/loadtest/fakes/Bloom.LoadTest.Fakes.Tests`
Expected: 7 passed, 0 warnings.

- [ ] **Step 5: Build its image**

Run: `docker build -f deploy/docker/Dockerfile --target app --build-arg PROJECT=deploy/loadtest/fakes/Bloom.LoadTest.Fakes --build-arg ASSEMBLY=Bloom.LoadTest.Fakes -t bloom-lt-fakes .`
Expected: success.

- [ ] **Step 6: Commit**

```bash
git add deploy/loadtest/fakes
git commit -m "test(loadtest): fake Stripe and Google for the load-test stack"
```

---

### Task 3: Compose stack up and healthy

**Files:**
- Create: `deploy/loadtest/loadtest.env`, `deploy/loadtest/postgres-init.sql`, `deploy/loadtest/seed-staff.sql`, `deploy/loadtest/docker-compose.yml`
- Modify: `.gitignore` (add `deploy/loadtest/results/`)

**Interfaces:**
- Consumes: images from Tasks 1–2 (the overlay `build:`s them: context `../..`, dockerfile `deploy/docker/Dockerfile`).
- Produces: gateway on host `localhost:8088`; on the network: `gateway:8080`, `fakes:8080`, `<svc>:8080/8081`, `prometheus:9090`. Env vars in `loadtest.env` that k6 and later tasks read: `LT_ADMIN_EMAIL=admin@loadtest.bloom.test`, `LT_ADMIN_PASSWORD`, `LT_ADMIN_TOTP_BASE32` (32 chars), `LT_ORIGIN=http://loadtest.bloom.test`, `LT_SUBNET=172.30.0.0/16`, plus every service secret as `LT_*`.

- [ ] **Step 1: Generate the throwaway secrets into `loadtest.env`.** Header comment: "DEV-ONLY throwaway keys for the local load-test stack. Used by nothing else. Never reuse in any environment." Values generated once like `TestSecrets.Create()`: P-256 PEM (single line, double-quoted, `\n` escapes), `CodePepper`, `CodeEncryptionKey` (shared Identity/Notification), `TotpKey`, Data Protection PFX base64, Launch pass key (32 bytes, kid `lt-1`), `LT_STRIPE_API_KEY=sk_test_loadtest_fake`, `LT_STRIPE_WEBHOOK_SECRET=whsec_loadtest_fake`, `LT_GOOGLE_CLIENT_ID=loadtest-google-client`, Shippo dummies, admin password and TOTP base32 secret.

- [ ] **Step 2: Write `postgres-init.sql`**: `CREATE DATABASE` for the 8 `bloom_<svc>` names (P2); `CREATE ROLE inventory_app LOGIN PASSWORD 'inventory-app-loadtest'`. Idempotent (`\gexec` guards or `DO` blocks) so `db-init` can rerun.

- [ ] **Step 3: Write `seed-staff.sql`**: one `INSERT INTO accounts (...) VALUES (...) ON CONFLICT (email_normalized) DO NOTHING` for `LT_ADMIN_EMAIL`, `type='staff'`, `role='admin'`, `status='active'`, `password_hash` = Argon2id of `LT_ADMIN_PASSWORD` via `PasswordHasher.Hash(PasswordPolicy.Normalize(pw))`, `totp_secret_enc` = `new SecretBox(TotpKey).Seal(base32-decoded LT_ADMIN_TOTP_BASE32)` as `'\x…'::bytea`, as `Seed.StaffAsync` does. Generate with a throwaway console program referencing `src/Identity/Bloom.Identity` in the scratchpad (not committed); comment at the top of the SQL says what it contains and that it must be regenerated if `LT_TOTP_KEY` changes. Column names: check `src/Identity/Bloom.Identity/Data/Migrations` for the exact snake_case names.

- [ ] **Step 4: Write `deploy/loadtest/docker-compose.yml`**

  - `networks: default: ipam: config: [{subnet: 172.30.0.0/16}]` (pick another /16 if `docker network ls` shows a clash; then also change `LT_SUBNET`).
  - Overrides of dev services: `prometheus.command` = `--config.file=/etc/prometheus/prometheus.yml --web.enable-remote-write-receiver`; `postgres.command` raises `max_connections` (600); `rabbitmq` env per P3; `redis` moved to `profiles: [dev-only]`; host ports `!reset []` on postgres, minio, otel-collector; keep 15672 (rabbitmq UI), 8025 (mailpit), 9090, 3000. Every container `cpus`/`mem_limit`.
  - New: `gateway-redis`, `catalog-redis`, `identity-redis`, `launch-redis` (`--appendonly yes`), `notification-redis`.
  - One-shots (`restart: "no"`): `db-init` (postgres image, `psql -v ON_ERROR_STOP=1 -f /init.sql`, waits for postgres healthy), `migrate` (image target `migrate`, after `db-init` completed), `seed-staff` (psql against `bloom_identity`, after `migrate` completed). Add a `pg_isready` healthcheck on postgres.
  - Services: an `x-dotnet` anchor (image build, `DOTNET_gcServer=0`, Kestrel endpoints, `OTEL_EXPORTER_OTLP_ENDPOINT=http://otel-collector:4317`, `OTEL_METRIC_EXPORT_INTERVAL=5000`, `depends_on` one-shots `service_completed_successfully`, rabbitmq). `<svc>-api` (aliases `[<svc>]`, `<Svc>__Mode=api`, healthcheck `curl -fsS http://localhost:8080/readyz` for gateway, `/healthz` otherwise — use `/readyz` where the service maps it) and `<svc>-worker` (`__Mode=worker`, `/healthz`). Config per service from the Options classes (`src/<Svc>/Bloom.<Svc>/<Svc>Options.cs` `Validate()`); peer URLs on `:8081`; `Database` = `Host=postgres;Database=bloom_<svc>;Username=bloom;Password=bloom;Maximum Pool Size=30` (Inventory as `inventory_app`); `Notification__Email__Provider=smtp`, `…__Smtp__Host=mailpit`, `…__Smtp__Port=1025`; `Gateway__TrustedProxies__0=${LT_SUBNET}`, `Gateway__AllowedOrigins__0=${LT_ORIGIN}`, `Notification__AllowedOrigins__0=${LT_ORIGIN}`; `Identity__Google__JwksUrl=http://fakes:8080/google/certs`; `Payment__Stripe__BaseUrl=http://fakes:8080/`. Gateway publishes `8088:8080`.
  - `fakes` (Task 2 image, env per its Interfaces, `WebhookUrl=http://gateway:8080/webhooks/stripe`).
  - `k6` (`profiles: [loadtest]`, `grafana/k6:<pinned>` — pick a tag ≥ 1.0 that `docker pull` resolves, no `latest`; `cpus: 2`, `mem_limit: 1.5g`; volumes `../loadtest/k6:/scripts:ro`, `../loadtest/results:/results`; env `BASE_URL=http://gateway:8080`, `FAKES_URL=http://fakes:8080`, `ORIGIN`, admin creds, `K6_OUT=experimental-prometheus-rw`, `K6_PROMETHEUS_RW_SERVER_URL=http://prometheus:9090/api/v1/write`, `K6_PROMETHEUS_RW_TREND_STATS=p(95),p(99),avg`).
  - Starting CPU split (tune in Task 7, stay in budget): postgres 1.25, gateway-api 0.75, catalog/order/launch/identity api 0.5 each, other apis 0.25, workers 0.15 (launch-worker, order-worker, payment-worker 0.25), rabbitmq 0.5, redis 0.1 each, observability 0.5 total.

- [ ] **Step 5: Bring it up and check health and budget**

Run: `$LT up -d --build --wait`
Expected: exit 0; `$LT ps` shows all long-running services `healthy`, one-shots `Exited (0)`.

Run (PowerShell): `$c = (docker compose -p bloom-lt --env-file deploy/loadtest/loadtest.env -f deploy/compose/docker-compose.yml -f deploy/loadtest/docker-compose.yml config --format json | ConvertFrom-Json).services.PSObject.Properties | ? { $_.Name -ne 'k6' -and -not $_.Value.profiles }; ($c.Value | Measure-Object cpus -Sum).Sum; ($c.Value | Measure-Object mem_limit -Sum).Sum / 1GB`
Expected: ≤ 6 and ≤ 9.

- [ ] **Step 6: Smoke the wiring** (each must hold before Task 4)

  - `curl -s -o /dev/null -w "%{http_code}" localhost:8088/api/catalog/categories` → `200`.
  - h2c peer gRPC: `$LT logs inventory-api launch-api order-api identity-api` show no `HTTP_1_1_REQUIRED` / `Http2` protocol errors after the smoke in Task 4; here, `docker run --rm --network bloom-lt_default curlimages/curl:8.11.0 -s --http2-prior-knowledge -o /dev/null -w "%{http_version}" http://catalog:8081/` prints `2`.
  - Trusted proxy (Review Focus 1): from `curlimages/curl:8.11.0` on `bloom-lt_default`, 11 `POST http://gateway:8080/api/auth/sign-in` with `X-Forwarded-For: 100.64.9.9` and an invalid body → the 11th is `429`; one more with `X-Forwarded-For: 100.64.9.10` → not `429`.
  - `$LT logs fakes` clean; `$LT logs payment-worker identity-worker` no startup exceptions.

- [ ] **Step 7: Bring it down and commit**

```bash
$LT down
git add .gitignore deploy/loadtest/loadtest.env deploy/loadtest/postgres-init.sql deploy/loadtest/seed-staff.sql deploy/loadtest/docker-compose.yml
git commit -m "build(loadtest): compose stack with per-service config, one-shots and fakes"
```

---

### Task 4: k6 library, seed and invariant verifier

**Files:**
- Create: `deploy/loadtest/k6/lib/config.js`, `ip.js`, `totp.js`, `http.js`, `auth.js`, `admin.js`
- Create: `deploy/loadtest/k6/seed.js`, `deploy/loadtest/k6/test/lib.test.js`
- Create: `deploy/loadtest/verify/verify.sh`, `verify-launch.sql`, `verify-order.sql`
- Modify: `deploy/loadtest/docker-compose.yml` (add `verify` one-shot, `profiles: [loadtest]`, postgres image, mounts `../loadtest/verify:/verify:ro`, `command: sh /verify/verify.sh`)

**Interfaces:**
- Consumes: Task 3 env (`BASE_URL`, `FAKES_URL`, `ORIGIN`, `LT_ADMIN_*`); HTTP routes from the protos (JSON is proto3 camelCase; int64 such as `version` are strings): catalog admin `src/Catalog/Bloom.Catalog/Protos/admin.proto` (CreateCategory, CreateProduct, CreateSku, SetSkuPrice/Active as needed, PublishProduct), inventory `warehouse.proto` (RecordGoodsReceipt), launch `launch.proto` (CreateLaunch, AddLaunchProduct, AddLaunchSku, PublishLaunch, AdminGetLaunch), identity `auth.proto` (SignIn, SignInWithGoogle), `staff.proto` (VerifyStaffMfa). Publish preconditions: `doc/low-level-design` launch and catalog LLD §6.
- Produces (ES modules):
  - `config.js`: `num(name, def) -> number`, `str(name, def) -> string`, `export const BASE_URL, FAKES_URL, ORIGIN`.
  - `ip.js`: `customerIp(i) -> string` = `100.64.0.0 + i` (i < 2^21); `anonIp(n) -> string` = `100.96.0.0 + (n % ANON_IPS)`, `ANON_IPS` env default 4096; `ADMIN_IP = "100.127.255.1"`.
  - `totp.js`: `totp(base32Secret, unixSeconds) -> string` (6 digits, HMAC-SHA1, 30 s step, RFC 6238).
  - `http.js`: `api(method, path, body, { token, ip, flow, headers, expect }) -> Response` — JSON body, `Origin`, `X-Forwarded-For: ip`, `Authorization: Bearer`, tags `{ flow }`, `responseCallback` = `expectedStatuses(expect ?? [200, 201, 204])`.
  - `auth.js`: `adminToken() -> string` (sign-in → staff MFA with `totp(...)`); `customer(i) -> { i, ip, token, cookie, issuedAt }` (`POST FAKES_URL/google/id-token` with `customer{i}@loadtest.bloom.test` → `POST /api/auth/google`, keep `bloom_rt` from `Set-Cookie`); `fresh(session) -> session` (when `now - issuedAt > REFRESH_AFTER_S` (default 600) → `POST /auth/refresh` with manual `Cookie: bloom_rt=…` and `Origin`, flow `auth`).
  - `admin.js`: `poolProduct(token, n) -> { productId, skuId }` (idempotent by name `lt-pool-<n>`: search first, create only if absent); `receive(token, skuId, qty)`; `freshLaunch(token, { delaySeconds, admissionRate, purchaseLimit, allocation, stock }) -> { launchId, productId, skuId, launchTime }` (new product `lt-launch-<timestamp>` + SKU + stock + launch + publish); `launch(token, launchId) -> AdminLaunch`.
  - `seed.js`: `CUSTOMERS` (default 5000), `POOL` (default 100), `POOL_STOCK` (default 1000000); creates category `lt-pool` if absent, `POOL` products via `poolProduct`, tops stock up to `POOL_STOCK`, signs in every customer once (shared-iterations, `SEED_VUS` default 50). Idempotent.
  - `verify.sh`: `sleep ${VERIFY_SETTLE_SECONDS:-30}`, then `psql -v ON_ERROR_STOP=1` `verify-launch.sql` on `bloom_launch` and `verify-order.sql` on `bloom_order`; exit non-zero on any violation; prints counts checked.
  - SQL checks (each `DO $$ … RAISE EXCEPTION '<check>: <offending row>' … $$`):
    - BI-01: `launch_skus` with `held + sold > allocation`; and per SKU, sum of `hold_lines.quantity` over `holds` in `held`/`sold` > `allocation`, or ≠ `held + sold`.
    - BI-03: `limit_usage` with `held + sold > launch_products.purchase_limit`; and the same recount from holds per (account, product).
    - Idempotency: `holds` grouped by `order_id` having > 1 row; in `bloom_order`, `orders` with `launch_id IS NOT NULL` not having exactly one `checkout_keys` row.

- [ ] **Step 1: Write the failing self-test `k6/test/lib.test.js`** (`thresholds: { checks: ['rate==1'] }`, 1 iteration):

```js
check(totp('GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ', 59),         { 'rfc6238 t=59': v => v === '287082' });
check(totp('GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ', 1111111109), { 'rfc6238 t=1111111109': v => v === '081804' });
check(customerIp(0),   { 'first customer': v => v === '100.64.0.0' });
check(customerIp(257), { 'carries into third octet': v => v === '100.64.1.1' });
check(anonIp(4096),    { 'anon wraps at ANON_IPS': v => v === '100.96.0.0' });
```

- [ ] **Step 2: Run it to verify it fails**

Run: `$LT run --rm k6 run /scripts/test/lib.test.js`
Expected: FAIL (module not found).

- [ ] **Step 3: Implement the lib modules, `seed.js`, the verifier and the `verify` service** per Interfaces.

- [ ] **Step 4: Run the self-test**

Run: `$LT run --rm k6 run /scripts/test/lib.test.js`
Expected: `checks 100%`, exit 0.

- [ ] **Step 5: Seed twice (Review Focus 4) and verify**

Run: `$LT up -d --wait && $LT run --rm -e CUSTOMERS=200 k6 run /scripts/seed.js && $LT run --rm -e CUSTOMERS=200 k6 run /scripts/seed.js && $LT run --rm verify`
Expected: both seeds exit 0 with no failed checks; the second creates no products (log says `0 created`); verify exits 0. Admin sign-in with TOTP succeeded (proves `seed-staff.sql`).

- [ ] **Step 6: Commit**

```bash
git add deploy/loadtest/k6 deploy/loadtest/verify deploy/loadtest/docker-compose.yml
git commit -m "test(loadtest): k6 helpers, idempotent seed and BI-01/BI-03 verifier"
```

---

### Task 5: Flows and `load.js`

**Files:**
- Create: `deploy/loadtest/k6/lib/flows.js`, `deploy/loadtest/k6/load.js`

**Interfaces:**
- Consumes: Task 4 lib. Routes: catalog `public.proto` (ListCategories, SearchProducts, GetProduct by id and by-slug, ListReviews); order `order.proto` (GetCart, UpsertCartLine `{kind:"product", skuId, quantity, version}`, ListMyOrders, StartCheckout with `Idempotency-Key` header (UUID) and `cart`/`launch` source, `shipTo`, `shippingOption:"standard"`); launch `launch.proto` (JoinWaitingRoom, GetWaitingRoomStatus: `state`, `pollAfterSeconds`, `passToken`); notification `notification.proto` (GetWsTicket) and hub `/api/notifications/hub?ticket=…` (SignalR JSON: send `{"protocol":"json","version":1}\x1e`, answer type-6 pings, see `src/Notification/Bloom.Notification.Tests/Infrastructure/Sockets.cs`).
- Produces: `flows.js` exports, each taking `data` = `setup()` result `{ launchId, launchProductId, launchSkuId, pool: [{productId, skuId, slug}] }`:
  - `catalogFlow(data)` — anonymous, `ip = anonIp(exec.scenario.iterationInTest)`, all requests `flow:catalog`.
  - `mixedFlow(data)` — `session = fresh(cached ?? customer(exec.vu.idInTest - 1))` (P4; `fail()` if index ≥ `CUSTOMERS`); browse (`flow:catalog`), upsert cart line (`flow:cart`), get cart (`flow:cart`), list orders (`flow:orders`); with probability `CHECKOUT_RATIO` (default 0.05) checkout the cart (`flow:checkout_regular`).
  - `launchFlow(data)` — join (`flow:wr_join`), poll status sleeping `pollAfterSeconds` (`flow:wr_status`) until `admitted` / `sold_out` / `closed` / scenario end; on `admitted` start launch checkout (`flow:checkout_launch`), then repeat the same request with the same `Idempotency-Key` and `check` the same `orderId` (`checkout_idempotent`).
  - `wsFlow(data)` — ticket (`flow:ws_ticket`), open the hub with `k6/websockets` (`Origin`, ticket in the query), handshake, hold `WS_HOLD_SECONDS`, reconnect with 1–5 s jitter if closed early; custom metrics `ws_connect_ok` (Rate), `ws_connect_ms` (Trend), `ws_sessions` (Gauge or Counter of opened sessions).
- Produces: `load.js` with env defaults (placeholder until Task 7): `CATALOG_RATE=50`, `MIXED_RATE=10`, `WS_CONNECTIONS=200`, `LAUNCH_USERS=300`, `LAUNCH_DELAY=120`, `ALLOCATION=150`, `PURCHASE_LIMIT=1`, `ADMISSION_RATE=20`, `RAMP=1m`, `HOLD=8m`; scenarios: `catalog` and `mixed` `ramping-arrival-rate` (ramp/hold/down), `ws` `constant-vus`, `launch` `per-vu-iterations` (1 iter, `maxDuration` = whole test). `setup()`: admin token, `freshLaunch(...)`, pool from `GET /api/catalog/products?q=lt-pool`. `teardown()`: `GET FAKES_URL/_stats` and `fail()` if `webhooksFailed > 0` (P5). Thresholds: Global Constraints latency three + `http_req_failed: rate<0.01` + `ws_connect_ok: rate>0.99` + `checks: rate>0.99` + `checkout_idempotent: rate==1` + `dropped_iterations: count==0`. `handleSummary` writes stdout text and `/results/load-<ISO>.json`.

- [ ] **Step 1: Write a short-run smoke config as the failing test**: the command in Step 2 with tiny numbers.

- [ ] **Step 2: Run it to verify it fails**

Run: `$LT run --rm -e RAMP=10s -e HOLD=40s -e CATALOG_RATE=5 -e MIXED_RATE=2 -e WS_CONNECTIONS=10 -e LAUNCH_USERS=20 -e ALLOCATION=10 -e LAUNCH_DELAY=30 k6 run /scripts/load.js`
Expected: FAIL (script missing).

- [ ] **Step 3: Implement `flows.js` and `load.js`** per Interfaces.

- [ ] **Step 4: Run the smoke and verify**

Run: the Step 2 command, then `$LT run --rm verify`
Expected: all thresholds pass, exit 0; summary shows `flow:checkout_launch` requests > 0, `ws_connect_ok` 100 %, no 429 in `http_reqs` by status; verify exit 0; `$LT logs order-worker payment-api` show orders paid (fake webhook reached payment).

- [ ] **Step 5: Run the smoke again** (Review Focus 4) — same command. Expected: exit 0 (fresh launch SKU per run).

- [ ] **Step 6: Forced refresh (Review Focus 2)** — Step 2 command with `-e REFRESH_AFTER_S=20 -e HOLD=60s`. Expected: exit 0, `flow:auth` refresh requests > 0 in the summary, 0 responses with status 401.

- [ ] **Step 7: Commit**

```bash
git add deploy/loadtest/k6
git commit -m "test(loadtest): catalog, mixed, launch and WebSocket flows and the load test"
```

---

### Task 6: `spike.js` and `stress.js`

**Files:**
- Create: `deploy/loadtest/k6/spike.js`, `deploy/loadtest/k6/stress.js`, `deploy/loadtest/k6/lib/knee.js`
- Modify: `deploy/loadtest/k6/test/lib.test.js` (knee cases)

**Interfaces:**
- Consumes: `flows.js`, `admin.js` (Task 4–5).
- Produces:
  - `knee.js`: `knee(steps, maxErrorRate) -> { knee: number|null, broke: number|null, firstTag: string|null }` where `steps = [{ step, errorRate, tags: { [flow]: { p95, target } } }]` ordered by step; a step meets SLO when every `p95 < target` and `errorRate < 0.01`; it breaks when any `p95 > 2 * target` or `errorRate > maxErrorRate`; `knee` = last step meeting SLO before the first broken step; `firstTag` = the tag with the highest `p95 / target` in the broken step (`"errors"` when only the error rate broke).
  - `spike.js`: env `BASE_MIXED_RATE`, `BASE_CATALOG_RATE`, `SPIKE_USERS` (default 2000), `SPIKE_WS` (default 1000), `LAUNCH_DELAY` (default 180), `SPIKE_HOLD` (180 s), `RECOVERY` (180 s), `ALLOCATION`, `ADMISSION_RATE`. Scenarios: `baseline_catalog`/`baseline_mixed` (constant-arrival-rate until `LAUNCH_DELAY + SPIKE_HOLD`), `spike_launch` (`SPIKE_USERS` per-vu-iterations, ramping in over the first 60 s), `spike_ws` (ramping-vus to `SPIKE_WS`), `recovery_catalog`/`recovery_mixed` (same rates, `startTime = LAUNCH_DELAY + SPIKE_HOLD`, duration `RECOVERY`, scenario tag `phase:recovery`). Thresholds: during spike `http_req_failed: rate<0.05`, `http_req_failed{status:5xx}` via a custom `server_errors` Rate `< 0.01`; on `phase:recovery` the three latency thresholds and `http_req_failed{phase:recovery} < 0.01`, `dropped_iterations{phase:recovery}` none. `teardown()`: launch state is sold out or `held + sold`/expired holds account for the whole allocation (`AdminGetLaunch`), and webhook check (P5).
  - `stress.js`: env `STEPS` (default 10), `STEP_DURATION` (default 2m), `CATALOG_STEP`, `MIXED_STEP`, `WS_STEP`, `STATUS_STEP` (users added per step), `MAX_ERROR_RATE` (0.05). One scenario per (flow, step) with `startTime = i * STEP_DURATION`, tag `step:i`; launch with `ALLOCATION` large and `ADMISSION_RATE=1` so most users stay queued and poll. Thresholds per step and SLO tag: `p(95)<2×target` with `abortOnFail: true, delayAbortEval: '30s'`, plus `http_req_failed{step:i}: rate<MAX_ERROR_RATE` abortOnFail. `handleSummary` builds `steps` from the summary's sub-metrics, calls `knee`, prints `KNEE step=… BROKE step=… FIRST=…` with each step's rates, and writes `/results/stress-<ISO>.json`. `teardown()` has the webhook check (P5). BI checks still via `verify`.

- [ ] **Step 1: Add the failing knee cases to `lib.test.js`**

```js
const t = { catalog: 150, wr_status: 50 };
const s = (step, cat, st, err = 0) => ({ step, errorRate: err, tags: { catalog: { p95: cat, target: t.catalog }, wr_status: { p95: st, target: t.wr_status } } });
check(knee([s(1,100,30), s(2,140,45), s(3,320,60)], 0.05), { 'knee before latency break': r => r.knee === 2 && r.broke === 3 && r.firstTag === 'catalog' });
check(knee([s(1,100,30), s(2,200,40), s(3,140,120)], 0.05), { 'over SLO but not broken is not the knee': r => r.knee === 1 && r.broke === 3 && r.firstTag === 'wr_status' });
check(knee([s(1,100,30, 0.2)], 0.05),                          { 'errors break step 1': r => r.knee === null && r.broke === 1 && r.firstTag === 'errors' });
check(knee([s(1,100,30), s(2,120,40)], 0.05),                  { 'never broke': r => r.knee === 2 && r.broke === null });
```

- [ ] **Step 2: Run to verify it fails** — `$LT run --rm k6 run /scripts/test/lib.test.js` → FAIL (`knee` missing).

- [ ] **Step 3: Implement `knee.js`, `spike.js`, `stress.js`.**

- [ ] **Step 4: Self-test passes** — same command → `checks 100%`.

- [ ] **Step 5: Short spike and stress runs, then verify**

Run: `$LT run --rm -e LAUNCH_DELAY=40 -e SPIKE_HOLD=40 -e RECOVERY=40 -e SPIKE_USERS=100 -e SPIKE_WS=50 -e ALLOCATION=30 -e BASE_CATALOG_RATE=5 -e BASE_MIXED_RATE=2 k6 run /scripts/spike.js && $LT run --rm verify`
Expected: exit 0 for both.

Run: `$LT run --rm -e STEPS=3 -e STEP_DURATION=40s -e CATALOG_STEP=10 -e MIXED_STEP=2 -e WS_STEP=20 -e STATUS_STEP=20 k6 run /scripts/stress.js && $LT run --rm verify`
Expected: output contains a `KNEE` line; `/results/stress-*.json` exists; verify exit 0.

- [ ] **Step 6: Commit**

```bash
git add deploy/loadtest/k6
git commit -m "test(loadtest): spike and stress tests with knee report"
```

---

### Task 7: Calibration, README and teardown

**Files:**
- Modify: defaults in `deploy/loadtest/k6/load.js`, `spike.js`, `stress.js`; CPU/memory split in `deploy/loadtest/docker-compose.yml` if calibration shows a starved container
- Create: `deploy/loadtest/README.md`

**Interfaces:**
- Consumes: everything above.
- Produces: calibrated defaults and a README with: prerequisites (Docker Desktop, VM 8 CPU / ~11.7 GB, stop the dev stack first: host ports clash), the `$LT` command, start (`up -d --build --wait`), seed, the three run commands each chained with `verify`, how to read the summary and the `KNEE` line, Grafana/Prometheus (k6 metrics via remote write, Explore), whether Tempo/Loki were stopped for runs and how, the measured knee, chosen defaults and machine/budget, the dev-only keys warning (spec §5.6), how `seed-staff.sql` was generated and when to regenerate it, and full teardown (`$LT --profile loadtest down -v --remove-orphans` and `docker image rm` of the `bloom-lt-*` images).

- [ ] **Step 1: Seed at full size** — `$LT up -d --wait && $LT run --rm k6 run /scripts/seed.js` (default `CUSTOMERS`). Expected: exit 0.

- [ ] **Step 2: Find the knee** (spec §5.9.1) — `$LT run --rm k6 run /scripts/stress.js && $LT run --rm verify`; raise `*_STEP` or `STEPS` until a step breaks. Watch `docker stats --no-stream` during the run: if k6 is at its 2-CPU cap, or one container is CPU-pinned while the system has headroom, adjust the split within budget and rerun. Expected: a `KNEE` line with a broken step that is a Bloom container, not k6.

- [ ] **Step 3: Set defaults** — load ≈ 50–60 % of the knee per flow; spike baseline = load, peak ≈ knee (spec §5.9.2–3). `CUSTOMERS` default ≥ the largest VU count any default run needs.

- [ ] **Step 4: Load passes twice in a row** (spec §5.9.5) — `$LT run --rm k6 run /scripts/load.js && $LT run --rm verify`, twice. Expected: both exit 0. Then the spike at defaults + verify: exit 0.

- [ ] **Step 5: Write the README** per Interfaces with the measured numbers.

- [ ] **Step 6: Full cleanup** — `$LT --profile loadtest down -v --remove-orphans`, `docker image rm` every `bloom-lt-*` image and the `grafana/k6` and `curlimages/curl` images this work pulled. Expected: `docker ps -a --filter name=bloom-lt` and `docker volume ls --filter name=bloom-lt` are empty.

- [ ] **Step 7: Commit**

```bash
git add deploy/loadtest
git commit -m "docs(loadtest): calibrated defaults and README"
```
