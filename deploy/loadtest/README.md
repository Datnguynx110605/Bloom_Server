# Bloom load-test stack

A local, throwaway copy of the whole Bloom backend (gateway and the 8 services, each as an `-api` and a `-worker`
container) with fake Stripe and Google, plus k6 tests that measure it through the gateway: `load.js` (steady load),
`spike.js` (a launch opening) and `stress.js` (find the knee). Design: `docs/superpowers/specs/2026-11-17-loadtest-stack-design.md`.

**DEV-ONLY KEYS.** `loadtest.env` holds throwaway keys (identity signing key, pepper, TOTP key, Data Protection PFX, launch pass key,
fake Stripe/Google/Shippo secrets, the admin password) and the admin TOTP secret in `seed-staff.sql`. They are committed so the stack
starts with one command. They are safe only because nothing outside this stack uses them. Never reuse any of them in another
environment, and never point this stack at real providers (spec 5.6).

## Prerequisites

- Docker Desktop with the VM at **8 CPUs and about 11.7 GB**. The stack is capped at 6 CPUs / 9 GB, k6 at 2 CPUs / 1.5 GB, so the
  VM has no spare CPU: close other heavy programs while measuring.
- **Stop the dev stack first** (`deploy/compose`): both publish host ports 3000 (Grafana), 8025 (Mailpit UI), 9090 (Prometheus) and
  15672 (RabbitMQ UI). This stack also publishes the gateway on 8088.
- Git Bash (the commands below). Prefix every compose command with `MSYS_NO_PATHCONV=1`, or Git Bash rewrites the container paths.

```bash
cd <repo root>
export MSYS_NO_PATHCONV=1
LT="docker compose -p bloom-lt --env-file deploy/loadtest/loadtest.env -f deploy/compose/docker-compose.yml -f deploy/loadtest/docker-compose.yml"
```

## Start

```bash
$LT up -d --build --wait      # first run builds 11 images and migrates 8 databases (several minutes)
```

If `--wait` ends with a failed worker, run `$LT up -d --wait` again: RabbitMQ needs about 70 s after a (re)create before it really
accepts connections, even when it reports healthy. **Wait that long before a run.**

Never use plain `$LT run ...` for the tests: it re-runs the one-shot services (migrate, ...) and can recreate Postgres under a
running test. Always `run --rm --no-deps`, as below. And never edit the compose file between a start and a run without `up -d`
and the 70 s wait.

### Postgres on a slow-fsync VM (read this once)

The VM disk of Docker Desktop here does an fsync in about 17 ms (`pgbench` with `pg_logical_emit_message`: 17.5 ms per commit) and a Postgres checkpoint
once spent 21 s in its fsync phase (the Postgres log line `checkpoint complete: ... sync=20.943 s`). Every checkout is 6 to 8 sequential commits, so with the
default (`synchronous_commit=on`) `checkout_launch` p95 is bounded by the disk (300 to 470 ms in the early calibration runs, 216 ms with the final settings),
and a checkpoint freezes everything for seconds (see Calibration: the one run with it on had 809 dropped iterations and 504s). The recorded runs
used the commit wait off, set once per database volume:

```bash
docker exec bloom-lt-postgres-1 psql -U bloom -d postgres -c "ALTER SYSTEM SET synchronous_commit = off" -c "SELECT pg_reload_conf()"
# undo: ALTER SYSTEM RESET synchronous_commit;  (it also goes away with `down -v`)
```

This measures Bloom and the stack instead of the laptop's disk; losing the last commits on a crash does not matter in a throwaway test. Whether this becomes the
stack's default is the owner's decision.

## Seed (once; idempotent)

```bash
$LT run --rm --no-deps k6 run /scripts/seed.js        # CUSTOMERS=2000, POOL=100 products: about 2 minutes
```

Creates the category and `lt-pool` products with stock, and signs in `CUSTOMERS` customers through the Google fake. VU ids are global
across a test's scenarios, so `CUSTOMERS` must be at least the VU count of any run (the defaults need under 1,000). The admin calls
share a 120/min bucket and the admin token lasts 15 minutes: a seed of 100 products fits, `POOL` of about 200 or more would 401.

