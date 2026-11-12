# API Gateway Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build `api-gateway` (the LLD 07 edge pipeline on YARP) and `gateway-worker` (the `identity.tokens-revoked.v1` consumer) as one ASP.NET Core app, plus the `Bloom.Identity.Contracts` package holding `TokensRevokedV1`.

**Architecture:** One app `Bloom.Gateway`; `Gateway:Mode` = `api` or `worker` picks the deployment (HLD-ADR-014). API mode: ASP.NET Core middleware for steps 1–8 of LLD 07 §3, YARP for step 9. One Lua script per request in gateway Redis does revocation + global + route limit (§6.2); per-pod .NET limiters take over when Redis is unreachable. Worker mode: a small RabbitMQ consumer writing `rev:{accountId}` with a `max()` Lua (§6.3).

**Tech Stack:** .NET 10, ASP.NET Core, Yarp.ReverseProxy, StackExchange.Redis, Microsoft.IdentityModel.JsonWebTokens, RabbitMQ.Client 7, System.Threading.RateLimiting, Bloom.Shared (telemetry, `Envelope`, `BloomErrors`), xUnit v3, Testcontainers (Redis, RabbitMQ), Microsoft.Extensions.TimeProvider.Testing.

**Spec:** `docs/superpowers/specs/2026-11-01-api-gateway-design.md` (approved). Design source: `doc/low-level-design/07-api-gateway.md` (cited as `§x`). Where the spec is silent, LLD 07 decides.

## Global Constraints

- Build settings from `Directory.Build.props` (net10.0, Nullable, ImplicitUsings, `TreatWarningsAsErrors`). Central package versions in `Directory.Packages.props`; add packages with `dotnet add <proj> package <id>` (latest stable for .NET 10).
- New packages, exactly: `Yarp.ReverseProxy`, `StackExchange.Redis`, `Microsoft.IdentityModel.JsonWebTokens` (app); `Testcontainers.Redis`, `Microsoft.Extensions.TimeProvider.Testing` (tests). Nothing else.
- No route, header, error reason, policy, metric or config the LLD/spec doesn't list (AGENTS.md §1). Never touch `doc/` or `Bloom.Shared`.
- Never forward client `x-bloom-*` headers (GW-02) or `Authorization`; never trust `X-Forwarded-For` from an untrusted hop (GW-06). Never log `Authorization` or `Cookie` values (§9).
- One Redis round trip per request for revocation + limits (§6.2). No upstream retries (§5.2).
- Revoked when `iat × 1000 < rev` (`iat` Unix s, `rev` Unix ms; spec §4).
- Error bodies: `google.rpc.Status` JSON with one `ErrorInfo{domain="bloom", reason}` (§5.3, spec §7).
- Docker via Colima: never run `colima`; daemon unreachable → stop and report. Never `dotnet run`. Test containers must be gone after the run.
- Every test case G-01 … G-10 has at least one test whose method name starts with its ID (`G01_…`, `G07b_…`). G-11 is deferred (spec §3).
- Test names below are binding; listed assertions are the minimum.
- Test command: `dotnet test --project src/Gateway/Bloom.Gateway.Tests`, narrowed with `--filter-class '*<Class>'` where a step says `--filter-class`.

## Review Focus

1. **Stale access token on sign-in, refresh or a webhook**: `Authorization` is ignored there, so an expired token never blocks sign-in (spec §7). Test in Task 4.
2. **Gateway Redis comes back after an outage**: limits move back to Redis, staff requests are served again, `gateway_redis_degraded` returns to 0. Test in Task 6.
3. **Upstream fails**: exactly one upstream attempt; a 5xx passes through untouched; connect failure → 503 `UPSTREAM_UNAVAILABLE`. Tests in Task 3.
4. **Chunked request body over the limit** (no `Content-Length`): 413 `PAYLOAD_TOO_LARGE`, not 400/502. Test in Task 3.
5. **Worker loses the broker connection or Redis while holding a revocation**: it reconnects / keeps retrying and the event is applied once both are back. Tests in Task 8.

## Rulings in this plan (builder's calls the spec left open)

