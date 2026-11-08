# Notification Service Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the notification service of `doc/low-level-design/09-notification.md` (API and worker as one app): customer API, SignalR hub with Redis backplane, the 17 event consumers, the email sender (Resend or SMTP) and the purges, with N-01 … N-11 and every §8 edge case tested.

**Architecture:** One ASP.NET Core app `Bloom.Notification`; `Notification:Mode` = `api` | `worker` (HLD-ADR-014). API mode hosts the transcoded customer gRPC service, the SignalR hub at `/api/notifications/hub` behind a ticket and Origin gate, the cross-pod disconnect listener and the shared DLQ admin. Worker mode runs the `Bloom.Shared` outbox relay, consumer host, retention purger and gauge poller, plus `EmailSender` and `DailyPurge`. One `EventConsumer` class handles all 17 queues. Consumers queue their pushes and disconnect commands on the `DbContext`; an EF transaction interceptor runs them after commit. Email goes through `IEmailProvider` (HLD-ADR-018): `ResendEmailProvider` (`HttpClient` + `System.Text.Json`) or `SmtpEmailProvider` (`System.Net.Mail`).

**Tech Stack:** .NET 10, ASP.NET Core gRPC + JSON transcoding, SignalR + StackExchange.Redis backplane, EF Core 10 + Npgsql, Scriban, Bloom.Shared, xUnit v3, Testcontainers (PostgreSQL, RabbitMQ, Redis, Mailpit), Microsoft.Extensions.TimeProvider.Testing, Microsoft.AspNetCore.SignalR.Client (tests).

**Spec:** `docs/superpowers/specs/2026-11-08-notification-design.md` (approved; cited "spec §x"). Design source: `doc/low-level-design/09-notification.md` (cited "§x"). Where the spec is silent, 09-notification decides. Where both are silent, the owner items and rulings below decide.

## Owner items (plan approved 2026-11-08)

Items 1–2 approved as recommended with the plan. Each is visible outside notification and covered by neither 09-notification nor the spec. The recommendation is what the tasks build.

1. **Hub rejection responses.** The spec says "reject" without a status. **Recommendation:** answer before the upgrade, with no body: disallowed or missing `Origin` → `403`; missing, unknown or used ticket → `401`; a request to the hub path that isn't a WebSocket upgrade → `400`, ticket untouched. Doc edit 5. (Task 6)
2. **Events for a deleted account.** After `account-deleted`, a later event for that account (for example order cancelling its open orders) finds no `contacts` row. §6.1 step 2 makes that transient, so it retries and dead-letters with an alert. **Recommendation:** accept it as written (no tombstone table). Nothing is sent to the deleted account, and Ops discards the DLQ message. No doc edit. (Task 7)

## Global Constraints

- Build settings from `Directory.Build.props` (net10.0, Nullable, ImplicitUsings, `TreatWarningsAsErrors`). Central versions in `Directory.Packages.props`. New packages are exactly those of spec §5: `Scriban`, `Microsoft.AspNetCore.SignalR.StackExchangeRedis`, `Microsoft.AspNetCore.SignalR.Client` (tests), the latest stable versions (`10.0.x` for the two ASP.NET Core packages, matching `Microsoft.AspNetCore.TestHost`). Mailpit uses the generic container of the `Testcontainers` core that `Testcontainers.PostgreSql` already pulls in. Add `Testcontainers.Redis` (already in the props) to the test project.
- No method, message, field, error reason, metric, route or config key beyond 09-notification, the spec, the owner items and the rulings (AGENTS.md §1). No health endpoints. Never touch `doc/`, `Bloom.Shared`, the gateway, any other service or its `.Contracts`, Dockerfiles, Compose or manifests (spec §3).
- The anti-goals of spec §3 are hard rules: one in-app row and one email job per event and type (NR-01); a verification code never in plain text in storage or logs and gone from `data` after sending (NR-03); no push before commit; no customer reads or marks another's rows; no WebSocket without a valid unused ticket and an allowed Origin; every template variable HTML-escaped in HTML bodies and no tracking pixel; no test reaches the internet.
- Idempotency key exactly `bloom:email:<id>` (UUID `D` format). Redis keys exactly `ws:ticket:{ticket}` (TTL 30 s) and channel `notification:disconnect`. SignalR group exactly `user:{accountId}`; client method `notification` with `{id, type, title, link, createdAt}`.
- Business times come from the injected `TimeProvider`. SQL takes timestamps as parameters, never `now()`.
- Every gRPC error has the `BloomErrors.Create` shape (ErrorInfo domain `bloom`). Reasons come from Task 1's table only.
- Docker via Colima: never run `colima`. If the daemon is unreachable, stop and report. Never `dotnet run`. Test containers must be gone after the run. The Resend base URL in tests is the fake's; `FakeResend` fails any request it doesn't serve.
- Every test case N-01 … N-11 has at least one test whose method name starts with its ID: `N01_…`. Test names below are binding; the listed assertions are the minimum.
- Test command: `dotnet test --project src/Notification/Bloom.Notification.Tests`, narrowed with `--filter-class '*<Class>'`.

## Review Focus

1. **Two senders on the same jobs.** Two `EmailSender.RunOnceAsync` in parallel over 60 due jobs: every job sent exactly once (60 distinct idempotency keys, 60 requests), all `sent`. Test in Task 8.
2. **A consumer transaction that rolls back.** A socket is open; the event's transaction inserts the row, then rolls back. No push arrives (wait 1 s), no row. The same event committed → exactly one push. Test in Task 7.
3. **Markup in payload values.** Name `<b>Al & "Bo"</b>`, an order number and a tracking number with `<script>`: the HTML body has them escaped, the text body and subject have them raw, the in-app title and body are stored raw. Test in Task 3.
4. **A page token from someone else's row.** Customer A sends a well-formed token built from customer B's last row: A gets only A's rows, never B's. Test in Task 5.
5. **One ticket, two simultaneous connects.** Ten parallel connects with one ticket: exactly one connected, nine `401`, `ws_rejected_total{reason=ticket}` = 9. Test in Task 6.

## Rulings in this plan (builder's calls the spec left open)

- **N1** Tests run the app on `TestServer`. gRPC goes through `GrpcChannel.ForAddress(base, new() { HttpHandler = server.CreateHandler() })`; transcoded calls through `Http`. Tests emulate the gateway with `x-bloom-user-id/type/role`.
- **N2** The app never migrates at startup; tests migrate. `DesignTimeFactory` uses env `Notification__Database` if set, else `Host=design-time;Database=notification`.
- **N3** `TryAddSingleton(TimeProvider.System)`; tests register `FakeTimeProvider` first. SignalR's own keep-alive and client timeout run on system time; only the 15-min lifetime timer uses the app's `TimeProvider`. Hosted loops use `new PeriodicTimer(interval, timeProvider)`, so a worker host under test needs `Clock.Advance` to tick.
- **N4** Config section `Notification`. Both modes need every key (as payment R4), so the DLQ admin and the sender types resolve in either mode. A missing or invalid key throws at build, naming the key.

  | Key | Rule |
  |---|---|
  | `Mode` | `api` or `worker` |
  | `Database`, `RabbitMq`, `Redis` | required |
  | `StorefrontBaseUrl` | absolute URL |
  | `CodeEncryptionKey` | base64 of exactly 32 bytes |
  | `AllowedOrigins` | non-empty array of absolute `scheme://host[:port]` origins |
  | `Email:Provider` | `resend` or `smtp` |
  | `Email:From` | required |
  | `Email:Resend:ApiKey`, `Email:Resend:BaseUrl` (absolute URL) | required when `resend` |
  | `Email:Smtp:Host`, `Email:Smtp:Port` (1–65535); `Email:Smtp:User`, `Email:Smtp:Password` optional | required when `smtp` |

  `appsettings.json`: `Mode=api`, `RabbitMq=amqp://rabbitmq:5672`, `Redis=redis:6379`, `Email:Provider=resend`, `Email:Resend:BaseUrl=https://api.resend.com/`. Secrets, origins, `From` and `StorefrontBaseUrl` come from the deployment.