`seed-staff.sql` uses `ON CONFLICT DO NOTHING`: changing the admin password, TOTP secret or `LT_TOTP_KEY` needs `down -v`, a new start and a re-seed.

## Run

Warm the stack once after every (re)start: the first test after a restart is slower (cold JIT, cold pools) and can fail on
`checkout_launch` p95 by itself. A short smoke does it:

```bash
$LT run --rm --no-deps k6 run -e CATALOG_RATE=10 -e MIXED_RATE=2 -e WS_CONNECTIONS=20 -e LAUNCH_USERS=60 -e ALLOCATION=40 \
  -e RAMP=20s -e HOLD=60s -e LAUNCH_DELAY=40 /scripts/load.js
```

Then the tests, each followed by the invariant check (`verify` waits 30 s for in-flight work, then checks BI-01, BI-03 and idempotency in SQL):

```bash
$LT run --rm --no-deps k6 run /scripts/load.js   && $LT run --rm --no-deps verify     # about 14 minutes
$LT run --rm --no-deps k6 run /scripts/spike.js  && $LT run --rm --no-deps verify     # about 13 minutes
$LT run --rm --no-deps k6 run /scripts/stress.js ;  $LT run --rm --no-deps verify     # up to 25 minutes; `;`, see below
```

Every rate, user count and duration is an env var with a default (`-e NAME=value`; see the top of each script). `stress.js` exits 99
by design when a step breaks (`abortOnFail`), so chain `verify` with `;`, not `&&`. Results land in `deploy/loadtest/results/` (git-ignored).
Also look at `docker stats --no-stream`: if k6 sits at its 2-CPU cap the result measures k6, not Bloom.

### Reading a result

- **Pass/fail is the k6 exit code** plus `verify`. Each metric line ends with `PASS[...]` or `FAIL[...]` for its threshold.
- Load gates: `http_req_duration{flow:catalog}` p95 < 150 ms, `{flow:checkout_launch}` p95 < 300 ms, `{flow:wr_status}` p95 < 50 ms,
  `http_req_failed` < 1 %, WebSocket connect success > 99 %, `checkout_idempotent` = 1, no dropped iterations, no 401 or 429.
  Do not edit these numbers to make a run pass.
- `checkout_idempotent` is the binding idempotency proof: k6 replays every launch checkout with the same `Idempotency-Key` (replay tagged
  `flow:checkout_replay`) and requires the same order. The idempotency checks in `verify` are backed by database unique indexes.
- Spike gates: during the spike error rate < 5 % and 5xx < 1 % (`phase:spike`); after it `catalog` and `wr_status` p95 and the error
  rate again (`phase:recovery`; no `checkout_launch` there: no pass tokens exist after sell-out). Its teardown requires
  `held + sold == allocation`, so `SPIKE_USERS` must stay above `ALLOCATION`.
- **Stress** prints `KNEE step=<k> BROKE step=<b> FIRST=<flow|errors>` first, then one line per step with its error rate and the p95 of
  each flow against its target. Step *i* offers *i* x the `*_STEP` amount of each flow (`CATALOG_STEP` it/s, `MIXED_STEP` it/s,
  `WS_STEP` sessions, `STATUS_STEP` waiting-room users). The knee is the last step where every p95 is under its target; a step breaks
  at p95 > 2 x target or errors > 5 %. `none` for the knee means no step met all targets. Raise `STEPS` or the `*_STEP` values if no step broke.

### Known noise

- `fulfillment-worker` logs "Label-check sweep failed ... connection refused" at start: Shippo's base URL is hard-coded, so
  `api.goshippo.com` is blackholed to 127.0.0.1 in the two fulfillment containers (`extra_hosts`). Expected.
- Right after a restart an admitted launch user can get `400 SKU_NOT_BUYABLE` ("launch not open for pre-orders"): the waiting room admits by clock
  and the stage is flipped by the launch scheduler a moment later. Warming the stack first avoids it; if it still shows, rerun (a spike run then fails on
  `held + sold == allocation` by one unit). This is a Bloom timing race, not a stack bug; it is recorded for the owner.
