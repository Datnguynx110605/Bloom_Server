# LLD — api-gateway

[Docs](../README.md) › [Low-level design](README.md)

| | |
|---|---|
| **Status** | Approved (2026-11-12) |
| **Last updated** | 2026-10-31 |
| **Product rules** | ACC-R10, [10-accounts-access §3](../product-design/10-accounts-access.md) (permission matrix) |
| **HLD** | [high-level-design.md §4, §9.1](../high-level-design/high-level-design.md), HLD-ADR-003, HLD-ADR-010, HLD-ADR-011 |
| **Depends on** | [06-identity.md](06-identity.md) (tokens, JWKS, revocation events) |

## 1. Problem and scope

The gateway is the only entry point for clients and providers. It verifies who the caller is, blocks revoked tokens and abusive traffic, and forwards requests with a trusted identity context, so services never handle raw tokens.

**In scope:** routing (YARP), JWT verification, revocation check, trusted identity context, coarse role rules per route, rate limiting (fixed window, sliding window, token bucket), CORS, request limits and timeouts, gateway error format, the revocation consumer.

**Out of scope:** TLS (terminated by the ingress controller), resource-level authorization (services), business logic, retries of non-idempotent requests.

## 2. Requirements and constraints

| ID | Requirement | Source |
|---|---|---|
| GW-01 | Every non-public route requires a valid ES256 access token. | HLD-ADR-010 |
| GW-02 | Client-supplied `x-bloom-*` headers are always removed before forwarding. | HLD-ADR-011 |
| GW-03 | Revoked tokens are rejected within ~1 s of revocation; staff requests fail closed if the revocation store is unreachable. | ACC-R10, owner decision |
| GW-04 | Gateway overhead p95 < 10 ms per request. | QA-02 … QA-04 budgets |
| GW-05 | Sustains 20k waiting-room status req/s with horizontal scaling. | QA-05 |
| GW-06 | Client IP is taken only from the ingress hop, never from client-set headers. | Rate-limit integrity |

## 3. Component structure

| Component | Deployment | Responsibility |
|---|---|---|
| Edge pipeline (YARP + ASP.NET Core middleware) | gateway | Request ID, header stripping, CORS, auth, revocation + rate limit, role check, proxy |
| JWKS cache | gateway | Holds identity's public keys |
| Revocation consumer | gateway-worker | `identity.tokens-revoked.v1` → gateway Redis |
| Gateway Redis (replica + failover) | — | Revocation entries, rate-limit state |

**Pipeline order:** (1) request ID + trace → (2) strip `x-bloom-*` → (3) CORS → (4) route match → (5) JWT validation (non-public routes) → (6) one Redis Lua call: revocation check + global limit + route limit → (7) role check → (8) add trusted context → (9) proxy with route timeout.

## 4. Data model

### 4.1 Redis

| Key | Value | TTL |
|---|---|---|
| `rev:{accountId}` | `issuedBefore` (Unix ms) | 16 min (access token lifetime + 1 min skew) |
| `fw:{policy}:{subject}:{windowStart}` | counter (fixed window) | window length + 5 s |
| `sw:{policy}:{subject}` | sorted set of request timestamps (sliding log) | window length |
| `tb:{policy}:{subject}` | hash `{tokens, ts}` (token bucket) | `2 × burst / rate` |

`subject` is the user ID for per-user policies and the client IP for per-IP policies.

### 4.2 Rate-limit policies

Each algorithm is used where its property matters:

| Algorithm | Property | Used for |
|---|---|---|
| **Fixed window** | Cheapest (`INCR` + expiry); may let up to 2× through across a window boundary | Coarse outer guard, webhooks |
| **Sliding window (exact log)** | Strict count over the last N seconds, no boundary burst | Authentication (brute-force protection) |
| **Token bucket** | Allows short bursts, then a steady rate | Signed-in browsing, cart and orders, checkout, waiting room |

| Policy | Algorithm | Applies to | Key | Limit |
|---|---|---|---|---|
| `global` | Fixed window | **Every client route** (not webhooks), before the route policy | IP | 300 / 1 min |
| `auth` | Sliding window | Sign-in, sign-up start, code verify, reset, staff MFA | IP | 10 / 1 min |
| `refresh` | Sliding window | `/auth/refresh` | IP | 20 / 1 min |
| `user` | Token bucket | Signed-in routes by default (catalog, cart, orders, notifications, staff admin) | user | burst 60, 120/min |
| `checkout` | Token bucket | `POST /api/checkout` | user | burst 3, 6/min |
| `wr-join` | Token bucket | Waiting-room join | user | burst 3, 5/min |
| `wr-status` | Token bucket | Waiting-room status | user | burst 5, 30/min |
| `webhook` | Fixed window | `/webhooks/*` (exempt from `global`) | IP | 600 / 1 min |

