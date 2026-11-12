# API gateway: design

| | |
|---|---|
| **Status** | Approved (2026-11-01) |
| **Date** | 2026-11-01 |
| **Branch / PR base** | `feat/api-gateway` → `feat/shared-library` |
| **Implements** | [07-api-gateway](../../../doc/low-level-design/07-api-gateway.md) (all sections), HLD §4, §9.1, [HLD-ADR-003](../../../doc/high-level-design/adr/README.md), HLD-ADR-010, HLD-ADR-011, HLD-ADR-014 (API/worker split) |
| **Builds on** | `Bloom.Shared` ([shared-library spec](2026-10-31-shared-library-design.md)) |

## 1. Intent

The gateway is the only entry point for clients and providers. It verifies the caller, blocks revoked tokens and abusive traffic, and forwards requests with a trusted identity context, so services never handle raw tokens (LLD 07 §1).

LLD 07 is the approved, complete design. This spec does not restate it. It records only the owner decisions and builder calls the LLD leaves open. Where this spec is silent, LLD 07 decides.

## 2. Goals

1. `api-gateway`: the edge pipeline of LLD 07 §3 (steps 1–9) on YARP, with every route, rule, policy, timeout, limit, header, error and metric of §4–§6, §9, §10.
2. `gateway-worker`: the revocation consumer of LLD 07 §6.3.
3. Automated tests for G-01 … G-10 and every edge case in LLD 07 §8.
4. `Bloom.Identity.Contracts` with the `TokensRevokedV1` record only (§4).

**Success criteria:** `dotnet build` passes with warnings as errors. `dotnet test` passes locally (Colima running) and in the existing CI workflow. Each test case G-01 … G-10 has at least one test named after its ID.

## 3. Non-goals and anti-goals

**Non-goals:** G-11 load test (deferred to the cluster; recorded as deferred in the PR). Dockerfile, Compose service entries, Kubernetes manifests. Any change to `Bloom.Shared`. Any change under `doc/`. The identity service or any upstream service (only its contracts package, §4).

**Anti-goals** (failure even if it technically works):
- Any route, header, error reason, policy or metric that LLD 07 doesn't list (AGENTS.md §1).
- Forwarding any client-sent `x-bloom-*` header (GW-02), or trusting client `X-Forwarded-For` (GW-06).
- More than one Redis round trip per request for revocation + rate limits (§6.2).
- Gateway retries of upstream requests (§5.2).
- Logging `Authorization` or `Cookie` values (§9).

## 4. Owner decisions

| Topic | Decision |
|---|---|
| Scope | All of LLD 07, nothing more |
| Revocation consumer | Gateway-owned small consumer; no inbox, no retry/DLQ queues; `Bloom.Shared` not changed (§6) |
| Revocation consumer on Redis failure | Hold the delivery unacked and retry the write in-process every 1 s until it succeeds or the worker stops (quorum-queue delivery limit would drop a NACK/requeue loop) |
| `tokens-revoked` payload contract | `TokensRevokedV1(Guid AccountId, long IssuedBefore)` (Unix ms), JSON `{"accountId": uuid, "issuedBefore": number}`, in a new identity-owned contracts package `src/Identity/Bloom.Identity.Contracts` (00-reliability §5.2). It holds only this record for now. |
| Upstream unreachable | 503, code `UNAVAILABLE`, reason `UPSTREAM_UNAVAILABLE`, gateway error shape. Upstream responses (including 5xx) pass through untouched. |
| `iat` vs `issuedBefore` units | Revoked when `iat × 1000 < issuedBefore` (fail closed). `iat` is Unix seconds (06-identity §4.3), `issuedBefore` Unix ms (LLD 07 §4.1). |
| Token on a public route | A present token is always validated. Bad / expired / revoked → 401 as on protected routes. Absent → anonymous. |
| G-11 | Deferred to cluster load test. `gateway_overhead_seconds` is built. |
| Deliverables | Code + tests only |
| Branch | `feat/api-gateway` off `feat/shared-library`, PR into `feat/shared-library` |

## 5. Layout and dependencies (builder's call)

```
src/Gateway/
  Bloom.Gateway/         # one ASP.NET Core app; runs as api-gateway or gateway-worker by config
  Bloom.Gateway.Tests/   # xUnit v3 + Testcontainers (Redis, RabbitMQ)
src/Identity/
  Bloom.Identity.Contracts/  # owner decision: TokensRevokedV1 only; identity service adds to it later
```