- One admitted user in a few runs gets `SOLD_OUT`: admission can issue more passes than units, so a pass is occasionally lost to a sold-out launch. Normal.

## Calibration (this machine, measured 2026-11-17)

**Machine and budget.** Windows 11 Home, Docker Desktop VM 8 CPUs / 11.7 GB. All non-k6 containers together (one-shots and `verify` included):
**5.95 CPUs / 8.94 GB**; k6 2 CPUs / 1.5 GB. `DOTNET_gcServer=0` everywhere; every container sets `cpus` and `mem_limit`. Gateway rate limits are production values.
Split (CPUs): postgres 0.6, rabbitmq 0.65, gateway-api 0.75, catalog-api 0.5, order-api 0.4, launch-api 0.35, identity-api 0.3, inventory-api 0.25, payment-api 0.2,
fakes 0.1, notification-api 0.08, fulfillment-api 0.05; workers 0.05 to 0.15 (launch 0.15, notification 0.09, inventory/order 0.08, payment 0.07,
catalog/gateway/identity 0.06, fulfillment 0.05); otel-collector 0.15, prometheus 0.1, tempo/loki/grafana/minio 0.05, redis x5 0.05, mailpit and one-shots 0.03 to 0.15.
The split came from `docker stats` and the cgroup throttle counters (`/sys/fs/cgroup/cpu.stat`) during stress runs: containers pinned at their cap while the
system had headroom (rabbitmq, gateway, catalog, launch-api) got CPU from the idle ones (postgres averages 0.1 with bursts to 0.6, order-api peaks at 0.3,
payment and inventory at 0.1 to 0.2). RabbitMQ is the heaviest single container (average 0.2, peaks at its 0.65 cap).

**The knee** (`stress.js` with its defaults: 2-minute steps, `CATALOG_STEP=5`, `MIXED_STEP=1`, `WS_STEP=25`, `STATUS_STEP=25`): step 5 met every target, step 6 broke.
Step 5 is catalog 25 it/s (5 requests each, about 125 req/s), mixed 5 it/s, 125 WebSocket sessions, 125 waiting-room users, all at once:
catalog p95 4.9 ms, wr_status 6.2 ms, checkout_launch 98 ms, errors 0.14 %. Step 6 broke on `checkout_launch` (p95 1.3 s on a handful of samples; catalog and wr_status were still
at 5.8 and 7.1 ms); k6 used about 12 % of its 2 CPUs, so the break is Bloom's (and the VM disk's, see above), not the generator's. Earlier stress runs on worse splits or with the
full telemetry volume broke at steps 2 to 5 of the larger step size (catalog p95 638 ms at 40 it/s when gateway and catalog were pinned; wr_status 347 ms at 150 users when launch-api
was throttled; several multi-second `checkout_launch` stalls coinciding with a dead OpenTelemetry collector or a Postgres checkpoint).

**Defaults** (about 55 % of the knee per flow; spike baseline = load; spike peak slightly above the knee):

| | load.js | spike.js | knee |
|---|---|---|---|
| catalog | `CATALOG_RATE=14` it/s | `BASE_CATALOG_RATE=14` | 25 |
| mixed | `MIXED_RATE=3` it/s | `BASE_MIXED_RATE=3` | 5 |
| WebSocket | `WS_CONNECTIONS=70` | `SPIKE_WS=140` | 125 |
| waiting room | `LAUNCH_USERS=70`, `ALLOCATION=50`, `ADMISSION_RATE=3`/s | `SPIKE_USERS=140`, `ALLOCATION=100`, same rate | 125 |

Load: ramp 1 m, hold 8 m, launch 120 s after the start. Spike: launch 180 s after the start, 180 s of spike, 180 s of recovery. Stress: 10 steps of 2 min.
`CUSTOMERS=2000` (a run needs under 1,000 VUs). Numbers this small are what a 6-CPU budget of fractional-CPU containers gives: on a bigger machine raise the env vars and
re-run `stress.js` to find the new knee (rule: spec 5.9).