Anonymous public routes are limited by `global` only. A request is allowed only if both `global` and its route policy allow it. Numbers are starting values, tuned by load test (configuration, not code).

### 4.3 Trusted identity context (headers forwarded to services)

| Header | Value |
|---|---|
| `x-bloom-user-id` | `sub` |
| `x-bloom-user-type` | `customer` \| `staff` |
| `x-bloom-role` | staff role (absent for customers) |
| `x-bloom-auth-time` | `auth_time` (Unix s) |
| `x-bloom-request-id` | Request ID (also correlation ID) |

ASP.NET Core gRPC JSON transcoding exposes these as gRPC metadata to service code.

## 5. Interfaces

### 5.1 Routes

Paths are the contract between gateway and services (services declare them in `.proto` HTTP annotations).

| Prefix | Upstream | Auth | Coarse roles | Policy |
|---|---|---|---|---|
| `GET /api/catalog/**` | catalog | Public | — | `global` (+ `user` when signed in) |
| `/api/admin/catalog/**` | catalog | Staff | Catalog manager, Admin; reviews moderation: Support, Admin | `user` |
| `/api/reviews/**` | catalog | Customer | — | `user` |
| `/api/auth/**` | identity | Public | — | `auth` |
| `POST /auth/refresh` | identity | Cookie (Origin checked) | — | `refresh` |
| `/api/me/**` | identity | Customer or staff | — | `user` |
| `/api/admin/staff/**`, `/api/admin/audit/**` | identity | Staff | Admin | `user` |
| `/api/cart/**`, `/api/orders/**` | order | Customer | — | `user` |
| `POST /api/checkout` | order | Customer | — | `checkout` |
| `/api/admin/orders/**` | order | Staff | Support, Admin | `user` |
| `/api/admin/coupons/**` | order | Staff | Catalog manager, Admin | `user` |
| `/api/admin/settings/**` | order | Staff | Admin | `user` |
| `GET /api/launches/{id}` | launch | Public | — | `global` (+ `user` when signed in) |
| `POST /api/launches/{id}/waiting-room` | launch | Customer | — | `wr-join` |
| `GET /api/launches/{id}/waiting-room` | launch | Customer | — | `wr-status` |
| `/api/admin/launches/**` | launch | Staff | Catalog manager, Admin | `user` |
| `/api/admin/inventory/**` | inventory | Staff | Warehouse, Catalog manager, Support, Admin (methods narrow further) | `user` |
| `/api/admin/payments/**` | payment | Staff | Support, Admin | `user` |
| `/api/returns/**`, `/api/warranty/**` | fulfillment | Customer | — | `user` |
| `/api/admin/fulfillment/**` | fulfillment | Staff | Warehouse, Support, Admin | `user` |
| `/api/notifications/**` | notification | Customer | — | `user` |
| `/api/notifications/hub` (WebSocket) | notification | One-time ticket validated by notification (no JWT on upgrade); `Origin` must be allowed | — | `global` |
| `/api/admin/dlq/{service}/**` (`{service}` = catalog, inventory, launch, order, payment, fulfillment, identity, notification) | that service | Staff | Admin | `user` |
| `POST /webhooks/stripe` | payment | None (signature in service) | — | `webhook` |
| `POST /webhooks/shippo/{secret}` | fulfillment | None (secret in service) | — | `webhook` |
| anything else | — | — | — | `404` |

Coarse role checks only block the wrong *kind* of caller; each service still checks per method and per resource.

### 5.2 Upstream timeouts

| Route | Timeout |
|---|---|
| `POST /api/checkout` | 15 s (calls catalog, launch / inventory, payment) |
| Webhooks | 10 s |
| Everything else | 5 s |

No gateway retries (requests may not be idempotent; clients retry with idempotency keys where required).

### 5.3 Gateway error format

Same JSON shape as transcoded gRPC errors (`google.rpc.Status` with `ErrorInfo`, domain `bloom`), so clients parse one format.

