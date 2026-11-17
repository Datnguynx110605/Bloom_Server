# Local load-test stack and k6 load / spike / stress tests — design

Status: draft for owner review · Date: 2026-11-17 · Branch: `feat/loadtest-stack` (base `main`)

## 1. Intent

Bloom deploys to the cloud (HLD §9.5, HLD-ADR-014). This machine is a **test bench**: it must run the whole backend
end to end and drive it with load, spike and stress tests **sized for this machine**, not for the production targets
in HLD §3.1 (20k status reads/s, 200 checkouts/s, 100k WebSockets). Those targets are checked later in the cloud with
the same scripts and bigger numbers.

Machine: 8 cores / 16 GB host; Docker Desktop VM has **8 CPUs, ~11.7 GB**. The k6 installed on the host is not used (k6
runs in a container, §5).

## 2. Goals, non-goals, anti-goals

**Goals**
- G1. One Compose stack that runs all 9 services (API + worker containers each, HLD §9.5), their dependencies and fake
  external providers, within the Docker VM's budget.
- G2. k6 scripts for **load**, **spike** and **stress**, covering four flows: launch path, catalog reads, normal mixed
  traffic, notification WebSockets.
- G3. Every rate, VU count, user count and duration comes from environment variables, so the cloud can reuse the scripts.
- G4. Pass/fail (owner decision): rates fitted to this machine, **design latency targets kept as thresholds**
  (QA-02 catalog p95 < 150 ms, QA-03 checkout start p95 < 300 ms, QA-04 status p95 < 50 ms), plus error rate and
  zero BI-01 / BI-03 violations. Load must pass them; spike must recover to them; stress has no latency gate and reports
  where it breaks.