**Recorded results** (final settings, `synchronous_commit=off`, Tempo stopped, collector and Loki up):

| run | result |
|---|---|
| `load.js` #1 | exit 0, verify ok: catalog p95 5.3 ms, checkout_launch 95 ms, wr_status 5.4 ms, 0 failed of 49,220 requests, 0 dropped, 700 WS ok |
| `load.js` #2 (right after) | exit 0, verify ok: 5.3 / 86.5 / 4.6 ms, 0 failed of 49,250, 0 dropped, 700 WS ok |
| `spike.js` | exit 0, verify ok: 100 sold of 100, spike-phase error rate 0 and 5xx 0; recovery catalog p95 5.0 ms, wr_status 6.3 ms (361 reads), error rate 0, 0 dropped; 839 WS ok, 166 webhooks delivered |

With `synchronous_commit=on` (one run, same defaults): checkout_launch p95 216 ms (pass) but a checkpoint with a 21 s fsync phase froze the stack for about 30 s: 809 dropped
iterations, 1.0 % failed requests (504s), catalog max 22.5 s: `load.js` FAIL. Settings that mattered besides the split: the telemetry volume (10 % of traces, OTLP logs at Warning:
with every SQL command exported, Tempo, Loki and the collector were OOM-killed or pinned and runs broke), and Tempo off during runs.

## Look at it

- Grafana http://localhost:3000 (admin / admin), Explore, the Prometheus datasource. k6 writes to Prometheus by remote write
  (`K6_OUT=experimental-prometheus-rw`): metrics `k6_http_req_duration_p95`, `_p99`, `_avg`, `k6_http_req_failed_rate`, ... with the `flow` tag.
  The services' own OTel metrics are in the same datasource. Prometheus is at http://localhost:9090, RabbitMQ at http://localhost:15672 (bloom / bloom),
  Mailpit at http://localhost:8025.
- **Tempo and Loki.** Both are in the stack but are the weakest containers inside the budget (Tempo at 0.05 CPU is OOM-killed on a full
  trace volume and leaves the collector backlogged). To keep the runs honest the services export 10 % of traces
  (`OTEL_TRACES_SAMPLER=parentbased_traceidratio`, arg 0.1) and only warnings and above as OTLP logs (`Logging__OpenTelemetry__LogLevel__Default=Warning`);
  metrics are complete and console logs are unchanged (`docker logs`). Tempo was not running during the recorded load and spike runs (it OOM-restarts
  on its old WAL): `$LT stop tempo` before a run, `$LT start tempo` to look at traces. Loki stayed up.

## Teardown (everything: containers, volumes, images)

```bash
$LT --profile loadtest down -v --remove-orphans
docker image rm bloom-lt-fakes bloom-lt-migrate bloom-lt-gateway bloom-lt-identity bloom-lt-catalog bloom-lt-inventory \
  bloom-lt-launch bloom-lt-order bloom-lt-payment bloom-lt-fulfillment bloom-lt-notification
docker image rm grafana/k6:1.3.0 curlimages/curl:8.11.0     # only if they were pulled for this work
docker ps -a --filter name=bloom-lt && docker volume ls --filter name=bloom-lt                  # both must list nothing
```

The base images the dev compose also uses (postgres, redis, rabbitmq, grafana, prometheus, ...) are left alone.

## How `seed-staff.sql` was generated

A throwaway console program (not committed) referencing `Bloom.Identity` produced the one `INSERT` the way `Seed.StaffAsync` does:
the password hash is `PasswordHasher.Hash(PasswordPolicy.Validate(LT_ADMIN_PASSWORD))` (Argon2id), and `totp_secret_enc` is
`SecretBox(LT_TOTP_KEY).Seal(base32-decode(LT_ADMIN_TOTP_BASE32))` (AES-GCM, `src/Identity/Bloom.Identity/Crypto/SecretBox.cs`). Regenerate the file when
`LT_ADMIN_PASSWORD`, `LT_ADMIN_TOTP_BASE32` or `LT_TOTP_KEY` change (the k6 scripts compute the TOTP code from `LT_ADMIN_TOTP_BASE32`), then `down -v` and re-seed.