| HTTP | `code` | `reason` | When |
|---|---|---|---|
| 401 | `UNAUTHENTICATED` | `TOKEN_MISSING` / `TOKEN_INVALID` | No / bad / expired token. An `Authorization` header that isn't `Bearer` → `TOKEN_INVALID` |
| 401 | `UNAUTHENTICATED` | `TOKEN_REVOKED` | `iat < rev:{sub}` |
| 403 | `PERMISSION_DENIED` | `ROLE_NOT_ALLOWED` | Wrong user type or role for the route |
| 403 | `PERMISSION_DENIED` | `ORIGIN_NOT_ALLOWED` | `/auth/refresh` from a foreign Origin |
| 404 | `NOT_FOUND` | `ROUTE_NOT_FOUND` | No route |
| 413 | `INVALID_ARGUMENT` | `PAYLOAD_TOO_LARGE` | Body above limit |
| 429 | `RESOURCE_EXHAUSTED` | `RATE_LIMITED` | Empty bucket; `Retry-After` header |
| 503 | `UNAVAILABLE` | `AUTH_STORE_UNAVAILABLE` | Staff request while gateway Redis is unreachable |
| 503 | `UNAVAILABLE` | `UPSTREAM_UNAVAILABLE` | Upstream unreachable |
| 504 | `DEADLINE_EXCEEDED` | `UPSTREAM_TIMEOUT` | Route timeout |

### 5.4 Messages

| Message | Direction | Effect |
|---|---|---|
| `identity.tokens-revoked.v1` | Consumed (gateway-worker) | §6.3 |

## 6. Behaviour

### 6.1 JWT validation

- Algorithm **ES256 only** (reject `none`, HS*, RS*); `iss = bloom-identity`; `aud = bloom`; `exp` and `iat` with 30 s clock skew; `kid` must be in the JWKS cache.
- **JWKS cache:** loaded at startup (readiness waits for it), refreshed every 10 min; an unknown `kid` triggers one refetch, at most once per 30 s.
- An `Authorization` header that isn't a `Bearer` token gets `TOKEN_INVALID`, on every route that reads a token.
- Refresh tokens never reach the gateway's validator; `/auth/refresh` is proxied with its cookie.

### 6.2 Revocation and rate limit (one Redis round trip)

A single Lua script per request, given `sub` (if authenticated), `iat`, client IP, the route policy and its parameters, and `now`:

1. **Revocation:** if `sub` present, `rev = GET rev:{sub}`; revoked if `iat × 1000 < rev` (`iat` in s, `rev` in ms) → return. Revocation stays fail-closed: a token issued in the same second as a revoke-all is treated as revoked, and the client recovers by refreshing.
2. **Global** (skipped for webhooks), fixed window: `INCR fw:global:{ip}:{floor(now/60s)}` (set expiry on first increment); over 300 → denied, `retry_after` = seconds to the next window.
3. **Route policy**, by algorithm:
   - *Fixed window:* as step 2 with the policy's key and limit.
   - *Sliding window (log):* `ZREMRANGEBYSCORE sw:{p}:{s} -inf <now − window>` (the upper bound has no `(` prefix, so the trim is inclusive: an entry exactly one window old is expired, so the window is `(now − W, now]`, which keeps `retry_after` above 0); if `ZCARD < limit` → `ZADD now` (member `now:random`), allowed; else denied, `retry_after` = oldest entry + window − now.
   - *Token bucket:* `tokens = min(burst, tokens + (now − ts) × rate)`; if `tokens ≥ 1` → `tokens −= 1`, allowed; else denied, `retry_after = (1 − tokens) / rate`.
4. A denied route policy doesn't refund the global count (simple, conservative).
5. Return `{revoked, allowed, retry_after}`.

**Redis unreachable (split by risk):**

| Caller | Revocation | Rate limit |
|---|---|---|
| Staff token | **Fail closed** → 503 `AUTH_STORE_UNAVAILABLE` | — |
| Customer token / anonymous | Fail open (signature and expiry still checked) | Per-pod in-memory limiters with the same algorithm and numbers per policy (.NET `FixedWindowRateLimiter`, `SlidingWindowRateLimiter`, `TokenBucketRateLimiter`) |

### 6.3 Revocation consumer

On `tokens-revoked {accountId, issuedBefore}`: Lua `SET rev:{accountId} max(existing, issuedBefore)` with TTL 16 min. The operation is idempotent and order-independent, so the gateway (which has no database) uses this instead of an inbox table. Any token issued before `issuedBefore` expires within 15 min, after which the entry is no longer needed. On a Redis error the worker holds the delivery and retries every 1 s (no NACK/requeue).

### 6.4 Client IP and CORS

- `ForwardedHeaders` trusts `X-Forwarded-For` only from the ingress controller's addresses (`KnownNetworks`); the client IP is the first untrusted hop (GW-06). `X-Forwarded-*` from an untrusted hop are replaced with values from the actual connection; from a trusted proxy (`Gateway:TrustedProxies`), the existing processing applies.
- CORS: allowed origins from configuration (the frontend origin); `AllowCredentials` (refresh cookie); preflight cached 10 min.
- `/auth/refresh` additionally requires `Origin` to be an allowed origin (CSRF defence on top of `SameSite=Strict`).
- Constraint for the frontend: the site and the API must be on the same registrable domain (for example `bloom.example` and `api.bloom.example`) for the `SameSite=Strict` cookie to be sent.