- One app with a mode setting (`api` / `worker`) gives the two deployments of LLD 07 §3 from one build (HLD-ADR-014 API/worker split). The worker mode serves `/healthz` only.
- Both projects are added to `Bloom.slnx` under `/src/Gateway/`.
- New packages (central versions in `Directory.Packages.props`): `Yarp.ReverseProxy` (HLD-ADR-003), `StackExchange.Redis`, `Microsoft.IdentityModel.JsonWebTokens` (ES256 validation against JWKS), `Testcontainers.Redis` (tests).
- Reused from `Bloom.Shared`: `AddBloomTelemetry` (plus the gateway's own meter), `Envelope`, `MessageNames`, `BloomErrors.Domain` / `RoleNotAllowed`. `AddBloom<TDbContext>` is **not** used (it requires PostgreSQL).

## 6. Revocation consumer (gateway-worker)

- Uses `RabbitMQ.Client` directly (the shared `RabbitMqConnection` is `internal`).
- Topology, declared at startup, idempotent: queue `api-gateway.tokens-revoked` (quorum, 00-reliability §5.1, §6) bound to topic exchange `identity.events` with routing key `identity.tokens-revoked.v1`.
- Per delivery:
  - Envelope or payload can't be parsed (as `TokensRevokedV1`, web JSON defaults), or unknown message type → log at error level, ACK (nothing to retry).
  - Otherwise run the §6.3 Lua (`SET rev:{accountId} max(existing, issuedBefore)` PX 16 min), then ACK.
  - Redis error → keep the delivery unacked; retry the write every 1 s until it succeeds or the worker stops (owner decision). Prefetch 1. On stop, the unacked delivery is redelivered after restart.
- No inbox: the write is idempotent and order-independent (LLD 07 §6.3, §7).

## 7. Gateway pipeline notes

All behaviour is LLD 07 §3–§6. Points the LLD leaves to the implementation:

- **Routes** live in `appsettings.json` (YARP routes + clusters). Per-route metadata carries: auth mode (`public`, `public-optional-user`, `customer`, `staff`, `any-user`, `none`), allowed roles, rate-limit policy, timeout. Policy numbers are configuration (§4.2: "configuration, not code").
- **Upstream addresses** are cluster destinations in configuration; tests override them with fakes.
- **JWT:** `typ` claim → `x-bloom-user-type`; `role` claim → `x-bloom-role` (staff only). Staff role values: `admin`, `catalog_manager`, `warehouse`, `support` (06-identity §4.1). Customers have no role.
- **JWKS** URL is configuration (identity's internal `GET /.well-known/jwks.json`, 06-identity §5).
- **"Staff request" for fail-closed** (§6.2) = a request carrying a valid token with `typ = staff`.
- **Errors** are written in the `google.rpc.Status` JSON shape with `ErrorInfo` (domain `bloom`) of LLD 07 §5.3, matching what gRPC JSON transcoding returns.
- **In-memory fallback limiters** are per pod, keyed per policy + subject, created on demand (§6.2 table).
- **Which public routes read a token** *(builder's call)*: only the two LLD 07 §5.1 marks "+ `user` when signed in": `GET /api/catalog/**` and `GET /api/launches/{id}` (mode `public-optional-user`). On every other public or unauthenticated route (`/api/auth/**`, `/auth/refresh`, webhooks, the notification hub), `Authorization` is ignored, so a stale access token can't block sign-in. No identity context is added there.
- **`Authorization` is never forwarded** upstream *(builder's call)*: services never handle raw tokens (LLD 07 §1); the trusted context replaces it.
- **Staff token on a `public-optional-user` route during a Redis outage** → 503 `AUTH_STORE_UNAVAILABLE`, as LLD 07 §6.2 says for any staff token.
- **Notification hub** *(builder's call)*: exempt from the 5 s route timeout (a long-lived WebSocket; HLD R-10 gives it a 15-min connection life). A foreign `Origin` on the upgrade → 403 `ORIGIN_NOT_ALLOWED` (the listed reason).
- **Upstream unreachable** (connect failure, reset, no destination) → 503 `UNAVAILABLE` / `UPSTREAM_UNAVAILABLE` (owner decision). Route timeout → 504 `UPSTREAM_TIMEOUT` (§5.3).
- **Known path, unlisted method** (e.g. `POST /api/catalog/x`) → 404 `ROUTE_NOT_FOUND` *(builder's call: §5.1 lists routes per method; "anything else → 404")*.
- **Headers over 32 KB** → Kestrel's own 431 response *(builder's call: Kestrel rejects before the pipeline runs; LLD 07 lists no reason for it)*.
- **Gateway Redis** is a single logical primary with a replica and automatic failover (HLD §9.5 "replica + automatic failover"), not Redis Cluster, so the multi-key Lua script of §6.2 is valid as designed.

## 8. Testing

- xUnit v3 + Testcontainers with real Redis and RabbitMQ (one container each per run). The gateway runs in-process (`WebApplicationFactory` / TestServer).
- Fakes inside tests: a JWKS endpoint backed by test ES256 keys (supports rotation for G-10), and upstream HTTP servers that echo received headers (G-01, G-08).
- Rate-limit tests drive time through the Lua `now` argument (injected clock), not wall-clock sleeps.
- G-05 / G-06 simulate a Redis outage by pointing the gateway at an unreachable Redis or stopping the container.
- Tests need Colima running locally. All containers are removed after the run.

## 9. Proposed doc edits (not applied)

No edit under `doc/` is made in this project (AGENTS.md §9). For the owner to approve separately:

1. LLD 07 §6.2 step 1: "revoked if `iat × 1000 < rev` (`iat` in s, `rev` in ms)".
2. LLD 07 §5.3: new row `503 | UNAVAILABLE | UPSTREAM_UNAVAILABLE | Upstream unreachable`.
3. 06-identity §5 (Messages): `identity.tokens-revoked.v1` payload types `accountId` uuid, `issuedBefore` Unix ms (number).
4. LLD 07 §6.3: on a Redis error the worker holds the delivery and retries every 1 s (no NACK/requeue).

## 10. Left to the builder

Internal names, class layout, how route metadata is modelled, the Lua script's exact code, test isolation, package versions (latest stable compatible with .NET 10). Anything visible outside the gateway or affecting the design that LLD 07 doesn't cover goes back to the owner (AGENTS.md §7).

## 11. Open items (owner)

None open.