**Non-goals**
- Kubernetes manifests, cloud infra, HA topology (CNPG replica, 3-node RabbitMQ, Linkerd, KEDA). Owner chose Compose only.
- Running load tests in CI.
- Fulfillment/shipping flows (Shippo's base URL is hard-coded, `ShippoClient.cs:9`; checkout never reaches it).
- Returns, refunds, coupons, reviews writes, staff flows beyond seeding.
- Any change under `doc/` and any change to service behaviour, endpoints, messages or data model (AGENTS.md §1, §7).

**Anti-goals** (failure even if it "works")
- Tests that pass only because limits, auth or validation were switched off. The gateway rate limits stay at production
  values (owner decision); auth uses real tokens from Identity.
- A k6 generator so starved or so greedy that numbers measure the generator, not Bloom.
- Scripts with this machine's numbers hard-coded so they can't scale to the cloud.

## 3. Constraints (from the code, see the recon notes in §9)

- Services never migrate on start; Inventory's migration needs role `inventory_app` first.
- Peer gRPC is h2c prior-knowledge over `http://`; Kestrel's default cleartext endpoint is HTTP/1.1 only.
- No first-admin bootstrap exists; staff sign-in needs TOTP.
- Gateway limits: global 300/min per IP, auth 10/min per IP, per-user buckets (status 30/min, join 5/min,
  checkout 6/min). k6 sends from one container IP.
- Refresh cookie `bloom_rt` is `Secure`, and `/auth/refresh` and the hub need an allowed `Origin`.
- Launch: `launch_time` must be in the future at publish; waiting room opens 15 min before; stage changes and admissions
  run only in the launch worker (1 s admission tick, 5 s stage tick); `admissionRate ≤ Launch:AdmissionCeiling`.
- Server-side custom histograms use OTel default buckets (no views configured), so Prometheus p95s for sub-second SLOs
  are coarse. Changing that is a code/design change → out of scope (parked, §8).

## 4. Approaches considered

| | Approach | Trade-off |
|---|---|---|
| **A (chosen)** | k6 as a CPU-capped container on the Compose network; gateway trusts that network for `X-Forwarded-For`; data seeded through real APIs; k6 metrics to Prometheus | Most Docker memory used; cleanest numbers, generator budget is explicit |
| B | k6 on the Windows host against `localhost` | Less overhead, but k6 competes uncapped with the Docker VM for the same cores → noisy; trusting the host as proxy is awkward |
| C | NBomber (C#) project | Same language, but commercial licence for organisations, weaker WS/threshold support; k6 already installed |

Dropped: merging API + worker into one container (contradicts HLD §9.5; the split is part of what's under test).
Owner picked **A**.

## 5. Design

### 5.1 Files (builder may adjust names)

```
deploy/docker/Dockerfile                 one multi-stage Dockerfile, build-arg SERVICE=<Svc>
.dockerignore
deploy/compose/docker-compose.yml        existing dev deps — NOT edited; the loadtest file overrides what it needs
                                         (e.g. prometheus `command` adding the remote-write receiver)
deploy/loadtest/docker-compose.yml       the app stack + fakes + k6, layered on the dev file (-f ... -f ...)
deploy/loadtest/loadtest.env             dev-only config and throwaway keys (see §5.6)
deploy/loadtest/postgres-init.sql        8 databases + inventory_app role
deploy/loadtest/seed-staff.sql           one admin account with known password and TOTP secret
deploy/loadtest/fakes/                   small ASP.NET app: fake Stripe + fake Google
deploy/loadtest/k6/lib/*.js              shared flows, auth, IP spreading, config
deploy/loadtest/k6/{seed,load,spike,stress}.js
deploy/loadtest/README.md                how to start, seed, run, read results, tear down
```

The fakes app and k6 scripts are not added to `Bloom.slnx` (they're test tooling, not services).

### 5.2 Containers and budget

Docker VM budget: **system ≤ 6 CPUs / ≤ 9 GB, k6 = 2 CPUs / 1.5 GB**, remainder is headroom. Every container gets
`cpus` and `mem_limit`. .NET containers use workstation GC (`DOTNET_gcServer=0`) to keep memory per container small.

| Group | Containers |
|---|---|
| Data | postgres (raised `max_connections`), rabbitmq (single node, quorum queues work), redis per service as the appsettings name them: `gateway-redis`, `catalog-redis`, `identity-redis`, `launch-redis` (AOF on, HLD §9.5), `notification-redis`; minio + minio-init; mailpit |
| One-shot | `db-init` (runs `postgres-init.sql`), `migrate` (applies all 8 services' EF migrations, e.g. efbundles or `dotnet ef database update`, as owner), `seed-staff` (runs `seed-staff.sql`) |
| Services | `<svc>-api` and `<svc>-worker` for gateway, identity, catalog, inventory, launch, order, payment, fulfillment, notification (18 containers, one image per service, `<Svc>__Mode`) |
| Fakes | `fakes` (Stripe + Google, §5.4) |
| Observability | existing otel-collector, prometheus (with remote-write receiver enabled for k6), grafana; Tempo and Loki kept but may be stopped during runs if the budget needs it (builder's call, documented in README) |
| Load | `k6` (Compose profile `loadtest`, image `grafana/k6`, pinned version) |

Ports: each service listens on **8080 HTTP/1.1** (health, gateway-forwarded JSON, WebSocket hub) and **8081 h2c** (peer
gRPC), set via `Kestrel__Endpoints__*` env. Peer URLs (`Order__Catalog`, `Launch__Catalog`, …) point at `:8081`; gateway
cluster addresses stay at `:8080`. Builder verifies once that h2c works before wiring everything (recon pitfall 3).

Only the gateway (and Grafana, Mailpit, RabbitMQ UI, Prometheus) publish host ports.

Notification uses `Email:Provider=smtp` → Mailpit (no Resend fake needed). `OTEL_EXPORTER_OTLP_ENDPOINT` points at the
collector with a short metric export interval.

Health: Compose `healthcheck` on `/healthz` (workers) and `/readyz` (APIs; gateway's readyz waits for JWKS). Services
`depends_on` the one-shots completing.

### 5.3 Gateway trust and IP spreading (owner decision: real limits)

- The loadtest network gets a fixed subnet; `Gateway__TrustedProxies__0=<that subnet>`.
- k6 sends `X-Forwarded-For: <synthetic IP>` derived deterministically from the virtual user / account index, so each
  simulated customer has its own IP and its own per-user budgets.
- Rate limit values are **not** overridden. `Gateway:AllowedOrigins` and `Notification:AllowedOrigins` include the Origin
  k6 sends (e.g. `http://loadtest.bloom.test`).
- A 429 is a test failure unless the scenario deliberately exceeds a user's budget (none do by default: pacing in every
  flow stays inside the per-user limits, e.g. status polling honours `pollAfterSeconds` 4–6 s).

### 5.4 Fakes (owner: providers may be fake)

One small ASP.NET minimal-API app (`deploy/loadtest/fakes`):

- **Stripe**: `POST /v1/checkout/sessions` (the only call on the checkout path), returning `id`, `status=open`,
  `expires_at`, `url`, `amount_total`, honouring `Idempotency-Key`, and rejecting `expires_at < now+30min` like
  `FakeStripe`. Optional worker paths (`GET/expire` sessions, refunds, payment_intents lists) return valid minimal
  bodies so payment's worker doesn't error. After a configurable delay and for a configurable fraction of sessions
  (default 100 %, delay ~2 s), it **POSTs a signed `checkout.session.completed` webhook** to the gateway
  (`/webhooks/stripe`, `Stripe-Signature` per `WebhookSignature.cs`, `metadata` echoed from the create request) — this
  plays the customer paying, so the payment → order saga runs under load. Fraction 0 leaves orders to expire.
- **Google**: serves a JWKS and `POST /google/id-token {email,name}` that mints an RS256 ID token
  (`iss=https://accounts.google.com`, `aud=<ClientId>`, `email_verified=true`). Identity's `Google:JwksUrl` points here.
  Customers then sign in through the **real** `POST /api/auth/google`, which creates a real verified customer and
  returns a real access token + refresh cookie. No password hashing, no email round trip.

### 5.5 Seeding

- **Admin**: `seed-staff.sql` inserts one `admin` staff row with a precomputed Argon2id hash of a known test password and
  a TOTP secret encrypted with the fixed dev `Identity:TotpKey` (format per `Seed.StaffAsync`, `SecretBox.cs`). The
  builder generates the hash/ciphertext once with a throwaway program and commits the SQL. k6 signs in as admin with
  `sign-in` → `staff/mfa`, computing the TOTP code in JS (HMAC-SHA1, `k6/crypto`).
- **`seed.js`** (run once, idempotent): creates a category, ~100 published products with SKUs (catalog read pool),
  receives stock for them, and signs in `CUSTOMERS` customers (default sized to the largest stress step, e.g. 5k) via the
  Google fake so the accounts exist.
- **Per test run** (k6 `setup()`): admin creates a fresh launch product + SKU + stock + launch
  (`launchTime = now + LAUNCH_DELAY`, `admissionRate`, `purchaseLimit`, `allocation` from env) and publishes it; signs in
  the customers needed for this run (Google fake → access token + refresh cookie). Tests longer than ~14 min refresh each
  VU's own token (never two concurrent refreshes of one token; cookie sent as a manual `Cookie` header with `Origin`).

### 5.6 Secrets

All keys in `loadtest.env` are **throwaway dev-only values** generated once and committed, labelled as such and used by
nothing but this stack: Identity signing key (P-256 PEM), `CodePepper`, `CodeEncryptionKey` (shared with Notification),
`TotpKey`, Data Protection PFX, Launch pass key, Stripe API key / webhook secret (fake), Google client id, MinIO creds
(existing). *Builder's call: committing them keeps the stack one-command; it is safe only because no cloud environment
uses them — README says so.*

### 5.7 Flows (shared by all three tests)

| Flow | What a virtual user does | Measured as |
|---|---|---|
| **Catalog reads** (anonymous) | list categories, search (`q`, category, sort, page), product page by id/slug, reviews | `catalog` tag → QA-02 |
| **Normal mixed** (signed-in customer) | sign in (Google), browse, add to cart (`PUT /api/cart/lines`), view cart, list orders, occasional regular checkout of a stocked SKU | `cart`, `orders`, `checkout_regular` tags |
| **Launch path** | join waiting room before launch, poll status every `pollAfterSeconds`, on `admitted` start launch checkout with the pass (`Idempotency-Key`, `shipTo`), fake Stripe pays | `wr_join`, `wr_status` → QA-04; `checkout_launch` → QA-03 |
| **WebSockets** | get ticket, open `/api/notifications/hub?ticket=…` with Origin, SignalR JSON handshake, answer pings, hold for the test duration (reconnect with jitter if dropped) | connect time, connect success rate, sessions held |

Latency thresholds are evaluated **in k6** on client-observed time per tag (end to end through the gateway; the fake
Stripe call adds ~ms, so this is slightly stricter than QA-03 which excludes payment, HLD-ADR-021).

### 5.8 The three tests

All numbers below are **env-driven defaults**; the builder fixes the defaults by calibration (§5.9).

| Test | Shape | Pass/fail |
|---|---|---|
| **load.js** | Steady ~10 min (ramp 1 min, hold 8, down 1): catalog reads + normal mixed at a constant arrival rate, `WS_CONNECTIONS` held, and a small launch running (users in the room polling, admitted users checking out) | catalog p95 < 150 ms, launch checkout p95 < 300 ms, status p95 < 50 ms, `http_req_failed` < 1 %, WS connect success > 99 %, BI-01 / BI-03 hold |
| **spike.js** | The launch opening: baseline normal traffic; `SPIKE_USERS` join in the 15-min pre-window (compressed: `LAUNCH_DELAY` a few minutes), WS surge at the same time, then at `launch_time` everyone polls and admission starts; hold ~3 min; then traffic falls back to baseline for ~3 min | During the spike: error rate < 5 %, no 5xx storms (5xx < 1 %), BI-01 / BI-03 hold, allocation sold out or fully held/expired at the end. In the **recovery** window: the same latency thresholds as load, on the `phase:recovery` tag |
| **stress.js** | Stepwise ramp of the combined profile (catalog + mixed + status polling against an open launch + WS) by `STEP` every `STEP_DURATION` until a break condition: p95 of any SLO tag > 2× its target or error rate > 5 % for a step; k6 aborts on break (`abortOnFail`, with delay) | No latency gate. Output: highest step that met all SLO thresholds (the **knee**), the step that broke, which tag broke first. BI-01 / BI-03 still hold (a correctness failure fails the run) |

**Invariant check** (`verify` one-shot, run after spike/stress/load): SQL against launch and order databases:
- BI-01: per launch SKU, sold + held ≤ allocation.
- BI-03: per (account, launch product), bought + held ≤ purchase limit.
- Plus: every successful launch checkout k6 recorded maps to exactly one order (no duplicates for one Idempotency-Key).

Exit code non-zero on any violation. The README's run commands chain k6 then `verify`.

### 5.9 Calibration rule (how defaults are chosen)

1. Run `stress.js` on this machine; take the knee.
2. Set `load.js` defaults to ≈ 50–60 % of the knee per flow.
3. Set `spike.js` baseline = load level, spike peak ≈ the knee or slightly above (it is allowed to degrade during the spike,
   must recover after).
4. Record the measured knee, the chosen defaults and the machine (CPU/RAM, Docker budget) in the README.
5. Load must pass with these defaults on two consecutive runs.

### 5.10 Results

- k6 end-of-test summary (stdout) is the pass/fail; `--summary-export` JSON saved to `deploy/loadtest/results/`
  (gitignored).
- k6 metrics via Prometheus remote write → visible in Grafana next to the services' OTel metrics (Explore; no custom
  dashboard).
- README explains the run commands, how to read the summary, and full teardown
  (`docker compose ... down -v --remove-orphans`, remove built images).

## 6. Decided by the owner

- Compose test stack only (no k8s manifests now).
- Flows: launch path, catalog reads, normal mixed, WebSockets.
- Pass/fail: design latency thresholds at machine-fitted rates; stress reports the knee.
- Real gateway rate limits; per-user `X-Forwarded-For` via trusted proxy network.
- Approach A: k6 in Compose.
- External providers may be fake.
- The builder **may run** the stack on Docker Desktop (`docker compose up -d`) and k6 against it to verify and calibrate
  (§5.9); this is the explicit request CLAUDE.md §5 requires. The Docker engine itself is never touched. At the end all
  containers, volumes, networks and built images are removed (CLAUDE.md §6). The existing `deploy/compose` files are not
  edited; the loadtest overlay overrides what it needs.

## 7. Calls made by the designer (owner may override)

1. Customers via the **Google sign-in fake** (real accounts, real tokens) instead of email sign-up through Mailpit
   (Argon2 + code limits make seeding thousands slow) or self-minted JWTs (no real accounts; `/api/me` breaks).
2. Admin seeded by **SQL with precomputed hash/ciphertext** (no bootstrap exists; spec 2026-11-01-identity open item).
3. **Fake Stripe auto-pays** via signed webhook so the payment saga is loaded too.
4. **Throwaway dev keys committed** in `loadtest.env` (§5.6).
5. Two Kestrel endpoints per service (8080 HTTP/1.1, 8081 h2c), set by env only — no code change.
6. Latency measured in k6, not from server histograms.
7. Gateway cluster addresses stay as in appsettings (`http://<svc>:8080`): API containers get network aliases
   `<svc>`, so no cluster overrides are needed.

## 8. Parked (out of scope, recorded for later)

- Server histograms use default OTel buckets → coarse Prometheus p95 for QA-02..04. Needs an owner-approved code change
  (bucket advice or views).
- Shippo base URL not configurable → fulfillment can't be load-tested against a fake without a code change.
- Cloud Kubernetes manifests and a cloud-sized run of these scripts.

## 9. Left to the builder

File names and layout within `deploy/`, Dockerfile details (sdk/aspnet 10.0 Debian images, libsodium comes via NuGet),
the exact migration mechanism, per-container CPU/memory split inside the budget, k6 script structure and helpers,
default numbers (by §5.9), and README wording.

Recon notes the builder should read first: per-service required config keys are in each `<Svc>Options.cs`
(`Validate()`); test secret generators in `Identity.Tests/Infrastructure/TestSecrets.cs`; Stripe fake behaviour in
`Payment.Tests/Infrastructure/FakeStripe.cs` and webhook events in `StripeEvents.cs`; Google fake in
`Identity.Tests/Infrastructure/FakeGoogle.cs`; SignalR raw-WS client in `Notification.Tests/Infrastructure/Sockets.cs`;
admin/staff seed in `Identity.Tests/Infrastructure/Seed.cs`; launch admin API in `src/Launch/Bloom.Launch/Protos/launch.proto`;
checkout body in `order.proto` `StartCheckoutRequest`.