### 6.5 Request limits

| Limit | Value |
|---|---|
| Body (default) | 1 MB |
| Body (`/webhooks/stripe` / `/webhooks/shippo`) | 256 KB / 64 KB |
| Headers total | 32 KB |

### 6.6 Health

`/healthz` (process alive); `/readyz` (JWKS loaded and routes configured). Redis reachability isn't a readiness condition (§6.2 defines degraded behaviour).

## 7. Concurrency and consistency

| Risk | Protection |
|---|---|
| Spoofed identity | Strip `x-bloom-*` (GW-02); only the gateway can reach services (Linkerd allow rules) |
| Spoofed client IP | Trusted proxies only (GW-06) |
| Limiter races across pods | One atomic Lua script per request covers revocation, global and route policy |
| Revocation reordering / duplicates | `max()` semantics |
| Gateway Redis failover loses recent revocations | Accepted; window ≤ 1 s of writes; identity's `tokens_valid_after` remains the source of truth for refreshes |

## 8. Edge cases

| Case | Expected behaviour |
|---|---|
| Token signed with a newly rotated key | Unknown `kid` → JWKS refetch → validated. |
| Token with `alg: none` | `TOKEN_INVALID`. |
| Client sends `x-bloom-role: admin` | Header removed; role comes from the token only. |
| Customer token on `/api/admin/**` | `ROLE_NOT_ALLOWED`. |
| Staff request during Redis outage | 503; customer requests continue. |
| Burst of 30 page-load requests (signed in) | All allowed (global 300/min, user burst 60). |
| Waiting-room status polled every 5 s | Never limited (12/min vs 30/min). |
| Office IP shared by many customers | Global 300/min per IP may be hit first; route policies are per user. |
| Stripe sends 400 webhooks in a minute | Exempt from `global`; `webhook` 600/min allows them. |
| 11 sign-in attempts spread evenly over a window boundary | 11th rejected (sliding log has no boundary burst). |

## 9. Security and validation

- TLS ends at the ingress; ingress → gateway → services runs inside Linkerd mTLS.
- Response headers: `Strict-Transport-Security`, `X-Content-Type-Options: nosniff`, `Referrer-Policy: no-referrer`; server version headers removed.
- Logs never contain `Authorization` or `Cookie` values.
- The Shippo webhook secret is redacted from gateway spans (`url.path`, `http.target`, `url.full`) as `/webhooks/shippo/***`.
- The gateway holds no secrets except its Redis credentials (JWKS is public).

## 10. Observability

| Metric | Labels | Purpose |
|---|---|---|
| `gateway_requests_total` | `route`, `status` | Traffic |
| `gateway_overhead_seconds` | `route` | GW-04 |
| `gateway_upstream_seconds` | `route` | Service latency as seen at the edge |
| `gateway_auth_failures_total` | `reason` | Token problems |
| `gateway_rate_limited_total` | `policy` | Limit hits |
| `gateway_redis_degraded` | — | 1 while in fallback mode |
| `jwks_refresh_total` | `result` | Key rotation health |

**Alerts:** `gateway_redis_degraded = 1` for > 1 min; overhead p95 > 10 ms; spike in `TOKEN_REVOKED` or `TOKEN_INVALID`; `jwks_refresh_total{result="error"}`.

## 11. Test cases

| # | Case | Expected |
|---|---|---|
| G-01 | Request with forged `x-bloom-user-id` | Removed; upstream sees the token's `sub` |
| G-02 | `alg: HS256` token | 401 `TOKEN_INVALID` |
| G-03 | Token issued before a revocation | 401 `TOKEN_REVOKED` |
| G-04 | Duplicate / reordered revocation events | Latest `issuedBefore` kept |
| G-05 | Redis down, staff request | 503 |
| G-06 | Redis down, customer request | Served; in-memory limiter applies |
| G-07 | 11 sign-in attempts within 60 s from one IP | 11th → 429 with `Retry-After` |
| G-07b | 301 requests in one minute window from one IP | 301st → 429 (global) |
| G-07c | 4 checkouts in a second by one user | 4th → 429 (`checkout` burst 3) |
| G-08 | Spoofed `X-Forwarded-For` from the client | Ignored; ingress-provided IP used |
| G-09 | `/auth/refresh` with foreign Origin | 403 `ORIGIN_NOT_ALLOWED` |
| G-10 | Unknown `kid` after rotation | One JWKS refetch, then valid |
| G-11 | Load: 20k status req/s | Overhead p95 < 10 ms with added replicas |

## 12. Open questions

| ID | Question |
|---|---|
| — | None open. |