- **P1** `/api/admin/catalog/**` coarse roles = `catalog_manager, support, admin` (§5.1 gives Support for reviews moderation but no separate path; catalog narrows per method).
- **P2** "Prefix" rows (`/x/**`) match `/x` and everything below (YARP `{**rest}`). The hub route covers `/api/notifications/hub` and below (SignalR negotiate) and wins over `/api/notifications/**`.
- **P3** Global denied → route policy not evaluated (its state unchanged). Route denied → global not refunded (§6.2 step 4).
- **P4** `Retry-After` = whole seconds, `ceil(retry_after)`, minimum 1.
- **P5** `/auth/refresh` and the hub with no `Origin` header → 403 `ORIGIN_NOT_ALLOWED`.
- **P6** Token claims: `sub` must parse as a GUID, `typ` ∈ {`customer`, `staff`}, `iat` and `auth_time` present, `staff` needs a non-empty `role`; otherwise `TOKEN_INVALID`. `iat` more than 30 s in the future → `TOKEN_INVALID`. `sub` is used in lowercase `D` form for Redis keys and `x-bloom-user-id`. A customer's `role` claim is ignored.
- **P7** Request ID = new GUID (`D` form) per request; any client value is stripped (GW-02). It isn't returned to the client.
- **P8** Metric label `route` = YARP route ID (`none` when nothing matched); `status` = HTTP status as a string. `gateway_auth_failures_total` counts `TOKEN_MISSING`, `TOKEN_INVALID`, `TOKEN_REVOKED` only.
- **P9** YARP's X-Forwarded transforms are off (no header the LLD doesn't list). Other client headers pass through as YARP does by default.
- **P10** In-memory sliding window = `SlidingWindowRateLimiter` with 60 segments per window (.NET has no exact log).
- **P11** `Strict-Transport-Security: max-age=31536000`. Removed from every response: `Server`, `X-Powered-By`, `X-AspNet-Version`.
- **P12** Tests run the gateway in-process on real Kestrel at `127.0.0.1:0` (needed for header/body limits, client IP, WebSockets) instead of TestServer (spec §8 says "WebApplicationFactory / TestServer"; same in-process intent, no Mvc.Testing package).
- **P13** `appsettings.json` holds placeholder addresses (`http://<service>:8080`, `gateway-redis:6379`, `amqp://rabbitmq:5672`, `http://identity:8080/.well-known/jwks.json`, origin `https://bloom.example`, no trusted proxies); deployments override them.
- **P14** JWKS: until the first successful load, retry every 1 s; then refresh every 10 min; an unknown `kid` refetches at most once per 30 s across all kids, single-flight. All JWKS timing runs on the injected `TimeProvider`.
- **P15** Forwarder error mapping (when the response hasn't started): `RequestTimedOut` → 504 `UPSTREAM_TIMEOUT`; `RequestBodyClient` caused by a 413 `BadHttpRequestException` → 413 `PAYLOAD_TOO_LARGE`; `RequestCanceled` (client gone) → nothing; any other error → 503 `UNAVAILABLE`/`UPSTREAM_UNAVAILABLE`.
- **P16** Redis client: `AbortOnConnectFail = false`, `BacklogPolicy.FailFast` (an outage fails requests at once instead of queueing); other options come from the connection string.
- **P17** Worker: one channel, prefetch 1, manual ACK after the Redis write; on connection loss it reconnects after 1 s and redeclares its topology.
- **P18** Fixed-window key `fw:{policy}:{subject}:{n}` with `n = floor(now_ms / window_ms)`.

---

## File Structure

```
Bloom.slnx                                  # + /src/Gateway/ (2 projects), /src/Identity/ (1 project)
Directory.Packages.props                    # + 5 packages
src/Identity/Bloom.Identity.Contracts/
  Bloom.Identity.Contracts.csproj, TokensRevokedV1.cs
src/Gateway/Bloom.Gateway/
  Bloom.Gateway.csproj (Sdk.Web), Program.cs, GatewayApp.cs, appsettings.json
  Errors/GatewayErrors.cs
  Edge/EdgeRoute.cs, EdgeMiddleware.cs, RequestHygiene.cs, ProxyPipeline.cs
  Auth/JwksCache.cs, TokenValidator.cs, Caller.cs
  Limits/RateLimitPolicy.cs, EdgeStore.cs (+ embedded edge.lua), FallbackLimiters.cs
  Observability/GatewayMetrics.cs
  Worker/RevocationConsumer.cs, RevocationWriter.cs
src/Gateway/Bloom.Gateway.Tests/
  Infrastructure/ Containers.cs, EchoUpstream.cs, TestGateway.cs, FakeJwks.cs, TestTokens.cs,
                  MetricCapture.cs, Poll.cs, LogCapture.cs, TestWorker.cs
  ShellTests.cs, RoutingTests.cs, UpstreamTests.cs, AuthTests.cs, EdgeLimitTests.cs,
  RedisOutageTests.cs, CorsOriginTests.cs, HubTests.cs, RevocationWorkerTests.cs
```

Route table used by Tasks 2, 4 and 7 (from §5.1; `Auth`, `Roles`, `Policy`, `MaxBodyBytes`, `CheckOrigin` are YARP route `Metadata`; timeout per §5.2):

| Route ID | Method | Match path | Cluster | Auth | Roles | Policy | Timeout | Other |
|---|---|---|---|---|---|---|---|---|
| `catalog-public` | GET | `/api/catalog/{**rest}` | catalog | `public-optional-user` | | `user` | 5 s | |
| `admin-catalog` | any | `/api/admin/catalog/{**rest}` | catalog | `staff` | `catalog_manager,support,admin` | `user` | 5 s | |
| `auth` | any | `/api/auth/{**rest}` | identity | `public` | | `auth` | 5 s | |
| `auth-refresh` | POST | `/auth/refresh` | identity | `none` | | `refresh` | 5 s | `CheckOrigin=true` |
| `me` | any | `/api/me/{**rest}` | identity | `any-user` | | `user` | 5 s | |
| `admin-staff` | any | `/api/admin/staff/{**rest}` | identity | `staff` | `admin` | `user` | 5 s | |
| `admin-audit` | any | `/api/admin/audit/{**rest}` | identity | `staff` | `admin` | `user` | 5 s | |
| `cart` | any | `/api/cart/{**rest}` | order | `customer` | | `user` | 5 s | |
| `orders` | any | `/api/orders/{**rest}` | order | `customer` | | `user` | 5 s | |
| `checkout` | POST | `/api/checkout` | order | `customer` | | `checkout` | 15 s | |
| `admin-orders` | any | `/api/admin/orders/{**rest}` | order | `staff` | `support,admin` | `user` | 5 s | |
| `admin-coupons` | any | `/api/admin/coupons/{**rest}` | order | `staff` | `catalog_manager,admin` | `user` | 5 s | |
| `admin-settings` | any | `/api/admin/settings/{**rest}` | order | `staff` | `admin` | `user` | 5 s | |
| `launch-public` | GET | `/api/launches/{id}` | launch | `public-optional-user` | | `user` | 5 s | |
| `waiting-room-join` | POST | `/api/launches/{id}/waiting-room` | launch | `customer` | | `wr-join` | 5 s | |
| `waiting-room-status` | GET | `/api/launches/{id}/waiting-room` | launch | `customer` | | `wr-status` | 5 s | |
| `admin-launches` | any | `/api/admin/launches/{**rest}` | launch | `staff` | `catalog_manager,admin` | `user` | 5 s | |
| `admin-inventory` | any | `/api/admin/inventory/{**rest}` | inventory | `staff` | `warehouse,catalog_manager,support,admin` | `user` | 5 s | |
| `admin-payments` | any | `/api/admin/payments/{**rest}` | payment | `staff` | `support,admin` | `user` | 5 s | |
| `returns` | any | `/api/returns/{**rest}` | fulfillment | `customer` | | `user` | 5 s | |
| `warranty` | any | `/api/warranty/{**rest}` | fulfillment | `customer` | | `user` | 5 s | |
| `admin-fulfillment` | any | `/api/admin/fulfillment/{**rest}` | fulfillment | `staff` | `warehouse,support,admin` | `user` | 5 s | |
| `notifications` | any | `/api/notifications/{**rest}` | notification | `customer` | | `user` | 5 s | |
| `notifications-hub` | any | `/api/notifications/hub/{**rest}` | notification | `none` | | (none: `global` only) | disabled | `CheckOrigin=true` |
| `dlq-<svc>` ×8 | any | `/api/admin/dlq/<svc>/{**rest}` | `<svc>` | `staff` | `admin` | `user` | 5 s | svc ∈ catalog, inventory, launch, order, payment, fulfillment, identity, notification |
| `webhook-stripe` | POST | `/webhooks/stripe` | payment | `none` | | `webhook` | 10 s | `MaxBodyBytes=262144` |
| `webhook-shippo` | POST | `/webhooks/shippo/{secret}` | fulfillment | `none` | | `webhook` | 10 s | `MaxBodyBytes=65536` |

Clusters: `catalog, identity, order, launch, inventory, payment, fulfillment, notification`, each with one destination named `d1`.

---

### Task 1: Projects, app shell, error writer, test infrastructure

**Files:**
- Create: `src/Identity/Bloom.Identity.Contracts/Bloom.Identity.Contracts.csproj` (classlib, no dependencies), `TokensRevokedV1.cs`
- Create: `src/Gateway/Bloom.Gateway/Bloom.Gateway.csproj` (`Microsoft.NET.Sdk.Web`; refs Bloom.Shared, Bloom.Identity.Contracts; packages Yarp.ReverseProxy, StackExchange.Redis, Microsoft.IdentityModel.JsonWebTokens, RabbitMQ.Client; `InternalsVisibleTo Bloom.Gateway.Tests`), `Program.cs`, `GatewayApp.cs`, `appsettings.json`, `Errors/GatewayErrors.cs`
- Create: `src/Gateway/Bloom.Gateway.Tests/Bloom.Gateway.Tests.csproj` (`OutputType Exe`, `IsPackable false`, `<Using Include="Xunit" />`; xunit.v3, Testcontainers.Redis, Testcontainers.RabbitMq, Microsoft.Extensions.TimeProvider.Testing), `Infrastructure/Containers.cs`, `EchoUpstream.cs`, `TestGateway.cs`, `Poll.cs` (copy of the shared one)
- Modify: `Bloom.slnx`, `Directory.Packages.props`
- Test: `src/Gateway/Bloom.Gateway.Tests/ShellTests.cs`

**Interfaces:**
- Produces:
  - `namespace Bloom.Identity.Contracts; public sealed record TokensRevokedV1(Guid AccountId, long IssuedBefore);` (Unix ms)
  - `public static class GatewayApp { public static WebApplication Build(string[] args, Action<WebApplicationBuilder>? configure = null); }` — reads `Gateway:Mode` (`api` | `worker`, anything else → throw at build); calls `AddBloomTelemetry("api-gateway" | "gateway-worker")` plus the gateway meter; `configure` runs after default config so tests override. `Program.cs` = `GatewayApp.Build(args).Run();`.
  - `public sealed record GatewayError(int HttpStatus, Grpc.Core.StatusCode Code, string Reason, string Message);`
  - `public static class GatewayErrors` with fields `TokenMissing` (401, Unauthenticated, `TOKEN_MISSING`), `TokenInvalid` (401, `TOKEN_INVALID`), `TokenRevoked` (401, `TOKEN_REVOKED`), `RoleNotAllowed` (403, PermissionDenied, `BloomErrors.RoleNotAllowed`), `OriginNotAllowed` (403, `ORIGIN_NOT_ALLOWED`), `RouteNotFound` (404, NotFound, `ROUTE_NOT_FOUND`), `PayloadTooLarge` (413, InvalidArgument, `PAYLOAD_TOO_LARGE`), `RateLimited` (429, ResourceExhausted, `RATE_LIMITED`), `AuthStoreUnavailable` (503, Unavailable, `AUTH_STORE_UNAVAILABLE`), `UpstreamUnavailable` (503, Unavailable, `UPSTREAM_UNAVAILABLE`), `UpstreamTimeout` (504, DeadlineExceeded, `UPSTREAM_TIMEOUT`); and `Task WriteAsync(HttpContext context, GatewayError error, TimeSpan? retryAfter = null)` — `application/json` body built as `Google.Rpc.Status` + `Any.Pack(ErrorInfo{Reason, Domain = BloomErrors.Domain})`, serialized with `Google.Protobuf.JsonFormatter` and a `TypeRegistry` holding `ErrorInfo`; sets `Retry-After` (P4) when `retryAfter` is given.
  - API mode pipeline order (later tasks fill the slots): `UseForwardedHeaders` (from `Gateway:TrustedProxies` CIDR list; `KnownNetworks`/`KnownProxies` cleared first, `ForwardLimit = null`) → request hygiene → `UseCors` → `UseRouting` → `EdgeMiddleware` → `UseRequestTimeouts` → `MapReverseProxy`. In this task: any request that matches no endpoint other than `/healthz` → `GatewayErrors.RouteNotFound`. Both modes map `GET /healthz` → 200.
  - Tests: `[assembly: AssemblyFixture(typeof(Containers))]`; `Containers` starts `redis:8.2.3` and `rabbitmq:4.1.4-management` (the Compose tags) and exposes `string RedisConnectionString`, `IConnectionMultiplexer Redis` (test-side), `string RabbitMqUri`, `Task<string> RabbitCtlAsync(params string[] args)` (as in Bloom.Shared.Tests).
  - `EchoUpstream : IAsyncDisposable` — `static Task<EchoUpstream> StartAsync()`; Kestrel on `127.0.0.1:0`; `Uri Address`; `int RequestCount`; every HTTP request → 200 JSON `{"method","path","query","headers":{lowercase name: value},"bodyLength"}` (reads the whole body), adds response headers `X-Powered-By: echo` (Kestrel's `Server` stays on); request header `x-test-status: <code>` → that status with body `boom`; `x-test-delay-ms: <ms>` → waits first; WebSocket upgrade → echoes text frames.
  - `TestGateway : IAsyncDisposable` — `static Task<TestGateway> StartAsync(Containers containers, Action<TestGatewayOptions>? configure = null)`; `TestGatewayOptions { Dictionary<string, string?> Config; }`. It starts its own `EchoUpstream`, points every cluster's `d1` at `{echo}/{cluster}/` (so the echo `path` starts with the cluster name), sets `Gateway:TrustedProxies:0 = 127.0.0.1/32`, `Gateway:AllowedOrigins:0 = https://bloom.test`, `Gateway:Redis = containers.RedisConnectionString`, binds `http://127.0.0.1:0`. Exposes `Uri Address`, `HttpClient Client` (base address set; default header `X-Forwarded-For: {ClientIp}`), `string ClientIp` (unique per instance, from `198.18.0.0/15`), `EchoUpstream Upstream`, `IServiceProvider Services`.

- [ ] **Step 1: Create the three projects**, add them to `Bloom.slnx` under `/src/Gateway/` and `/src/Identity/`, add the five packages centrally. `appsettings.json` per P13 with `Gateway:Mode = api` and `Kestrel:AddServerHeader = false`.

- [ ] **Step 2: Write the failing tests** in `ShellTests.cs`

```csharp
[Fact] public void TokensRevokedV1_uses_web_json_names()
// JsonSerializer.Serialize(new TokensRevokedV1(id, 1730419200000), JsonSerializerOptions.Web)
// == $"{{\"accountId\":\"{id}\",\"issuedBefore\":1730419200000}}"; round-trips back to an equal record.
[Fact] public async Task Unknown_route_is_404_ROUTE_NOT_FOUND()
// GET /nope → 404, content type application/json; body has exactly the properties code, message, details;
// code == 5; details has one element with "@type" == "type.googleapis.com/google.rpc.ErrorInfo",
// "reason" == "ROUTE_NOT_FOUND", "domain" == "bloom".
[Fact] public async Task Api_mode_serves_healthz()                   // 200
[Fact] public async Task Worker_mode_serves_healthz_only()           // Mode=worker: /healthz 200; GET /api/catalog/x → 404
[Fact] public void Unknown_mode_fails_at_build()                     // Mode=edge → GatewayApp.Build throws
[Fact] public async Task Retry_after_is_whole_seconds_rounded_up()   // WriteAsync(RateLimited, 1.2 s) → "Retry-After: 2"; 0.1 s → "1"
```

- [ ] **Step 3: Run** `dotnet test --project src/Gateway/Bloom.Gateway.Tests` — Expected: build fails (types missing).

- [ ] **Step 4: Implement** the contracts record, `GatewayApp`, `GatewayErrors`, the test infrastructure.

- [ ] **Step 5: Run** the same command — Expected: PASS. Then `dotnet build -c Release` — 0 warnings.

- [ ] **Step 6: Commit** — `git add -A && git commit -m "feat(gateway): app shell, error format, identity contracts and test infrastructure"`

---

### Task 2: Routes, proxy, request hygiene, request metrics

**Files:**
- Create: `Edge/EdgeRoute.cs`, `Edge/EdgeMiddleware.cs`, `Edge/RequestHygiene.cs`, `Edge/ProxyPipeline.cs`, `Observability/GatewayMetrics.cs`
- Modify: `appsettings.json` (the route table above, 8 clusters), `GatewayApp.cs`
- Create: `Infrastructure/MetricCapture.cs` (tests)
- Test: `RoutingTests.cs`

**Interfaces:**
- Consumes: `GatewayErrors`, `TestGateway` (Task 1).
- Produces:
  - `public enum AuthMode { Public, PublicOptionalUser, Customer, Staff, AnyUser, None }` (config strings `public`, `public-optional-user`, `customer`, `staff`, `any-user`, `none`).
  - `public sealed record EdgeRoute(string RouteId, AuthMode Auth, IReadOnlySet<string> Roles, string? Policy, long? MaxBodyBytes, bool CheckOrigin)` with `static EdgeRoute From(RouteConfig route)` — throws on an unknown `Auth` value or a `Policy` missing from `Gateway:RateLimits` (Task 5 adds the policy check; until then any name). Parsed once per route config.
  - `RequestHygiene` middleware: removes every request header starting `x-bloom-` (ordinal, ignore case), sets `x-bloom-request-id` to a new GUID (P7), adds response headers per P11 and removes `Server`/`X-Powered-By`/`X-AspNet-Version` in `Response.OnStarting` (applies to proxied responses too).
  - `EdgeMiddleware`: endpoint without YARP `RouteModel` metadata, except `/healthz` and `/readyz` → `RouteNotFound` (covers no match and ASP.NET's 405 endpoint). Otherwise stores `EdgeRoute` in `HttpContext.Features` (`context.Features.Get<EdgeRoute>()` for later steps), removes `Authorization` before the proxy, calls next. Later tasks insert their steps here in §3 order.
  - `ProxyPipeline` (YARP `MapReverseProxy` pipeline step): times the forward for `gateway_upstream_seconds`. Task 3 adds error mapping here.
  - `GatewayMetrics` (`MeterName = "Bloom.Gateway"`, meter from `IMeterFactory`): `void Request(string route, int status, double totalSeconds, double? upstreamSeconds)` records `gateway_requests_total{route,status}`, `gateway_overhead_seconds{route}` (= total − upstream, or total when not proxied) and `gateway_upstream_seconds{route}` (proxied only). Later tasks add `AuthFailure(string reason)`, `RateLimited(string policy)`, `SetRedisDegraded(bool)`, `JwksRefresh(string result)`.
  - YARP X-Forwarded transforms off on every route (P9).
  - Tests: `MetricCapture(IServiceProvider services)` — listens only to instruments whose `Meter.Scope` is that host's `IMeterFactory`; `long Sum(string name, params (string Tag, string Value)[] tags)`, `int Count(string name, ...)` (histogram measurements), `double Gauge(string name)`.

- [ ] **Step 1: Write the failing tests** in `RoutingTests.cs`

```csharp
[Theory, MemberData(nameof(RouteTable))]  // one row per route-table line above (dlq: all 8)
public void Route_config_matches_LLD_5_1(string routeId, string? methods, string path, string cluster,
    string auth, string? roles, string? policy, string? timeout, string? maxBody, bool checkOrigin)
// Reads routes via IProxyStateLookup: Match.Path, Match.Methods, ClusterId, Metadata values equal the row;
// Timeout "00:00:05"/"00:00:15"/"00:00:10"; notifications-hub TimeoutPolicy == "Disable". Also asserts the
// configured route count == table count (no extra routes).

[Theory, MemberData(nameof(SampleRequests))]
public async Task Requests_reach_the_route_cluster(string method, string path, string cluster)
// One sample per route (e.g. GET /api/catalog/products/1 → catalog; POST /api/admin/dlq/payment/x/replay → payment;
// GET /api/notifications/hub/negotiate → notification; GET /api/notifications/unread → notification;
// GET /api/cart → order (P2)). Echo path == $"/{cluster}{path}". Sends Origin: https://bloom.test.

[Theory]
[InlineData("POST", "/api/catalog/x")] [InlineData("GET", "/api/checkout")] [InlineData("GET", "/webhooks/stripe")]
[InlineData("GET", "/api/admin/dlq/unknown/x")] [InlineData("GET", "/api/launches")]
public async Task Unlisted_method_or_path_is_404_ROUTE_NOT_FOUND(string method, string path)  // never 405

[Fact] public async Task G01_forged_x_bloom_headers_are_stripped()
// client sends x-bloom-user-id, x-bloom-role: admin, X-Bloom-User-Type → upstream sees none of them
[Fact] public async Task Request_id_is_a_fresh_guid()     // client x-bloom-request-id: abc → upstream value parses as Guid, != "abc"; differs per request
[Fact] public async Task Authorization_is_not_forwarded() // GET /api/auth/x with Authorization → upstream has no authorization header
[Fact] public async Task Other_client_headers_pass_through() // Accept-Language and Cookie reach upstream unchanged
[Fact] public async Task No_forwarded_headers_are_added()    // upstream sees no x-forwarded-for/-proto/-host/-prefix
[Fact] public async Task Security_headers_on_proxied_and_error_responses()
// proxied 200 and a 404: Strict-Transport-Security "max-age=31536000", X-Content-Type-Options "nosniff",
// Referrer-Policy "no-referrer"; no Server, no X-Powered-By (echo sends both)
[Fact] public async Task Request_metrics_are_recorded()
// proxied GET → gateway_requests_total{route=catalog-public,status=200} 1; overhead and upstream histograms 1 each;
// GET /nope → gateway_requests_total{route=none,status=404} 1; no upstream measurement for it
```

- [ ] **Step 2: Run** `dotnet test --project src/Gateway/Bloom.Gateway.Tests --filter-class *RoutingTests` — Expected: FAIL.

- [ ] **Step 3: Implement** the route table in `appsettings.json`, `EdgeRoute`, `RequestHygiene`, `EdgeMiddleware`, `ProxyPipeline`, `GatewayMetrics`.

- [ ] **Step 4: Run** the filter, then the whole project — Expected: PASS.

- [ ] **Step 5: Commit** — `git add -A && git commit -m "feat(gateway): route table, proxy, header hygiene and request metrics"`.

---

### Task 3: Upstream timeouts, upstream errors, request limits

**Files:**
- Modify: `GatewayApp.cs`, `Edge/EdgeMiddleware.cs`, `Edge/ProxyPipeline.cs`, `appsettings.json` (`Kestrel:Limits:MaxRequestBodySize = 1048576`, `MaxRequestHeadersTotalSize = 32768`)
- Test: `UpstreamTests.cs`

**Interfaces:**
- Consumes: `EdgeRoute.MaxBodyBytes`, `ProxyPipeline`, `GatewayErrors` (Tasks 1–2).
- Produces:
  - `RequestTimeoutOptions.DefaultPolicy` = 5 s; routes with `Timeout` override; the hub has `TimeoutPolicy: Disable`.
  - `EdgeMiddleware` sets `IHttpMaxRequestBodySizeFeature.MaxRequestBodySize = route.MaxBodyBytes` when present (Kestrel enforces both `Content-Length` and chunked bodies).
  - `ProxyPipeline` maps `IForwarderErrorFeature` per P15 with `GatewayErrors.WriteAsync`.
  - Headers over 32 KB → Kestrel's own 431 (spec §7).

- [ ] **Step 1: Write the failing tests** in `UpstreamTests.cs`

```csharp
[Fact] public async Task Default_route_times_out_after_5s_with_504()
// GET /api/catalog/x, x-test-delay-ms: 8000 → 504, code 4, reason UPSTREAM_TIMEOUT; elapsed 4.5–6.5 s
[Fact] public async Task Route_timeout_override_applies()
// Config ReverseProxy:Routes:webhook-stripe:Timeout = 00:00:00.5; POST /webhooks/stripe with delay 3000 → 504 in < 2 s
[Fact] public async Task Upstream_5xx_passes_through_untouched_once()
// x-test-status: 500 → 500, body "boom", Upstream.RequestCount == 1
[Fact] public async Task Unreachable_upstream_is_503_UPSTREAM_UNAVAILABLE()
// cluster catalog d1 = http://127.0.0.1:1/ → 503, code 14, reason UPSTREAM_UNAVAILABLE
[Theory]
[InlineData("/api/auth/sign-in", 1048576)] [InlineData("/webhooks/stripe", 262144)] [InlineData("/webhooks/shippo/s3cret", 65536)]
public async Task Body_limit_per_route(string path, int limit)
// POST limit bytes → 200 with bodyLength == limit; POST limit+1 bytes → 413, code 3, reason PAYLOAD_TOO_LARGE
[Fact] public async Task Chunked_body_over_limit_is_413()
// POST /webhooks/shippo/s (no Content-Length, chunked StreamContent, 65537 bytes) → 413 PAYLOAD_TOO_LARGE
[Fact] public async Task Headers_over_32KB_get_431()   // one 33 000-byte header → 431
```

- [ ] **Step 2: Run** `--filter-class *UpstreamTests` — Expected: FAIL.

- [ ] **Step 3: Implement** the timeout policy, body-size feature, forwarder error mapping, Kestrel limits.

- [ ] **Step 4: Run** the filter, then the whole project — Expected: PASS.

- [ ] **Step 5: Commit** — `git add -A && git commit -m "feat(gateway): upstream timeouts, error mapping and request limits"`

---

### Task 4: JWT, JWKS cache, auth modes, role check, trusted context

**Files:**
- Create: `Auth/Caller.cs`, `Auth/JwksCache.cs`, `Auth/TokenValidator.cs`
- Modify: `Edge/EdgeMiddleware.cs`, `GatewayApp.cs` (map `/readyz`, register `TimeProvider.System` unless already registered), `Observability/GatewayMetrics.cs`
- Create (tests): `Infrastructure/FakeJwks.cs`, `TestTokens.cs`, `LogCapture.cs`; modify `TestGateway.cs`, `RoutingTests.cs`
- Test: `AuthTests.cs`

**Interfaces:**
- Consumes: `EdgeRoute`, `AuthMode`, `GatewayErrors`, `GatewayMetrics` (Tasks 1–2).
- Produces:
  - `public sealed record Caller(Guid UserId, string UserType, string? Role, long AuthTime, long IssuedAt)` (`UserType` = `customer` | `staff`; `IssuedAt` = `iat` Unix s; `IsStaff => UserType == "staff"`).
  - `JwksCache : BackgroundService` — fetches `Gateway:JwksUrl` with `HttpClient`, parses `JsonWebKeySet`; timing per P14 on `TimeProvider`; `bool IsLoaded`; `ValueTask<bool> RefreshForUnknownKidAsync(string kid)` (true when the kid is known after the call); `IReadOnlyList<SecurityKey> Keys`. Records `jwks_refresh_total{result=success|error}` per fetch.
  - `TokenValidator.ValidateAsync(string token) : Task<Caller?>` — `JsonWebTokenHandler`, `ValidAlgorithms = ["ES256"]`, `ValidIssuer = "bloom-identity"`, `ValidAudience = "bloom"`, `ClockSkew = 30 s`, signed tokens required; unknown `kid` → `RefreshForUnknownKidAsync` then one more attempt; claim rules per P6. Null = `TOKEN_INVALID`.
  - `EdgeMiddleware` auth step (§3 step 5) by `route.Auth`:
    - `Public`, `None`: `Authorization` ignored (spec §7).
    - `PublicOptionalUser`: absent → anonymous; present → must validate (else 401).
    - `Customer`, `Staff`, `AnyUser`: absent / not `Bearer` → `TokenMissing`; invalid → `TokenInvalid`.
    - Role step (§3 step 7, after Task 5's Redis step): `Customer` route needs `customer`; `Staff` route needs `staff` with `Role ∈ route.Roles`; `AnyUser` takes either; else `RoleNotAllowed`.
    - Context step (§3 step 8, §4.3): `x-bloom-user-id` (`D` form), `x-bloom-user-type`, `x-bloom-role` (staff only), `x-bloom-auth-time`. The validated `Caller` is stored in `HttpContext.Features` for Tasks 5–6.
    - `gateway_auth_failures_total{reason}` for the three token reasons (P8).
  - `GET /readyz` → 200 when `JwksCache.IsLoaded` and YARP has routes, else 503.
  - Tests: `TestKeys` (`static TestKeys Create(string kid)`; P-256 `ECDsaSecurityKey`; `JsonWebKey PublicJwk`); `TestTokens.Issue(TestKeys key, string typ = "customer", string? role = null, Guid? sub = null, DateTimeOffset? iat = null, TimeSpan? lifetime = null, Action<IDictionary<string, object>>? edit = null) : string` (ES256, `iss`/`aud`/`auth_time` set; `edit` can remove/replace claims), plus `IssueHs256(...)`, `IssueUnsigned(...)` (`alg: none`), `IssueRs256(...)`. `FakeJwks : IAsyncDisposable` — `static Task<FakeJwks> StartAsync(params TestKeys[] keys)`, `Uri Url`, `void SetKeys(params TestKeys[] keys)`, `bool Available { set; }` (false → 503), `int FetchCount`. `TestGateway` gains `FakeJwks Jwks`, `TestKeys DefaultKey` (served by `Jwks`), `FakeTimeProvider Clock` (start = real now, registered as the app's `TimeProvider`), and `TestGatewayOptions.WaitForReady` (default true: poll `/readyz` == 200 before returning). `LogCapture` — an `ILoggerProvider` added by `TestGateway` recording category, level, formatted message and state values at `Trace` level.

- [ ] **Step 1: Update `Requests_reach_the_route_cluster`** rows with a caller column (`anon`, `customer`, `staff:<role>`); the test sends `TestTokens.Issue(DefaultKey, …)` for non-anon rows.

- [ ] **Step 2: Write the failing tests** in `AuthTests.cs`

```csharp
[Fact] public async Task G01_upstream_sees_token_identity()
// customer token + forged x-bloom-user-id on GET /api/cart → upstream x-bloom-user-id == sub ("D"), x-bloom-user-type customer,
// x-bloom-auth-time == auth_time, no x-bloom-role; staff admin token on /api/admin/orders/x → x-bloom-role admin
[Fact] public async Task G02_HS256_token_is_TOKEN_INVALID()      // 401, code 16, reason TOKEN_INVALID
[Fact] public async Task Alg_none_token_is_TOKEN_INVALID()
[Fact] public async Task Missing_token_is_TOKEN_MISSING()        // GET /api/cart without Authorization; also "Basic x"
[Theory] [MemberData(nameof(BadTokens))]
public async Task Bad_tokens_are_TOKEN_INVALID(string name)
// expired 31+ s ago; wrong iss; wrong aud; RS256; iat 60 s in future; sub missing; sub "abc"; typ "robot";
// staff without role; auth_time missing; "garbage"; signed by a key not in JWKS (unknown kid after refetch)
[Fact] public async Task Expired_within_30s_skew_is_accepted()   // exp = now − 20 s → 200
[Fact] public async Task Customer_token_on_admin_route_is_ROLE_NOT_ALLOWED()   // 403, code 7
[Fact] public async Task Staff_token_on_customer_route_is_ROLE_NOT_ALLOWED()   // staff admin → /api/cart
[Fact] public async Task Staff_role_not_listed_is_ROLE_NOT_ALLOWED()          // warehouse → /api/admin/payments/x
[Fact] public async Task Catalog_admin_allows_support()                         // P1: support → /api/admin/catalog/x 200
[Fact] public async Task Any_user_route_accepts_customer_and_staff()           // /api/me
[Fact] public async Task Optional_user_route_reads_a_present_token()
// GET /api/catalog/x: anonymous → 200, no x-bloom-user-id; customer token → 200 with identity; bad token → 401 TOKEN_INVALID
[Theory]
[InlineData("POST", "/api/auth/sign-in")] [InlineData("POST", "/auth/refresh")]
[InlineData("POST", "/webhooks/stripe")] [InlineData("GET", "/api/notifications/hub/negotiate")]
public async Task Stale_token_is_ignored_on_public_routes(string method, string path)  // Review Focus 1
// expired token + Origin https://bloom.test → 200 from upstream; upstream has no x-bloom-user-id
[Fact] public async Task G10_unknown_kid_refetches_JWKS_once()
// Jwks.SetKeys(old, rotated); token signed by rotated → 200; FetchCount +1. A token with a second unknown kid within 30 s
// → 401 and FetchCount unchanged; Clock.Advance(30 s) → that kid triggers a fetch again
[Fact] public async Task Concurrent_unknown_kid_requests_fetch_once()   // 20 parallel requests, new kid → all 200, FetchCount +1
[Fact] public async Task JWKS_refreshes_every_10_minutes()              // Clock.Advance(10 min) → FetchCount +1 (poll); jwks_refresh_total{result=success}
[Fact] public async Task Readyz_waits_for_JWKS()
// Jwks.Available=false, WaitForReady=false → /readyz 503, /healthz 200, jwks_refresh_total{result=error} ≥ 1;
// Available=true, Clock.Advance(1 s) → /readyz 200 (poll)
[Fact] public async Task Auth_failure_metric_by_reason()   // one missing + one invalid → TOKEN_MISSING 1, TOKEN_INVALID 1
[Fact] public async Task Authorization_and_cookie_are_never_logged()
// requests with a token + Cookie "bloom_rt=s3cr3t" producing 200, 401 and 403 → no captured log text or state value
// contains the token string or "s3cr3t"
```

- [ ] **Step 3: Run** `--filter-class *AuthTests` and `*RoutingTests` — Expected: FAIL.

- [ ] **Step 4: Implement** `Caller`, `JwksCache`, `TokenValidator`, the auth/role/context steps, `/readyz`, the two metrics.

- [ ] **Step 5: Run** the whole project — Expected: PASS.

- [ ] **Step 6: Commit** — `git add -A && git commit -m "feat(gateway): JWT validation, JWKS cache, roles and trusted identity context"`

---

### Task 5: Revocation check and rate limits in one Lua call; client IP

**Files:**
- Create: `Limits/RateLimitPolicy.cs`, `Limits/EdgeStore.cs`, `Limits/edge.lua` (embedded resource)
- Modify: `Edge/EdgeMiddleware.cs`, `Edge/EdgeRoute.cs` (policy name must exist), `GatewayApp.cs` (register `IConnectionMultiplexer` per P16), `appsettings.json` (`Gateway:RateLimits`), `Observability/GatewayMetrics.cs`
- Test: `EdgeLimitTests.cs`

**Interfaces:**
- Consumes: `Caller`, `EdgeRoute`, `TimeProvider`, `GatewayErrors`, `TestGateway.Clock`, `Containers.Redis` (Tasks 1–4).
- Produces:
  - `public enum Algorithm { FixedWindow, SlidingWindow, TokenBucket }`, `public enum LimitKey { Ip, User }`, `public sealed record RateLimitPolicy(string Name, Algorithm Algorithm, LimitKey Key, int Limit, int WindowSeconds, int Burst, double PerMinute)` bound from `Gateway:RateLimits:<name>` (fixed/sliding use `Limit`, `WindowSeconds`; token bucket uses `Burst`, `PerMinute`). Values from §4.2:
    `global` FixedWindow/Ip 300/60 · `auth` SlidingWindow/Ip 10/60 · `refresh` SlidingWindow/Ip 20/60 · `user` TokenBucket/User 60, 120 · `checkout` TokenBucket/User 3, 6 · `wr-join` TokenBucket/User 3, 5 · `wr-status` TokenBucket/User 5, 30 · `webhook` FixedWindow/Ip 600/60.
  - `public sealed record EdgeDecision(bool Revoked, bool Allowed, TimeSpan RetryAfter, string? DeniedPolicy)`.
  - `EdgeStore.CheckAsync(Caller? caller, string clientIp, RateLimitPolicy? routePolicy, string requestId) : Task<EdgeDecision>` — one `ScriptEvaluateAsync` of `edge.lua` per call; `now` = `TimeProvider.GetUtcNow()` in ms; global skipped when `routePolicy.Name == "webhook"`; route policy subject = `caller.UserId` for `User` keys, `clientIp` for `Ip`; `routePolicy` null when the route has no policy, or for `public-optional-user` when anonymous. Throws `RedisException` when Redis is unreachable (Task 6 handles it).
  - `edge.lua` follows §6.2 steps 1–5 with P3, P18 and these rules: revoked if `iat × 1000 < tonumber(GET rev:{sub})` → return at once (no counting); fixed window `INCR` + `PEXPIRE window+5 s` on first increment, `retry_after` = ms to next window; sliding log member = `now:requestId`, key `PEXPIRE` = window; token bucket hash `{tokens, ts}` with rate in tokens/ms, `PEXPIRE` = `2 × burst / rate`; returns `{revoked, allowed, retry_after_ms, denied_policy}`. Keys exactly §4.1.
  - `EdgeMiddleware` Redis step (§3 step 6) between auth and role: revoked → `TokenRevoked` (+ auth-failure metric); denied → `RateLimited` with `RetryAfter` and `gateway_rate_limited_total{policy}`.

- [ ] **Step 1: Write the failing tests** in `EdgeLimitTests.cs` (each test sets `Clock` explicitly; every gateway has its own `ClientIp` and token `sub`)

```csharp
[Fact] public void Rate_limits_match_LLD_4_2()   // the 8 bound policies equal the values above; no others
[Fact] public async Task G03_token_issued_before_revocation_is_TOKEN_REVOKED()
// SET rev:{sub} = iat*1000 + 1 → GET /api/cart 401, code 16, reason TOKEN_REVOKED; no fw:global:{ip}:* key created
[Fact] public async Task Revocation_in_the_same_millisecond_as_iat_does_not_revoke()   // rev = iat*1000 → 200
[Fact] public async Task G07_11th_sign_in_within_60s_is_429_with_Retry_After()
// POST /api/auth/sign-in at t0+0 … t0+9 s → 200 ×10; 11th at t0+10 s → 429, code 8, reason RATE_LIMITED, Retry-After "50"
[Fact] public async Task G07b_301st_request_in_a_window_is_429()
// Clock = window start + 10 s; 300 anonymous GET /api/catalog/x → 200; 301st → 429, Retry-After "50"
[Fact] public async Task G07c_4th_checkout_in_a_second_is_429()
// customer, 4 × POST /api/checkout at one instant → 4th 429, Retry-After "10"; Clock.Advance(10 s) → 200
[Fact] public async Task Sliding_window_has_no_boundary_burst()
// 11 sign-ins every 3 s from 30 s to 60 s past a fixed-window boundary → 11th 429
[Fact] public async Task Signed_in_burst_of_30_is_allowed()            // 30 × GET /api/catalog/x with token, one instant → all 200
[Fact] public async Task Waiting_room_status_every_5s_is_never_limited() // 120 × GET …/waiting-room, Clock +5 s each → all 200
[Fact] public async Task Webhooks_skip_global_and_use_webhook_limit()
// 600 × POST /webhooks/stripe in one window → 200; 601st → 429; no fw:global:{ip}:* key exists
[Fact] public async Task Global_denial_leaves_route_policy_untouched()  // after global is spent, a sign-in → 429; no sw:auth:{ip} key
[Fact] public async Task Route_denial_still_counts_global()             // 11 sign-ins → fw:global:{ip}:{n} == 11
[Fact] public async Task Redis_keys_and_ttls_follow_4_1()
// fw:global:{ip}:{floor(now/60000)} PTTL in (60000, 65000]; sw:auth:{ip} PTTL ≤ 60000; tb:user:{sub} has fields tokens, ts, PTTL ≤ 60000;
// tb:checkout:{sub} PTTL ≤ 60000
[Fact] public async Task Rate_limited_metric_by_policy()               // after G07-style denial: gateway_rate_limited_total{policy=auth} == 1
[Fact] public async Task G08_spoofed_X_Forwarded_For_is_ignored()
// (a) default trusted 127.0.0.1: X-Forwarded-For "6.6.6.6, {ClientIp}" → fw:global:{ClientIp}:* exists, fw:global:6.6.6.6:* doesn't;
// (b) Gateway:TrustedProxies empty: X-Forwarded-For "6.6.6.7" → fw:global:6.6.6.7:* doesn't exist, fw:global:127.0.0.1:* does
```

- [ ] **Step 2: Run** `--filter-class *EdgeLimitTests` — Expected: FAIL.

- [ ] **Step 3: Implement** `RateLimitPolicy` binding, `edge.lua`, `EdgeStore`, the middleware step, the metric.

- [ ] **Step 4: Run** the whole project — Expected: PASS.

- [ ] **Step 5: Commit** — `git add -A && git commit -m "feat(gateway): revocation and rate limits in one Redis Lua call"`

---

### Task 6: Redis outage: staff fail closed, per-pod fallback limiters

**Files:**
- Create: `Limits/FallbackLimiters.cs`
- Modify: `Edge/EdgeMiddleware.cs`, `Observability/GatewayMetrics.cs`
- Test: `RedisOutageTests.cs`

**Interfaces:**
- Consumes: `EdgeStore`, `EdgeDecision`, `RateLimitPolicy`, `Caller` (Tasks 4–5).
- Produces:
  - `FallbackLimiters.Check(Caller? caller, string clientIp, RateLimitPolicy? routePolicy) : EdgeDecision` — one `PartitionedRateLimiter<string>` per policy (partition = subject, created on demand): `FixedWindowRateLimiter` (`PermitLimit`, `Window`), `SlidingWindowRateLimiter` (P10), `TokenBucketRateLimiter` (`TokenLimit = Burst`, 1 token per `60 s / PerMinute`, `AutoReplenishment = true`), `QueueLimit = 0`; same global-then-route order and webhook exemption as Task 5; `RetryAfter` from lease metadata (P4 when absent: 1 s).
  - `EdgeMiddleware`: `RedisException` from `EdgeStore` → `SetRedisDegraded(true)`; staff `Caller` → `AuthStoreUnavailable`; otherwise `FallbackLimiters.Check` (revocation skipped). A successful `EdgeStore` call → `SetRedisDegraded(false)`.
  - `gateway_redis_degraded` observable gauge (0/1).

- [ ] **Step 1: Write the failing tests** in `RedisOutageTests.cs` (outage = `Gateway:Redis = 127.0.0.1:1,abortConnect=false,connectTimeout=200`)

```csharp
[Fact] public async Task G05_staff_request_during_Redis_outage_is_503()
// staff admin → GET /api/admin/orders/x → 503, code 14, reason AUTH_STORE_UNAVAILABLE
[Fact] public async Task Staff_token_on_optional_user_route_during_outage_is_503()   // GET /api/catalog/x
[Fact] public async Task G06_customer_request_during_outage_is_served_with_in_memory_limits()
// GET /api/cart → 200; 4 × POST /api/checkout → 4th 429 RATE_LIMITED with Retry-After ≥ 1
[Fact] public async Task Anonymous_during_outage_uses_in_memory_limits()   // 11 sign-ins → 11th 429
[Fact] public async Task Invalid_token_during_outage_is_401()              // HS256 → TOKEN_INVALID (not 503)
[Fact] public async Task Redis_degraded_gauge_is_1_during_outage()
[Fact] public async Task Recovers_when_Redis_returns()   // Review Focus 2
// own redis:8.2.3 container (Testcontainers, disposed at end), connection string with asyncTimeout=500;
// staff 200 → PauseAsync → staff 503, gauge 1 → UnpauseAsync → staff 200 (poll ≤ 10 s), gauge 0, fw:global:{ip}:* written again
```

- [ ] **Step 2: Run** `--filter-class *RedisOutageTests` — Expected: FAIL.

- [ ] **Step 3: Implement** `FallbackLimiters`, the outage branch, the gauge.

- [ ] **Step 4: Run** the whole project — Expected: PASS.

- [ ] **Step 5: Commit** — `git add -A && git commit -m "feat(gateway): Redis outage handling with per-pod fallback limiters"`

---

### Task 7: CORS, Origin checks, notification hub

**Files:**
- Modify: `GatewayApp.cs` (CORS default policy), `Edge/EdgeMiddleware.cs` (origin step)
- Test: `CorsOriginTests.cs`, `HubTests.cs`

**Interfaces:**
- Consumes: `EdgeRoute.CheckOrigin`, `Gateway:AllowedOrigins`, `GatewayErrors.OriginNotAllowed` (Tasks 1–2).
- Produces:
  - Default CORS policy (§6.4): `WithOrigins(Gateway:AllowedOrigins)`, `AllowCredentials()`, `AllowAnyHeader()`, `AllowAnyMethod()`, `SetPreflightMaxAge(10 min)`; runs before routing (§3 step 3).
  - `EdgeMiddleware` origin step, right after route match: `route.CheckOrigin` and `Origin` missing or not in `AllowedOrigins` (exact, ordinal-ignore-case) → `OriginNotAllowed` (P5).

- [ ] **Step 1: Write the failing tests**

```csharp
// CorsOriginTests.cs
[Fact] public async Task Preflight_from_allowed_origin()
// OPTIONS /api/cart, Origin https://bloom.test, Access-Control-Request-Method POST → 204;
// Access-Control-Allow-Origin == origin, Access-Control-Allow-Credentials "true", Access-Control-Max-Age "600"
[Fact] public async Task Preflight_from_foreign_origin_gets_no_CORS_headers()
[Fact] public async Task Error_responses_carry_CORS_headers()       // GET /api/cart, allowed Origin, no token → 401 with Access-Control-Allow-Origin
[Fact] public async Task G09_refresh_from_foreign_origin_is_403()   // POST /auth/refresh, Origin https://evil.test → 403, code 7, ORIGIN_NOT_ALLOWED
[Fact] public async Task Refresh_without_Origin_is_403()
[Fact] public async Task Refresh_from_allowed_origin_is_proxied_with_cookie()  // Cookie bloom_rt=x reaches upstream

// HubTests.cs (ClientWebSocket to ws://{Address}/api/notifications/hub?ticket=t1 with Origin via SetRequestHeader)
[Fact] public async Task Hub_upgrade_with_allowed_origin_is_proxied_without_JWT()
// connects; text frame round-trips through the echo
[Fact] public async Task Hub_upgrade_from_foreign_origin_is_403()   // connect fails; plain GET with that Origin → 403 ORIGIN_NOT_ALLOWED
[Fact] public async Task Hub_connection_outlives_the_route_timeout()  // open 7 s, then a frame still round-trips
```

- [ ] **Step 2: Run** `--filter-class *CorsOriginTests` and `*HubTests` — Expected: FAIL (hub upgrade test may already pass; the Origin tests must fail).

- [ ] **Step 3: Implement** the CORS policy and the origin step.

- [ ] **Step 4: Run** the whole project — Expected: PASS.

- [ ] **Step 5: Commit** — `git add -A && git commit -m "feat(gateway): CORS, Origin checks and notification hub proxying"`

---

### Task 8: gateway-worker revocation consumer

**Files:**
- Create: `Worker/RevocationWriter.cs`, `Worker/RevocationConsumer.cs`
- Modify: `GatewayApp.cs` (worker mode registers them; `Gateway:RabbitMq`, `Gateway:Redis`)
- Create (tests): `Infrastructure/TestWorker.cs`
- Test: `RevocationWorkerTests.cs`

**Interfaces:**
- Consumes: `TokensRevokedV1`, `Envelope.Parse` / `GetPayload<T>` (Bloom.Shared), `MessageNames.EventExchange("identity")`, `IConnectionMultiplexer` (P16), `TestGateway`, `TestTokens` (Tasks 1, 4).
- Produces:
  - `RevocationWriter.WriteAsync(TokensRevokedV1 message) : Task` — Lua: `SET rev:{accountId:D} max(existing, issuedBefore) PX 960000` (§6.3).
  - `RevocationConsumer : BackgroundService` per spec §6 and P17: declares topic exchange `identity.events` (durable), quorum queue `api-gateway.tokens-revoked` (`x-queue-type=quorum`, durable), binding `identity.tokens-revoked.v1`; per delivery: parse failure (`JsonException`, `PermanentFailureException`) or `MessageType != "identity.tokens-revoked.v1"` → error log (no payload values), ACK; else `WriteAsync`, retrying every 1 s on `RedisException` until written or stopping (no ACK on stop), then ACK.
  - Tests: `TestWorker : IAsyncDisposable` — `static Task<TestWorker> StartAsync(Containers containers, string? redis = null)` (Mode=worker); `static byte[] RevocationEnvelope(Guid accountId, long issuedBefore)` builds an `Envelope` (`identity.tokens-revoked.v1`, kind event, producer identity).

- [ ] **Step 1: Write the failing tests** in `RevocationWorkerTests.cs` (each test uses fresh account IDs; publish via a test RabbitMQ channel to `identity.events`)

```csharp
[Fact] public async Task Worker_declares_queue_and_binding()
// rabbitmqctl list_queues name type → api-gateway.tokens-revoked quorum; list_bindings shows identity.events → queue, key identity.tokens-revoked.v1
[Fact] public async Task G04_latest_issuedBefore_wins()
// publish 2000, 1000, 2000 for one account → poll until queue empty; GET rev:{id} == "2000"; PTTL in (950000, 960000]
[Fact] public async Task G03_revocation_reaches_the_gateway_within_seconds()
// TestGateway + TestWorker on the same Redis; customer token (iat = now) → 200; publish issuedBefore = now ms + 1000
// → GET /api/cart returns 401 TOKEN_REVOKED within 2 s (poll); elapsed logged
[Theory] [InlineData("not-json")] [InlineData("no-payload")] [InlineData("unknown-type")] [InlineData("missing-issuedBefore")]
public async Task Malformed_messages_are_acked_and_skipped(string kind)
// queue drains, no rev key; a valid message published afterwards is applied
[Fact] public async Task Redis_outage_holds_the_delivery_until_written()   // Review Focus 5
// own redis container paused; publish → after 2 s: messages_unacknowledged == 1, no key; unpause → key written (poll), queue drained
[Fact] public async Task Worker_reconnects_after_the_broker_closes_its_connection()  // Review Focus 5
// rabbitmqctl close_all_connections "test" → publish → key written (poll ≤ 15 s)
```

- [ ] **Step 2: Run** `--filter-class *RevocationWorkerTests` — Expected: FAIL.

- [ ] **Step 3: Implement** `RevocationWriter`, `RevocationConsumer`, worker-mode registration.

- [ ] **Step 4: Run** the whole solution: `dotnet build -c Release` (0 warnings) and `dotnet test -c Release` — Expected: all PASS; `docker ps -a` shows no containers from the run.

- [ ] **Step 5: Commit** — `git add -A && git commit -m "feat(gateway): gateway-worker revocation consumer"`