- **N5** `BloomOptions.ServiceName = "notification"`; telemetry `notification-api` / `notification-worker`; meter `Bloom.Notification`. Notification sends no commands.
- **N6** One `EventConsumer` class, registered with `AddConsumer<EventConsumer>(handler, binding)` once per row of Task 7's table, in both modes (the DLQ admin needs the catalog). Queue `notification.<handler>`.
- **N7** After-commit work: `NotificationDbContext.AfterCommit` (`List<Func<Task>>`). A singleton `AfterCommitInterceptor : DbTransactionInterceptor`, added in `AddDbContext`, runs the list on `TransactionCommittedAsync` (each action in its own `try`, a failure is a warning log, best effort per §6.1 step 5) and clears it on `TransactionRolledBackAsync`. Pushes, disconnect publishes and `notifications_created_total` all go through it.
- **N8** Caller: copy order's `Caller` (`From`, `RequireCustomer`). Missing `x-bloom-*` → `InvalidOperationException` (a server error). The role check runs before validation (spec §6).
- **N9** Validation (`VALIDATION_FAILED`, `metadata["field"]` = proto field name): `page_token` (field `page_token`), `ids` count 1–100 and each a UUID (field `ids`).
- **N10** `FakeResend` is an `HttpMessageHandler` behind the real `ResendEmailProvider` (as payment's `FakeStripe`); spec §8's "in-process HTTP server" is met in-process without a socket.
- **N11** Test isolation: one database per test host; one Redis, broker and Mailpit per run (random account IDs keep tests apart). Only `WorkerTests` start a worker host (xUnit runs one class sequentially, so its hosts never compete for queues). Consumers are tested with `Consume.Async` on an API host (consumers and sender types are registered in both modes); the sender and purge are driven by their `RunOnceAsync` / `PurgeOnceAsync`.
- **N12** `contacts` upsert keeps the newest: `ON CONFLICT (account_id) DO UPDATE SET email, name, updated_at WHERE contacts.updated_at <= excluded.updated_at`, with `updated_at` = the envelope's `OccurredAt`. `order_refs` is `ON CONFLICT (order_id) DO NOTHING` (its values never change for an order).
- **N13** Metrics: `notifications_created_total{type, channel}` with `channel` = `in_app` | `email`, once per row actually inserted, after commit. `ws_connections{pod}` is an up-down counter, `pod` = `Environment.MachineName`. `email_jobs_by_status{status}` is refreshed every 30 s by the worker (`SELECT status, count(*) … GROUP BY status`). `event_to_email_seconds{type}` uses the job's `template`.
- **N14** Ids are `Guid.CreateVersion7()`; `created_at` is the service's clock at consume time.
- **N15** Templates: one embedded file per type, `Templates/<type>.sbn` (logical name = file name), split into sections by lines `--- subject`, `--- html`, `--- text`, and for in-app types `--- title`, `--- body`. Each section is a Scriban template parsed once at startup (a parse error fails the build of the app). Variables are strings with the names below (camelCase), rendered with `StrictVariables = true`. HTML sections render through a `TemplateContext` that HTML-encodes every output value; the other sections render raw.
- **N16** `email_jobs.data` is a JSON object of string values (`jsonb`, mapped as `string` on the entity). Money is formatted at consume time with `Money.Format(cents)` (en-US currency: `$1,234.56`).
- **N17** `attempts` counts send tries (success included); the 5th failed try → `failed`. `last_error` = the provider's reason (`EmailSendException.Reason`), or `decrypt_failed` / `render_failed`. Error logs name the job ID and template, never the address, code or body.

---

## File Structure

```
Bloom.slnx                                     # + Bloom.Notification, Bloom.Notification.Tests
Directory.Packages.props                       # + Scriban, SignalR.StackExchangeRedis, SignalR.Client
src/Notification/Bloom.Notification/
  Bloom.Notification.csproj (Sdk.Web, as Bloom.Payment.csproj; refs Shared + Identity/Order/Fulfillment/Payment .Contracts)
  Program.cs, NotificationApp.cs, NotificationOptions.cs, appsettings.json
  Protos/notification.proto                    # package bloom.notification.v1
  Data/ NotificationDbContext.cs, Entities.cs, AfterCommitInterceptor.cs, DesignTimeFactory.cs, Migrations/
  Errors/NotificationErrors.cs   Context/Caller.cs   Observability/NotificationMetrics.cs
  Templates/ TemplateRenderer.cs, Money.cs, NotificationTypes.cs, *.sbn (13)
  Email/ IEmailProvider.cs, ResendEmailProvider.cs, SmtpEmailProvider.cs, CodeCipher.cs
  Services/CustomerService.cs
  Sockets/ NotificationHub.cs, HubGate.cs, ConnectionRegistry.cs, Tickets.cs, Disconnects.cs, DisconnectListener.cs
  Consumers/EventConsumer.cs
  Worker/ EmailSender.cs, DailyPurge.cs
src/Notification/Bloom.Notification.Tests/
  Infrastructure/ Containers.cs, TestNotification.cs, FakeResend.cs, Consume.cs, Seed.cs, Sockets.cs, Mailpit.cs, MetricCapture.cs, LogCapture.cs, Poll.cs
  ShellTests, SchemaTests, TemplateTests, EmailProviderTests, ApiTests, HubTests, ConsumerTests, SenderTests, PurgeTests, WorkerTests
```

---

### Task 1: App shell, config, errors, caller, metrics, test infrastructure

**Files:**
- Create: `Bloom.Notification.csproj` (`InternalsVisibleTo Bloom.Notification.Tests`; `Protobuf Include="Protos\*.proto" GrpcServices="Server"`; `EmbeddedResource Include="Templates\*.sbn" LogicalName="%(Filename)%(Extension)"`), `Program.cs`, `NotificationApp.cs`, `NotificationOptions.cs`, `appsettings.json`, `Errors/NotificationErrors.cs`, `Context/Caller.cs`, `Observability/NotificationMetrics.cs`, `Data/NotificationDbContext.cs` (reliability tables only), `Data/DesignTimeFactory.cs`
- Create: `Bloom.Notification.Tests.csproj` (as payment's, plus `Microsoft.AspNetCore.SignalR.Client`, `Testcontainers.Redis`), `Infrastructure/{Containers, TestNotification, MetricCapture, LogCapture, Poll}.cs` (copy payment's where they exist)
- Modify: `Bloom.slnx`, `Directory.Packages.props`
- Test: `ShellTests.cs`

**Interfaces:**
- Produces:
  - `NotificationApp.Build(string[] args, Action<WebApplicationBuilder>? configure = null)`, same shape as `PaymentApp.Build` (N3–N5). API mode: `AddGrpc().AddJsonTranscoding(...)` as payment, `MapBloomDeadLetterAdmin()`. Worker mode: `bloom.AddWorker()`.
  - `NotificationOptions` (N4) with nested `EmailOptions { Provider, From, ResendOptions Resend { ApiKey, BaseUrl }, SmtpOptions Smtp { Host, Port, User, Password } }`, `string[] AllowedOrigins`, `bool IsApi`, `byte[] CodeKey` (set by `Validate()`), `void Validate()`.
  - `NotificationErrors`: `const string ValidationFailed = "VALIDATION_FAILED"`, `RpcException Validation(string field, string message)` (as `PaymentErrors.Validation`). Reasons: `VALIDATION_FAILED` → InvalidArgument; `ROLE_NOT_ALLOWED` (shared) → PermissionDenied.
  - `Caller` (N8).
  - `NotificationMetrics` (`MeterName = "Bloom.Notification"`, §10, N13): `Created(string type, string channel)`, `Pushed(string type)`, `Rejected(string reason)`, `ConnectionOpened()`, `ConnectionClosed()`, `EmailSend(double seconds)` (`email_send_seconds`, unit `s`), `EventToEmail(string type, double seconds)`, `SetEmailJobs(IReadOnlyDictionary<string, long> byStatus)`.
  - Tests: `Containers` (assembly fixture): PostgreSQL (`postgres:17.6`, `max_connections=1000`), RabbitMQ (`rabbitmq:4.1.4-management`), Redis (`redis:7.4`), Mailpit (`axllent/mailpit` with a pinned `v1.x` tag, ports 1025/8025, waits for HTTP 8025). Exposes `RabbitMqUri`, `RedisConnectionString`, `SmtpHost`, `SmtpPort`, `MailpitApi` (`Uri`), `CreateDatabaseAsync()`, `RabbitCtlAsync(...)`.
  - `TestNotification.StartAsync(Containers c, Action<TestNotificationOptions>? configure = null)` (options `Mode`, `ConnectionString`, `Config`) exposes `Http`, `Clock`, `Logs`, `Services`, `Server` (`TestServer`), `ConnectionString`, `NewDb()`, `SqlAsync(FormattableString)`, `ErrorLogs`. Default config: every N4 key; `StorefrontBaseUrl = https://shop.test`, `AllowedOrigins = ["https://shop.test"]`, `Email:Provider = resend`, `Email:From = Bloom <no-reply@bloom.test>`, `Email:Resend:BaseUrl = https://resend.test/`, `Email:Smtp:Host/Port` = Mailpit's, `CodeEncryptionKey` = `TestNotification.CodeKey` (a fixed 32-byte test key, base64).

- [ ] **Step 1: Create the projects**, add the packages and add both projects to `Bloom.slnx`.
- [ ] **Step 2: Write the failing tests** in `ShellTests.cs`

```csharp
[Fact] public void Unknown_mode_fails_at_build()
[Theory, MemberData(nameof(RequiredKeys))] public void Missing_config_fails_at_build_naming_the_key(string mode, string key)
// every always-required N4 key, both modes; Resend keys with provider resend; Smtp Host/Port with provider smtp
[Theory, MemberData(nameof(InvalidValues))] public void Invalid_config_fails_at_build_naming_the_key(string key, string value)
// relative StorefrontBaseUrl / Resend BaseUrl; CodeEncryptionKey of 31 bytes and not base64; Port 0 and 70000; Provider "ses"; AllowedOrigins[0] "shop.test"
[Fact] public void Smtp_needs_no_resend_keys()                  // provider smtp without Resend keys builds
[Fact] public void Each_reason_has_its_status()                 // VALIDATION_FAILED InvalidArgument, ROLE_NOT_ALLOWED PermissionDenied; domain "bloom"
[Fact] public void Role_checks()                                // customer passes; staff (any role) → ROLE_NOT_ALLOWED
[Fact] public async Task Api_host_starts_and_hosts_the_dlq_admin()   // GET /api/admin/dlq/notification/queues (staff admin) → 200
```

- [ ] **Step 3: Run** `--filter-class '*ShellTests'`. Expected: build fails (types missing).
- [ ] **Step 4: Implement.**
- [ ] **Step 5: Run**: PASS. `dotnet build -c Release`: 0 warnings.
- [ ] **Step 6: Commit**: `git commit -m "feat(notification): app shell, config, errors and test infrastructure"`

---

### Task 2: Data model and initial migration

**Files:**
- Create: `Data/Entities.cs`, `Data/AfterCommitInterceptor.cs`, `Data/Migrations/*` (`dotnet ef migrations add Initial --project src/Notification/Bloom.Notification`)
- Modify: `Data/NotificationDbContext.cs` (+ `AfterCommit`), `NotificationApp.cs` (interceptor singleton, `AddInterceptors`)
- Test: `SchemaTests.cs`

**Interfaces:**
- Produces (snake_case columns of §4.1 exactly; `timestamptz` ↔ `DateTimeOffset`):
  - `ContactRow` (`contacts`, PK `account_id`): `Email`, `Name`, `UpdatedAt`.
  - `OrderRefRow` (`order_refs`, PK `order_id`): `AccountId`, `OrderNumber`, `UpdatedAt`.
  - `NotificationRow` (`notifications`): every §4.1 column. `ux_notifications_source_message_id_type` unique `(source_message_id, type)`; `ix_notifications_account_id_created_at` on `(account_id, created_at DESC)`; `ix_notifications_account_id_unread` on `(account_id) WHERE read_at IS NULL`.
  - `EmailJobRow` (`email_jobs`): every §4.1 column; `Data` is `string` with column type `jsonb` (N16); `AccountId` nullable. `ux_email_jobs_source_message_id_template` unique `(source_message_id, template)`; `ix_email_jobs_status_next_attempt_at` (spec §5). Checks: status in `pending`, `sent`, `failed`, `cancelled`; `attempts >= 0`. Constants class `EmailStatuses`.
  - `AddBloomReliabilityTables()`.
  - `NotificationDbContext.AfterCommit` and `AfterCommitInterceptor` (N7).

- [ ] **Step 1: Write the failing tests** in `SchemaTests.cs`

```csharp
[Fact] public async Task Migration_applies_and_model_has_no_pending_changes()
[Fact] public async Task One_notification_per_event_and_type()        // same (source_message_id, type) → 23505; other type → fine
[Fact] public async Task One_email_job_per_event_and_template()       // same (source_message_id, template) → 23505
[Fact] public async Task Email_status_rejects_unknown_values()        // 'x' → 23514
[Fact] public async Task Indexes_match_the_design()                   // pg_indexes: the five names above; the unread one has WHERE (read_at IS NULL); created_at DESC
[Fact] public async Task After_commit_actions_run_only_on_commit()
// app-resolved context: AfterCommit action + commit → ran once; another action + rollback → not run, list empty
```

- [ ] **Step 2: Run** `--filter-class '*SchemaTests'`: FAIL.
- [ ] **Step 3: Implement** the entities, the interceptor, the configuration and the migration.
- [ ] **Step 4: Run**: PASS.
- [ ] **Step 5: Commit**: `git commit -m "feat(notification): data model, migration and after-commit hook"`

---

### Task 3: Templates and rendering

**Files:**
- Create: `Templates/TemplateRenderer.cs`, `Templates/Money.cs`, `Templates/NotificationTypes.cs`, `Templates/<type>.sbn` × 13
- Modify: `NotificationApp.cs` (`TemplateRenderer` singleton)
- Test: `TemplateTests.cs` (no containers)

**Interfaces:**
- Produces:
  - `NotificationTypes`: a `const string` per type: `account.code`, `staff.invitation`, `order.confirmed`, `order.cancelled`, `order.preorder-delayed`, `order.shipped`, `order.delivered`, `order.delivery-failed`, `return.label-issued`, `return.received`, `return.decided`, `return.expired`, `refund.issued`; `IReadOnlySet<string> All`, `IReadOnlySet<string> InApp` (all but the first two, NR-02).
  - `TemplateRenderer(NotificationOptions options)`: `(string Subject, string Html, string Text) RenderEmail(string type, IReadOnlyDictionary<string, string> vars)` and `(string Title, string Body) RenderInApp(string type, IReadOnlyDictionary<string, string> vars)` (throws for a type not in `InApp`). `RenderEmail` adds `orderUrl` = `<StorefrontBaseUrl>/orders/<orderNumber>` (no doubled slash) when `orderNumber` is present. N15.
  - Static `TemplateRenderer.InAppLink(string orderNumber) → "/orders/<orderNumber>"`.
  - `Money.Format(long cents) → string` (N16).
  - Variables per type (Task 7 fills them): `account.code` `purpose`, `code`; `staff.invitation` `role`, `inviteUrl`; order, shipping, return and refund types `orderNumber`, `name`, plus `reason` (`order.cancelled`), `trackingNumber` (`order.shipped`), `kind` (`order.delivery-failed`), `labelUrl` (`return.label-issued`), `refundAmount` (`return.decided`, `refund.issued`).
  - Copy (spec §4, §7): short plain US English. `order.cancelled` text per `reason` (`customer`, `support`, `checkout_expired`, `late_payment`, `checkout_failed`, else generic); `order.delivery-failed` per `kind` (`returned_to_sender`, `lost`); `account.code` per `purpose` (`signup`, `reset_password`, `change_email`, else generic "your Bloom code"). Order emails link `orderUrl`; the invitation links `inviteUrl`; the code email has no link. No `<img>`.

- [ ] **Step 1: Write the failing tests** in `TemplateTests.cs`

```csharp
[Theory, MemberData(nameof(AllTypes))] public void Every_type_renders_every_part(string type)
// sample vars; subject, html, text non-empty; in-app types: title, body non-empty; account.code and staff.invitation: RenderInApp throws
[Fact] public void Markup_is_escaped_in_html_only()              // Review Focus 3: name `<b>Al & "Bo"</b>`, orderNumber and trackingNumber with <script> → html has &lt;b&gt;Al &amp; &quot;Bo&quot;&lt;/b&gt; and no "<script"; text, subject, title, body raw
[Fact] public void Emails_have_no_images()                         // no "<img" in any html
[Fact] public void Order_emails_link_the_order()                   // html and text contain https://shop.test/orders/BLM-7K2M9QXA; InAppLink → /orders/BLM-7K2M9QXA
[Fact] public void Invitation_links_the_invite_and_code_email_has_no_order_link()
[Fact] public void Cancelled_text_differs_per_reason()             // the five reasons → five different texts, none equal to the generic one; "other" → generic
[Fact] public void Delivery_failed_text_differs_per_kind()
[Fact] public void Code_text_differs_per_purpose_and_shows_the_code()   // three purposes + generic; code "482913" in html and text
[Fact] public void Missing_variable_throws()                       // order.confirmed without orderNumber → exception
[Theory, InlineData(123456, "$1,234.56"), InlineData(5, "$0.05"), InlineData(0, "$0.00")] public void Money_formats_us_dollars(long cents, string text)
```

- [ ] **Step 2: Run** `--filter-class '*TemplateTests'`: FAIL.
- [ ] **Step 3: Implement** the renderer and write the 13 templates.
- [ ] **Step 4: Run**: PASS.
- [ ] **Step 5: Commit**: `git commit -m "feat(notification): Scriban templates and renderer"`

---

### Task 4: Email providers, code decryption and fake Resend

**Files:**
- Create: `Email/IEmailProvider.cs`, `Email/ResendEmailProvider.cs`, `Email/SmtpEmailProvider.cs`, `Email/CodeCipher.cs`, Tests `Infrastructure/FakeResend.cs`, `Infrastructure/Mailpit.cs`
- Modify: `NotificationApp.cs` (`AddHttpClient<ResendEmailProvider>` with base URL, bearer key, 10 s; `AddTransient<IEmailProvider>` choosing by `Email:Provider`), `TestNotification.cs` (exposes `Resend`; `ConfigurePrimaryHttpMessageHandler(() => fake)`)
- Test: `EmailProviderTests.cs`

**Interfaces:**
- Produces:
  - `IEmailProvider` (HLD-ADR-018): `Task<string?> SendAsync(EmailMessage message, string idempotencyKey, CancellationToken ct)` → the provider message ID (null for SMTP).
  - `sealed record EmailMessage(string To, string Subject, string Html, string Text)`; the provider adds `Email:From`.
  - `sealed class EmailSendException(bool transient, string reason, string message, Exception? inner = null)` with `Transient`, `Reason`.
  - `ResendEmailProvider` (spec §7): `POST emails`, header `Idempotency-Key`, bearer `Email:Resend:ApiKey`, JSON `{from, to: [to], subject, html, text}`, reads `id`. 5xx, 429 → transient, reason `http_<status>`; timeout → transient `timeout`; network error → transient `network`; other 4xx → permanent, reason = body `name` ?? `http_<status>`.
  - `SmtpEmailProvider` (spec §7): `System.Net.Mail.SmtpClient` per send, `MailMessage` with a text body and an HTML alternate view; credentials only when `User` is set. `static bool IsPermanent(SmtpStatusCode code)`: true for 550, 551, 552, 553; any other `SmtpException` → transient, reason `smtp_<code>`; permanent reason `smtp_<code>`.
  - `CodeCipher.Decrypt(byte[] key, string encryptedCode) → string`: base64url `nonce(12) ‖ ciphertext ‖ tag(16)`, AES-256-GCM, no associated data (identity's `SecretBox`); throws `CryptographicException` or `FormatException`. Tests seed codes with `TestNotification.EncryptCode(string code) → string` (same format, `TestNotification.CodeKey`).
  - `FakeResend(FakeTimeProvider clock)` (N10, spec §8): serves only `POST emails`; anything else → `404` and an entry in `Unexpected`. Returns `{"id":"re_<n>"}`; the same idempotency key again returns the first ID and is not counted again in `Delivered`. Scripting: `Requests` (path, `Idempotency-Key`, `Authorization`, parsed JSON body), `Delivered` (unique keys), `Fail(int status, string? name = null, int times = 1)`, `HangNext()` (waits for the caller's cancellation), `Func<Task>? Before`.
  - `Mailpit.FindAsync(Containers c, string to) → Task<MailpitMessage?>` (subject, text, html) over Mailpit's HTTP API.

- [ ] **Step 1: Write the failing tests** in `EmailProviderTests.cs`

```csharp
[Fact] public async Task Resend_sends_the_documented_request()
// POST emails; Bearer key; Idempotency-Key bloom:email:<id>; body from (Email:From), to [address], subject, html, text; returns re_1
[Theory, InlineData(500), InlineData(503), InlineData(429)] public async Task Resend_server_errors_and_429_are_transient(int status)
[Fact] public async Task Resend_4xx_is_permanent_with_its_name()   // 422 validation_error → Transient false, Reason "validation_error"; 400 without name → "http_400"
[Fact] public async Task Resend_times_out_after_10_seconds()        // HangNext → transient "timeout" (real time, < 13 s)
[Fact] public async Task Smtp_sends_through_mailpit()              // provider smtp; Mailpit has the message: subject, text part, html part; returns null
[Theory, InlineData(550, true), InlineData(553, true), InlineData(554, false), InlineData(421, false), InlineData(451, false)]
public void Smtp_mailbox_errors_are_permanent(int code, bool permanent)
[Fact] public void Code_decrypts_identity_format()                  // Encrypt then Decrypt round trip; wrong key, one flipped byte, 20-byte input, "%%" → throws
```

- [ ] **Step 2: Run** `--filter-class '*EmailProviderTests'`: FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run**: PASS.
- [ ] **Step 5: Commit**: `git commit -m "feat(notification): Resend and SMTP providers, code decryption"`

---

### Task 5: Customer API and WebSocket ticket

**Files:**
- Create: `Protos/notification.proto`, `Services/CustomerService.cs`, `Sockets/Tickets.cs`, Tests `Infrastructure/Seed.cs`
- Modify: `NotificationApp.cs` (`IConnectionMultiplexer` singleton from `Redis`; `Tickets` singleton; `MapGrpcService<CustomerService>()` in API mode)
- Test: `ApiTests.cs`

**Interfaces:**
- Produces:
  - `notification.proto` (package `bloom.notification.v1`, `csharp_namespace = "Bloom.Notification.Protos"`), spec §6:
    ```proto
    service Notifications {
      rpc ListNotifications(ListNotificationsRequest) returns (ListNotificationsResponse) { option (google.api.http) = { get: "/api/notifications" }; }
      rpc GetUnreadCount(GetUnreadCountRequest) returns (GetUnreadCountResponse) { option (google.api.http) = { get: "/api/notifications/unread-count" }; }
      rpc MarkRead(MarkReadRequest) returns (google.protobuf.Empty) { option (google.api.http) = { post: "/api/notifications/read" body: "*" }; }
      rpc MarkAllRead(MarkAllReadRequest) returns (google.protobuf.Empty) { option (google.api.http) = { post: "/api/notifications/read-all" body: "*" }; }
      rpc GetWsTicket(GetWsTicketRequest) returns (GetWsTicketResponse) { option (google.api.http) = { get: "/api/notifications/ws-ticket" }; }
    }
    message ListNotificationsRequest { string page_token = 1; }
    message ListNotificationsResponse { repeated NotificationItem notifications = 1; string next_page_token = 2; }
    message NotificationItem { string id = 1; string type = 2; string title = 3; string body = 4; string link = 5; bool read = 6; google.protobuf.Timestamp created_at = 7; }
    message GetUnreadCountRequest {}  message GetUnreadCountResponse { int32 count = 1; }
    message MarkReadRequest { repeated string ids = 1; }  message MarkAllReadRequest {}
    message GetWsTicketRequest {}  message GetWsTicketResponse { string ticket = 1; }
    ```
  - `CustomerService`: every method `Caller.From(ctx).RequireCustomer()` first (N8), then N9, and filters on the caller's `account_id`.
    - `ListNotifications`: `WHERE account_id = @a AND (created_at, id) < (@c, @i) ORDER BY created_at DESC, id DESC LIMIT 21`; 20 returned; `next_page_token` = base64url(`created_at` as int64 microseconds since Unix epoch, big-endian ‖ `id` 16 bytes) of the 20th row when a 21st exists, else `""`. A token that isn't 24 bytes of base64url → `VALIDATION_FAILED` `page_token`.
    - `GetUnreadCount`: count with `read_at IS NULL`.
    - `MarkRead`: `UPDATE … SET read_at = @now WHERE account_id = @a AND id = ANY(@ids) AND read_at IS NULL`.
    - `MarkAllRead`: same without the ID filter.
    - `GetWsTicket`: `Tickets.IssueAsync`.
  - `Tickets(IConnectionMultiplexer redis)`: `Task<string> IssueAsync(Guid accountId)` (32 random bytes, base64url, `SET ws:ticket:{t} <accountId D> EX 30`), `Task<Guid?> RedeemAsync(string ticket)` (`GETDEL`).
  - `Seed.NotificationAsync(TestNotification t, Guid accountId, DateTimeOffset createdAt, string type = "order.confirmed", DateTimeOffset? readAt = null) → Task<Guid>`, `TestNotification.CustomerClient(Guid accountId) → HttpClient` and `StaffClient(string role)` (default `x-bloom-*` headers).

- [ ] **Step 1: Write the failing tests** in `ApiTests.cs` (transcoded HTTP)

```csharp
[Fact] public async Task List_is_newest_first_20_per_page()
// 45 rows (5 share one created_at) → pages 20, 20, 5; order created_at desc then id desc; no row twice or missing; last nextPageToken ""
[Fact] public async Task List_returns_the_documented_fields()        // JSON keys id, type, title, body, link, read, createdAt; nextPageToken; read true for a read row
[Fact] public async Task List_shows_only_the_callers_rows()          // Review Focus 4: B's rows never appear, also with a token built from B's last row
[Fact] public async Task Malformed_page_token_is_VALIDATION_FAILED() // "x!", base64url of 3 bytes → 400, reason VALIDATION_FAILED, field page_token
[Fact] public async Task Unread_count_counts_unread_rows_of_the_caller()
[Fact] public async Task Mark_read_marks_only_the_callers_unread_rows()
// own unread → read_at = clock; own already read keeps its read_at; B's id and a random id → ignored, B's row unread; 200 {}
[Theory, MemberData(nameof(BadIds))] public async Task Mark_read_validates_ids(string json)   // [], 101 ids, ["x"] → 400 VALIDATION_FAILED field ids; nothing changed
[Fact] public async Task Mark_all_read_marks_every_unread_row_of_the_caller()   // B untouched
[Fact] public async Task Ws_ticket_is_stored_for_30_seconds()        // 43-char base64url; Redis ws:ticket:<t> = account id; TTL in (0, 30]; two calls → two tickets
[Fact] public async Task Staff_callers_are_ROLE_NOT_ALLOWED()        // every method, staff support → 403 ROLE_NOT_ALLOWED
[Fact] public async Task Missing_caller_headers_are_a_server_error() // 500
```

- [ ] **Step 2: Run** `--filter-class '*ApiTests'`: FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run**: PASS.
- [ ] **Step 5: Commit**: `git commit -m "feat(notification): customer API and WebSocket ticket"`

---

### Task 6: SignalR hub, gate, lifetime and disconnect

**Files:**
- Create: `Sockets/NotificationHub.cs`, `Sockets/HubGate.cs`, `Sockets/ConnectionRegistry.cs`, `Sockets/Disconnects.cs`, `Sockets/DisconnectListener.cs`, Tests `Infrastructure/Sockets.cs`
- Modify: `NotificationApp.cs`
- Test: `HubTests.cs`

**Interfaces:**
- Consumes: `Tickets.RedeemAsync` (Task 5); `NotificationMetrics` (Task 1).
- Produces (spec §7 Sockets, §6.3, owner item 1):
  - Both modes: `AddSignalR(o => { o.KeepAliveInterval = 15 s; o.ClientTimeoutInterval = 30 s; }).AddStackExchangeRedis(Redis, o => o.Configuration.ChannelPrefix = RedisChannel.Literal("bloom-notification"))`.
  - API mode: `ConnectionRegistry` singleton, `DisconnectListener` hosted, `app.UseMiddleware<HubGate>()`, `MapHub<NotificationHub>("/api/notifications/hub", o => o.Transports = HttpTransportType.WebSockets)`.
  - `NotificationHub.Path = "/api/notifications/hub"`, `NotificationHub.Group(Guid accountId) → "user:{accountId}"`. `OnConnectedAsync`: account from `HttpContext.Items["accountId"]`, join the group, `registry.Add`. `OnDisconnectedAsync`: `registry.Remove`.
  - `PushMessage(Guid Id, string Type, string Title, string Link, DateTimeOffset CreatedAt)`; `NotificationHub.ClientMethod = "notification"`.
  - `HubGate` (only on the exact path `NotificationHub.Path`): not a WebSocket request → `400`. `Origin` not in `AllowedOrigins` (ordinal, case-insensitive) → `403`, `Rejected("origin")`. `Tickets.RedeemAsync(query ticket)` null → `401`, `Rejected("ticket")`. Else `Items["accountId"]` and continue.
  - `ConnectionRegistry(TimeProvider clock, NotificationMetrics metrics)`: `Add(Guid accountId, HubCallerContext ctx)` (starts a `clock.CreateTimer` that calls `ctx.Abort()` 15 min later; `ConnectionOpened()`), `Remove(HubCallerContext ctx)` (disposes the timer; `ConnectionClosed()`), `int AbortAll(Guid accountId)`.
  - `Disconnects.Channel = "notification:disconnect"`; `Disconnects.PublishAsync(IConnectionMultiplexer redis, Guid accountId)` (message = account ID `D`).
  - `DisconnectListener` (API): subscribes to the channel; each message → `registry.AbortAll`.
  - Tests: `Sockets.ConnectAsync(TestNotification t, string ticket, string origin = "https://shop.test") → Task<HubConnection>` (WebSockets, `SkipNegotiation`, `WebSocketFactory` = `t.Server.CreateWebSocketClient()` with `ConfigureRequest` setting `Origin`), `Sockets.TicketAsync(TestNotification t, Guid accountId)`, `Sockets.Inbox(HubConnection c) → Channel<PushMessage>`, `Sockets.ClosedAsync(HubConnection c, TimeSpan within)`.

- [ ] **Step 1: Write the failing tests** in `HubTests.cs` (API hosts only)

```csharp
[Fact] public async Task Ticket_connects_and_joins_the_users_group()
// connect; IHubContext group user:{id} SendAsync → client gets notification {id, type, title, link, createdAt}; ws_connections 1, after StopAsync 0
[Fact] public async Task N04_reused_ticket_is_rejected()            // second connect with the same ticket → 401; ws_rejected_total{ticket} 1
[Fact] public async Task Unknown_or_missing_ticket_is_rejected()    // 401 each
[Fact] public async Task One_ticket_connects_once_under_concurrency()   // Review Focus 5: 10 parallel connects → 1 connected, 9 × 401, {ticket} 9
[Fact] public async Task Disallowed_origin_is_rejected_and_keeps_the_ticket()
// Origin https://evil.test → 403, {origin} 1; no Origin → 403; the same ticket with https://shop.test then connects
[Fact] public async Task Non_websocket_request_is_400_and_keeps_the_ticket()   // plain GET /api/notifications/hub?ticket=t → 400; ticket still in Redis
[Fact] public async Task N05_connection_is_closed_after_15_minutes()
// Clock +14 min 59 s → still Connected; +2 s → closed within 2 s; a new ticket connects (the client's reconnect)
[Fact] public async Task Every_tab_gets_the_push()                  // two connections, one account → both receive
[Fact] public async Task Disconnect_message_closes_only_that_users_sockets()
// A: two sockets, B: one; Disconnects.PublishAsync(A) → A's closed within 2 s; B still Connected
```

- [ ] **Step 2: Run** `--filter-class '*HubTests'`: FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run**: PASS.
- [ ] **Step 5: Commit**: `git commit -m "feat(notification): SignalR hub with ticket gate, lifetime and disconnect"`

---

### Task 7: Event consumers

**Files:**
- Create: `Consumers/EventConsumer.cs`, Tests `Infrastructure/Consume.cs`
- Modify: `NotificationApp.cs` (register every handler, N6)
- Test: `ConsumerTests.cs`

**Interfaces:**
- Consumes: `TemplateRenderer`, `NotificationTypes`, `Money` (Task 3); `AfterCommit` (Task 2); `NotificationHub.Group`, `PushMessage`, `Disconnects.PublishAsync` (Task 6); `CodeCipher` only in tests.
- Produces:
  - `EventConsumer.Handlers : IReadOnlyList<(string Handler, ConsumerBinding Binding)>` and `HandleAsync`, switching on `envelope.MessageType`. `GetPayload<T>` first. Producer of each binding = the message prefix.

    | Handler | Message | Effect |
    |---|---|---|
    | `identity-verification-code` | `VerificationCodeV1` | `account.code` email to `Email`; data `purpose`, `encryptedCode` (as received), `expiresAt` (ISO-8601 `O`) |
    | `identity-staff-invited` | `StaffInvitedV1` | `staff.invitation` email to `Email`; data `role`, `inviteUrl` |
    | `identity-customer-registered`, `identity-contact-changed` | `CustomerRegisteredV1`, `ContactChangedV1` | Upsert `contacts` (N12) |
    | `identity-account-deleted` | `AccountDeletedV1` | §6.5: delete the account's `contacts`, `notifications`, `order_refs`; `pending` jobs of the account → `cancelled`; after commit `Disconnects.PublishAsync` |
    | `identity-tokens-revoked` | `TokensRevokedV1` | After commit `Disconnects.PublishAsync` (no write) |
    | `order-paid` | `OrderPaidV1` | Insert `order_refs`, then `order.confirmed` |
    | `order-cancelled` | `OrderCancelledV1` | Insert `order_refs`, then `order.cancelled`; `reason` |
    | `order-preorder-delayed` | `OrderPreorderDelayedV1` | `order.preorder-delayed` (recipient `AccountId`) |
    | `fulfillment-shipment-shipped` | `ShipmentShippedV1` | `order.shipped`; `trackingNumber` |
    | `fulfillment-shipment-delivered` | `ShipmentDeliveredV1` | `order.delivered` |
    | `fulfillment-delivery-failed` | `DeliveryFailedV1` | `order.delivery-failed`; `kind` |
    | `fulfillment-return-label-issued` | `ReturnLabelIssuedV1` | `return.label-issued`; `labelUrl` |
    | `fulfillment-return-received` | `ReturnReceivedV1` | `return.received` |
    | `fulfillment-return-decided` | `ReturnDecidedV1` | `return.decided`; `refundAmount` = `Money.Format(RefundCents)` |
    | `fulfillment-return-closed` | `ReturnClosedV1` | `reason = expired` → `return.expired`; else nothing |
    | `payment-refund-completed` | `RefundCompletedV1` | `refund.issued`; `refundAmount` = `Money.Format(AmountCents)` |

    Fulfillment and payment rows resolve the account and order number through `order_refs[OrderId]`. Order rows use the payload's `AccountId` and `OrderNumber`.
  - The notifying path (§6.1, spec §7): missing `order_refs` or `contacts` row → throw `InvalidOperationException` (transient). Data = the row's variables + `orderNumber` + `name` (from `contacts`). In-app: `RenderInApp`, `INSERT INTO notifications … ON CONFLICT (source_message_id, type) DO NOTHING` with `source_message_id` = `envelope.MessageId`, `link` = `InAppLink(orderNumber)`; if 1 row → after commit push `PushMessage` to `Group(accountId)`, `Pushed(type)`, `Created(type, "in_app")`. Email: `INSERT INTO email_jobs (…, to_email = contact email, template = type, status = pending, attempts = 0, next_attempt_at = now, …) ON CONFLICT (source_message_id, template) DO NOTHING`; if 1 row → after commit `Created(type, "email")`. Email-only types: `account_id` null.
  - `Consume.Async(TestNotification t, string messageType, object payload, Guid? messageId = null, bool rollback = false)`: as payment's, but rolls back instead of committing when `rollback`.
  - `Seed.ContactAsync(t, accountId, email, name)`, `Seed.OrderRefAsync(t, orderId, accountId, orderNumber)`.

- [ ] **Step 1: Write the failing tests** in `ConsumerTests.cs` (API host, `Consume.Async`)

```csharp
[Fact] public async Task N01_order_paid_twice_makes_one_row_one_job_one_push()
// same message id consumed twice (inbox bypassed) → 1 notifications row, 1 email_jobs row, the open socket gets exactly 1 push; ws_pushes_total{order.confirmed} 1; created_total {order.confirmed,in_app} 1, {…,email} 1
[Theory, MemberData(nameof(NotifyingMessages))] public async Task Each_message_maps_to_its_type(string messageType, string type, string[] dataKeys)
// 13 rows: in-app row type, title, link /orders/<number> (in-app types only); job template = type, to_email, data keys exactly dataKeys + orderNumber, name where they apply; email-only: no row, account_id null
[Fact] public async Task N02_event_before_its_order_ref_is_transient()
// shipment-shipped with no order_refs → non-Permanent exception, nothing written; consume order.paid; the same shipped message → processed
[Fact] public async Task Missing_contact_is_transient()
[Fact] public async Task Code_job_keeps_the_code_encrypted()       // data = {purpose, encryptedCode as sent, expiresAt}; no plaintext code anywhere in the row
[Fact] public async Task Return_closed_notifies_only_when_expired() // decided → no row, no job; expired → return.expired
[Fact] public async Task Late_payment_refund_resolves_through_the_cancelled_ref()   // §8: cancelled (late_payment) then refund-completed → refund.issued with refundAmount
[Fact] public async Task Contact_change_keeps_the_address_of_queued_jobs()          // §8: job queued; contact-changed → old job to_email unchanged; next event uses the new address
[Fact] public async Task Older_contact_event_does_not_overwrite_a_newer_one()      // N12: changed (OccurredAt t2) then registered (t1 < t2) → email of t2
[Fact] public async Task Push_happens_only_after_commit()          // Review Focus 2: rollback → no push in 1 s, no row; commit → 1 push
[Fact] public async Task Offline_customer_sees_it_on_next_load()   // §8: no socket; ListNotifications returns the row unread
[Fact] public async Task N10_account_deleted_removes_data_and_cancels_pending_jobs()
// contact, notifications, order_refs of A gone; A's pending jobs cancelled, A's sent job unchanged; B untouched; A's socket closed within 2 s
[Fact] public async Task Tokens_revoked_closes_the_users_sockets() // no database change
```

- [ ] **Step 2: Run** `--filter-class '*ConsumerTests'`: FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run**: PASS.
- [ ] **Step 5: Commit**: `git commit -m "feat(notification): event consumers"`

---

### Task 8: Email sender

**Files:**
- Create: `Worker/EmailSender.cs`
- Modify: `NotificationApp.cs` (`EmailSender` hosted in the worker)
- Test: `SenderTests.cs`

**Interfaces:**
- Consumes: `IEmailProvider`, `EmailSendException`, `CodeCipher` (Task 4); `TemplateRenderer` (Task 3); entities (Task 2).
- Produces (§6.2, spec §7, N13, N17):
  - `EmailSender` (`PeriodicTimer(2 s, TimeProvider)`), `internal Task<int> RunOnceAsync(CancellationToken ct)` → jobs handled.
    1. Claim, one statement in its own transaction: `UPDATE email_jobs SET next_attempt_at = @now + 1 min WHERE id IN (SELECT id FROM email_jobs WHERE status = 'pending' AND next_attempt_at <= @now ORDER BY next_attempt_at LIMIT 50 FOR UPDATE SKIP LOCKED) RETURNING …`.
    2. Per job: data → vars; `account.code` → `code = CodeCipher.Decrypt(options.CodeKey, encryptedCode)` (failure → `failed`, `decrypt_failed`); `RenderEmail` (failure → `failed`, `render_failed`); `SendAsync(new EmailMessage(to_email, …), $"bloom:email:{id}")`, timed into `EmailSend`.
    3. Outcome, one guarded statement per job (`WHERE id = @id AND status = 'pending'`): success → `sent`, `sent_at`, `provider_message_id`, `attempts + 1`, and for `account.code` `data = data - 'encryptedCode'` in the same statement; `EventToEmail(template, sent_at − created_at)`. Transient with `attempts + 1 < 5` → `attempts + 1`, `next_attempt_at = now + 1 s / 5 s / 30 s / 2 min` (by the new count), `last_error`. Transient 5th or permanent → `failed`, `attempts + 1`, `last_error`, error log (job ID, template, reason).
    4. Every 30 s (clock), `SetEmailJobs` from the status counts.

- [ ] **Step 1: Write the failing tests** in `SenderTests.cs` (API host; `RunOnceAsync` via `ActivatorUtilities`)

```csharp
[Fact] public async Task Pending_job_is_rendered_and_sent()
// order.confirmed job → Resend request: Idempotency-Key bloom:email:<id>, subject, html with https://shop.test/orders/<n>, text; row sent, sent_at = clock, provider_message_id re_1, attempts 1; event_to_email_seconds{order.confirmed} and email_send_seconds recorded
[Fact] public async Task N03_code_email_is_sent_with_the_code_and_scrubbed()
// consume verification-code (encrypted with TestNotification.CodeKey) → send: html and text contain the code; data = {purpose, expiresAt}; no log entry and no column contains the code
[Fact] public async Task Expired_code_is_still_sent()               // §6.2: expiresAt in the past → sent
[Fact] public async Task Undecryptable_code_fails_without_sending() // tampered → failed, last_error decrypt_failed, 0 Resend requests, error log without the ciphertext's plaintext
[Fact] public async Task N08_provider_500_five_times_fails_the_job()
// Fail(500, times 5): attempts 1..4, next_attempt_at +1 s, +5 s, +30 s, +2 min (advance the clock each time); 5th → failed, last_error http_500, one error log;
// §8 Ops re-queue (status pending, next_attempt_at = now) → next pass sent
[Fact] public async Task N09_invalid_address_fails_at_once()       // 422 validation_error → failed after 1 request, last_error validation_error, error log
[Fact] public async Task Not_due_and_finished_jobs_are_skipped()  // next_attempt_at future; sent; failed; cancelled → 0 requests
[Fact] public async Task Two_senders_send_each_job_once()          // Review Focus 1: 60 due jobs, two RunOnceAsync in parallel (Before delays 50 ms) → 60 requests, 60 distinct keys, all sent
[Fact] public async Task Claimed_job_waits_for_its_lease()        // next_attempt_at = now + 1 min (a crashed sender's lease) → skipped; +61 s → sent
[Fact] public async Task One_failure_does_not_block_the_others()  // first Fail 500, second sent in the same pass
[Fact] public async Task Job_cancelled_during_the_send_stays_cancelled()   // Before sets status cancelled → after the pass: cancelled, no error
[Fact] public async Task Smtp_job_is_delivered_to_mailpit()       // host with Email:Provider smtp → sent, provider_message_id null; Mailpit has it
[Fact] public async Task Email_jobs_gauge_counts_by_status()      // 2 pending, 1 sent, 1 failed → email_jobs_by_status {pending 2, sent 1, failed 1}
```

- [ ] **Step 2: Run** `--filter-class '*SenderTests'`: FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run**: PASS.
- [ ] **Step 5: Commit**: `git commit -m "feat(notification): email sender with retries and code scrubbing"`

---

### Task 9: Daily purge, worker mode, broker and cross-pod tests, full verification

**Files:**
- Create: `Worker/DailyPurge.cs`
- Modify: `NotificationApp.cs` (`DailyPurge` hosted in the worker)
- Test: `PurgeTests.cs`, `WorkerTests.cs`

**Interfaces:**
- Produces (§6.6, spec §7):
  - `DailyPurge.NextRun(DateTimeOffset now) → DateTimeOffset` (static: the next 03:00 UTC strictly after `now`); the loop waits for it on the app clock.
  - `DailyPurge.PurgeOnceAsync(CancellationToken ct) → Task`: `DELETE FROM notifications WHERE id IN (SELECT id … WHERE created_at < @now − 90 days LIMIT 1000)` and `DELETE FROM email_jobs WHERE id IN (SELECT id … WHERE status IN ('sent','failed','cancelled') AND created_at < @now − 30 days LIMIT 1000)`, each until fewer than 1,000 rows go. A failure is an error log; the loop continues next day.
  - Worker host: `AddWorker()` services, `EmailSender`, `DailyPurge`. API host: `DisconnectListener`, no sender, no purge.

- [ ] **Step 1: Write the failing tests**

`PurgeTests.cs`:
```csharp
[Fact] public async Task N11_notifications_older_than_90_days_are_purged()   // 2,500 rows at −91 days, 3 at −89 days → 3 left
[Fact] public async Task Finished_email_jobs_older_than_30_days_are_purged() // sent, failed, cancelled at −31 days → gone; pending at −31 days and sent at −29 days → kept
[Fact] public void Next_run_is_the_next_0300_utc()                          // 02:59 → same day 03:00; 03:00 → next day; 23:00 → next day
```

`WorkerTests.cs` (two API hosts + one worker host on one database, the run's Redis and broker):
```csharp
[Fact] public async Task Every_queue_is_bound()                    // 17 notification.<handler> queues on the right <producer>.events exchange with routing key = type, with retry and .dlq queues
[Fact] public async Task N01_order_paid_published_twice_makes_one_row()   // same envelope twice through order.events → 1 row, 1 job
[Fact] public async Task N02_shipped_before_order_ref_is_retried_then_processed()
// publish shipment-shipped; after 1.5 s no row; publish order.paid → order.shipped row within 15 s
[Fact] public async Task Every_notifying_event_flows_through_the_broker()   // spec §8: one envelope per §5.3 message → its row / job within 15 s
[Fact] public async Task N07_push_from_the_worker_reaches_either_host()
// customer A on host 1, customer B on host 2; order.paid for each through the broker → each gets its push within 5 s
[Fact] public async Task N06_tokens_revoked_closes_sockets_on_every_pod()
// A on host 1 and host 2, B on host 1; tokens-revoked for A → both A sockets closed within 2 s of consumption; B connected
[Fact] public async Task Email_is_sent_by_the_worker()             // order.paid through the broker; Clock.Advance(2 s) until the job is sent (≤ 15 s)
[Fact] public async Task Dlq_admin_is_hosted()                     // GET /api/admin/dlq/notification/queues (admin) → 17 .dlq queues
[Fact] public async Task Worker_runs_sender_and_purge()            // hosted services: worker has EmailSender, DailyPurge; API has DisconnectListener and neither of the others
```

- [ ] **Step 2: Run** `--filter-class '*PurgeTests'` and `'*WorkerTests'`: FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run**: PASS. Then the whole suite: `dotnet build -c Release` (0 warnings) and `dotnet test` (all projects): PASS. ID coverage: `grep -rhoE 'N(0[1-9]|1[01])_' src/Notification/Bloom.Notification.Tests | sort -u` lists N01_ … N11_. No-internet check: `grep -rn 'api.resend.com' src/Notification/Bloom.Notification.Tests` is empty. `docker ps -a` shows no leftover test containers.
- [ ] **Step 5: Commit**: `git commit -m "feat(notification): daily purge, worker mode and cross-pod delivery"`

---

## Proposed doc edits (not applied; in addition to spec §9)

5. 09-notification §5.1 hub row: rejection before the upgrade, `403` for Origin, `401` for the ticket, `400` for a non-WebSocket request (owner item 1).
