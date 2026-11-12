# Notification service: design

| | |
|---|---|
| **Status** | Approved (2026-11-08) |
| **Date** | 2026-11-08 |
| **Branch / PR base** | `feat/notification` → `feat/payment` |
| **Implements** | [09-notification](../../../doc/low-level-design/09-notification.md) (all sections except §6.4 sizing and N-12), NTF-R01 … R06, ACC-R06 (notification side), ACC-R10 (socket side), HLD §5, §7.2, HLD-ADR-014 (API/worker split), HLD-ADR-018, LLD-ADR-008 |
| **Builds on** | `Bloom.Shared` ([shared-library spec](2026-10-31-shared-library-design.md)), gateway routes already in place ([api-gateway spec](2026-11-01-api-gateway-design.md): `/api/notifications/**` (customer, `user` policy), `/api/notifications/hub/**` (no JWT, `CheckOrigin`), `/api/admin/dlq/notification/**`), the existing message records in `Bloom.Identity.Contracts`, `Bloom.Order.Contracts`, `Bloom.Fulfillment.Contracts`, `Bloom.Payment.Contracts` |

## 1. Intent

`notification` turns business events into emails and in-app notifications, and pushes in-app notifications to open browser tabs in real time. It never decides *when* to notify; it only reacts to events (09-notification §1).

09-notification is the approved design, and this spec doesn't restate it. It records only the owner decisions and the builder calls the LLD leaves open. Where this spec is silent, 09-notification decides.

## 2. Goals

1. Notification **API** deployment:
   - `ListNotifications`, `GetUnreadCount`, `MarkRead`, `MarkAllRead`, `GetWsTicket` (customer, gRPC transcoded to JSON)
   - the SignalR hub at `/api/notifications/hub`, with the Redis backplane
   - the shared DLQ admin service (`MapBloomDeadLetterAdmin`, LLD-ADR-009)
2. Notification **worker** deployment:
   - the outbox relay (the DLQ admin's audit event is notification's only outgoing message)
   - the consumers of every message in 09-notification §5.3 (13 that notify, 4 with effects only)
   - the email sender (every 2 s)
   - the daily purges of §6.6, plus the shared retention purger
3. Automated tests for N-01 … N-11 and every edge case of 09-notification §8.

**Success criteria:** `dotnet build` passes with warnings as errors. `dotnet test` passes locally (Colima running) and in the existing CI workflow. Each test case N-01 … N-11 has at least one test named after its ID.

## 3. Non-goals and anti-goals

**Non-goals:**
- **Infrastructure:** a Dockerfile, Compose entries, Kubernetes manifests, HPA, ingress settings or alert rules (09-notification §6.4; alerts are metrics + error logs here).
- **Load test:** N-12 (100k sockets). The code must not rule it out (no per-connection database work, no unbounded per-connection memory), but no harness is built.
- **Client:** no browser client. The 0–10 s reconnect jitter and the refetch on reconnect (§6.3) are client behaviour and are not built here.
- **Cross-service tests:** end-to-end tests across services. Upstream services are stood in for by tests that publish their events to the broker.
- **Other projects:** any change to `Bloom.Shared`, the gateway, or any other service, including the message records in their `.Contracts` projects.
- **`doc/`:** any change under it (the proposed edits are in §9).

**Anti-goals** (a failure even if it technically works):
- Any method, message, field or error reason that neither 09-notification nor this spec lists (AGENTS.md §1).
- Two in-app rows or two email jobs for one event and type (NR-01).
- A verification code stored in plain text, logged, or left in `email_jobs.data` after a successful send (NR-03).
- A push sent before its transaction commits.
- A customer reading or marking another customer's notifications.
- A WebSocket accepted without a valid, unused ticket, or from an Origin outside the allow-list.
- A template that outputs a variable without HTML-escaping it, or an email with a tracking pixel.
- A test that reaches the internet.

## 4. Owner decisions

| Topic | Decision |
|---|---|
| Scope | All of 09-notification except §6.4 sizing and N-12. Code and tests only. |
| Email providers | Both behind one `EmailProvider` interface (HLD-ADR-018), selected by config. **Resend:** a hand-written client, `HttpClient` + `System.Text.Json`, one call `POST /emails` with `Idempotency-Key: bloom:email:<id>`. **SMTP (Mailpit):** .NET's built-in `System.Net.Mail.SmtpClient`. No mail packages. Tests use an in-process fake Resend and a Mailpit container, and no test reaches the internet. |
| Copy | The builder writes short, plain US-English copy: one Scriban template per notification type (HTML and plain-text email bodies, email subject, in-app title and body). The owner reviews the wording in the PR. |
| Branch | `feat/notification` off `feat/payment`, with the PR into `feat/payment`. |

## 5. Layout and dependencies (builder's call)

```
src/Notification/
  Bloom.Notification/            # one ASP.NET Core app; runs as notification-api or notification-worker by config
  Bloom.Notification.Tests/      # xUnit v3 + Testcontainers (PostgreSQL, RabbitMQ, Redis, Mailpit) + in-process fake Resend
```

- One app with a mode setting (`api` / `worker`), like payment (HLD-ADR-014).
- No `Bloom.Notification.Contracts` project: notification publishes no messages and exposes no internal gRPC. The customer proto lives in the service (package `bloom.notification.v1`).
- `AddBloom<NotificationDbContext>` provides the outbox, inbox, consumer host, retention purger, error mapping and metrics. `AddBloomTelemetry` provides OTel.
- EF Core migrations: the four tables of 09-notification §4.1 exactly, with the two `notifications` indexes and the two `UNIQUE` constraints, plus `AddBloomReliabilityTables`. Added index: `email_jobs(status, next_attempt_at)` for the sender.
- New packages (added to `Directory.Packages.props`):
  - `Scriban`: templates (named by 09-notification §6.1)
  - `Microsoft.AspNetCore.SignalR.StackExchangeRedis`: the backplane (§4.2)
  - `Microsoft.AspNetCore.SignalR.Client`: tests only
  - `Testcontainers` (generic container, for Mailpit), tests only, if not already pulled in by the existing Testcontainers packages
- New projects are added to `Bloom.slnx`.

## 6. Routes (builder's call)

| Surface | Methods → path |
|---|---|
| `/api/notifications/**` (customer) | `ListNotifications` `GET /api/notifications?page_token=…` · `GetUnreadCount` `GET /api/notifications/unread-count` · `MarkRead` `POST /api/notifications/read` (`{ids}`) · `MarkAllRead` `POST /api/notifications/read-all` · `GetWsTicket` `GET /api/notifications/ws-ticket`: gRPC transcoded to JSON |
| `/api/notifications/hub` | SignalR hub, WebSockets transport only, `?ticket=…` |
| `/api/admin/dlq/notification/**` | Shared DLQ admin |

- **Caller** comes from the trusted context (LLD 07 §4.3), read the way order reads it: a missing header is a server error, and a caller whose user type isn't `customer` gets `PERMISSION_DENIED` / `ROLE_NOT_ALLOWED`. Every query and update filters on the caller's `account_id`.
- **`ListNotifications`:** newest first, keyset on `(created_at, id)`, 20 per page. The page token is opaque (base64url of the last `created_at` and `id`). A malformed token returns `VALIDATION_FAILED`. Response rows: `id`, `type`, `title`, `body`, `link`, `read`, `createdAt`, plus `nextPageToken` (empty on the last page).
- **`MarkRead`:** 1–100 valid UUIDs, otherwise `VALIDATION_FAILED`. It sets `read_at = now()` on the caller's unread rows among the IDs. IDs that don't exist or belong to someone else are ignored without error, so the endpoint reveals nothing about other accounts. It returns nothing.
- **`MarkAllRead`:** sets `read_at` on all the caller's unread rows.
- **`GetWsTicket`:** 32 random bytes, base64url, stored as `ws:ticket:{ticket}` → `accountId`, TTL 30 s.

## 7. Implementation notes (builder's calls unless marked)

### Consumers

- **Queues and bindings** follow 00-reliability §6, with the shared inbox and retry schedule (1 s, 5 s, 30 s, 2 min, then DLQ + error log). A missing `contacts` or `order_refs` row throws a transient exception (09-notification §6.1 step 2).
- **One transaction per event** (§6.1): `order.paid` / `order.cancelled` upsert `order_refs` first. Rows are inserted with `ON CONFLICT (source_message_id, type | template) DO NOTHING`, so a redelivery is harmless even if the inbox missed it. The push happens after commit, only for an in-app row that this transaction actually inserted.
- **Email job data:** the template variables only (order number, tracking number, label URL, refund amount, invite URL, name), never rendered HTML. The email is rendered at send time from those variables.
- **`account.code`:** `data` holds `purpose`, the encrypted code exactly as received, and `expiresAt`. There is one template with text per purpose (`signup`, `reset_password`, `change_email`; anything else → generic "your Bloom code" text).
- **`order.cancelled` text per reason:** `customer`, `support`, `checkout_expired`, `late_payment`, `checkout_failed`; any other value → generic "your order was cancelled" text. Doc edit §9.2.
- **`fulfillment.delivery-failed` text per kind:** `returned_to_sender`, `lost`.
- **`fulfillment.return-closed`:** only `reason = expired` notifies. Other reasons are acknowledged with no effect.
- **Links:** in-app `link` = `/orders/{orderNumber}` for every order, shipping, return and refund type. Emails use `<StorefrontBaseUrl>/orders/{orderNumber}` (config). Code and invitation emails carry no order link; the invitation uses the payload's `inviteUrl`.
- **Money:** cents formatted as US dollars (`$1,234.56`) in templates.
- **`identity.tokens-revoked`:** publish `{accountId}` on the Redis channel `notification:disconnect` (see Sockets). No database write.
- **`identity.account-deleted`** (§6.5): in one transaction, delete the `contacts`, `notifications` and `order_refs` rows and set the account's `pending` email jobs to `cancelled`. After commit, publish the same disconnect message.

### Email sender

- Every 2 s: in one short transaction, claim up to 50 `pending` jobs with `next_attempt_at <= now()` (`FOR UPDATE SKIP LOCKED`, ordered by `next_attempt_at`) and push their `next_attempt_at` 1 min ahead as a lease, so another sender skips them. Then send each job and write its outcome in its own transaction, so a slow provider doesn't hold a lock. A sender that crashes mid-batch leaves its jobs to be picked up when the lease ends (a possible resend, accepted by NR-01).
- **Render:** Scriban templates embedded in the assembly, HTML auto-escaping on (every variable), US English. Every email has a subject, an HTML body and a plain-text body.
- **Code jobs:** decrypt with AES-256-GCM, format `base64url(nonce(12) ‖ ciphertext ‖ tag(16))`, no associated data, the same as identity's `SecretBox`. The key is config `Notification:CodeEncryptionKey` (32 bytes, base64; the same secret as identity's). A decryption failure is permanent: `failed` + error log (without the code). On success, `data` loses `encryptedCode` in the same update that sets `sent` (NR-03).
- **Resend:** bearer API key and base URL from config; request `{from, to, subject, html, text}`. HTTP 5xx, 429, timeouts and network errors are **transient**. Any other 4xx is **permanent** (09-notification §6.2 step 5), with Resend's error `name` kept in `last_error`. Timeout 10 s.
- **SMTP:** host, port, `from` from config; no credentials needed for Mailpit (optional user/password in config). `SmtpException` with a 5xx mailbox status (for example `MailboxUnavailable`) is permanent; anything else is transient. SMTP has no idempotency key, so a crash between send and update can resend (accepted, NR-01).
- **Retries:** transient → `attempts + 1`, `next_attempt_at` per 1 s / 5 s / 30 s / 2 min; the 5th failure → `failed` + error log (the alert). `last_error` keeps the latest reason, never the code or body.

### Sockets (SignalR)

- **Hub:** WebSockets transport only, so clients connect with `skipNegotiation` and the ticket is consumed exactly once, on the upgrade.
- **Connect check** (before the hub accepts the connection):
  1. `Origin` must be in config `Notification:AllowedOrigins`, otherwise reject with `ws_rejected_total{reason=origin}`. The gateway checks it too; this is the second check that 09-notification §10 measures.
  2. `GETDEL ws:ticket:{ticket}`; missing → reject with `ws_rejected_total{reason=ticket}`.
  3. Join group `user:{accountId}`.
- **Revocation race (accepted):** a ticket issued just before `tokens-revoked` can still open a socket after the disconnect command, for up to its 30 s life. That socket lives at most 15 min. 09-notification §6.3 relies on short-lived tickets here, so no ticket invalidation is built.
- **Lifetime:** each connection is aborted 15 min after it opened (a per-connection timer, cancelled on disconnect).
- **Keep-alive:** `KeepAliveInterval` 15 s, `ClientTimeoutInterval` 30 s.
- **Push message:** client method `notification`, payload `{id, type, title, link, createdAt}` (09-notification §9).
- **Pushing from the worker:** the worker registers SignalR with the same Redis backplane (same channel prefix and hub type) and sends through `IHubContext` without hosting the hub.
- **Disconnect across pods:** every API pod subscribes to `notification:disconnect` and aborts its local connections for that `accountId` (an in-memory map from account to connections on each pod). This is the "disconnect command" over the backplane Redis of 09-notification §4.2. Doc edit §9.3.

### Purges and metrics

- **Daily purge** (worker, 03:00 UTC): delete `notifications` older than 90 days, and `sent` / `failed` / `cancelled` `email_jobs` older than 30 days (by `created_at`), in batches of 1,000.
- **Metrics** of 09-notification §10 go on meter `Bloom.Notification`.
  - `ws_connections` is per pod.
  - `email_jobs_by_status` is a polled gauge.
  - `event_to_email_seconds` measures from the job's `created_at` (consume time) to the moment it is sent. Doc edit §9.4.
- **Alerts** (09-notification §10) are error logs plus metrics: a `failed` email job, a DLQ message, and processing errors.

## 8. Testing

- xUnit v3 + Testcontainers: real PostgreSQL, RabbitMQ, Redis and Mailpit (one container of each per run). The app runs in-process (`WebApplicationFactory`) in both modes.
- **Upstream events:** tests publish real envelopes for the §5.3 messages to the broker, using the existing contract records.
- **Fake Resend:** an in-process HTTP server for `POST /emails`. It records every request and idempotency key, and can be scripted per test with transient and permanent errors.
- **SMTP:** at least one test sends through Mailpit and reads the message back over Mailpit's HTTP API.
- **Sockets:** tests connect with the SignalR .NET client (WebSockets, `skipNegotiation`).
  - N-04: a reused ticket is rejected.
  - N-06 and N-07: two API hosts share one Redis. A push from the worker reaches a client on either host, and `tokens-revoked` closes the user's sockets on both.
  - A connection with a disallowed Origin is rejected.
- **N-03:** the code is visible in the sent email and gone from `email_jobs.data` afterwards; no log line contains it.
- `FakeTimeProvider` drives the sender's retry schedule, the 15-min lifetime, the purge schedule and ages.
- Tests need Colima running locally. All containers are removed after the run.

## 9. Proposed doc edits (not applied)

No edit under `doc/` is made in this project (AGENTS.md §9). These are for the owner to approve separately:

1. **09-notification §5:** add an error-reasons subsection. Only shared reasons are used: `VALIDATION_FAILED` (bad page token, `MarkRead` IDs) and `ROLE_NOT_ALLOWED` (caller isn't a customer). `MarkRead` ignores IDs that aren't the caller's.
2. **09-notification §5.3, `order.cancelled`:** order also sends `reason = checkout_failed`; add it to the text list.
3. **09-notification §4.2:** add the Redis channel `notification:disconnect` (cross-pod disconnect command, no TTL).
4. **09-notification §10:** `event_to_email_seconds` is measured from consume time (`email_jobs.created_at`), not from the event's publish time.

## 10. Left to the builder

Internal names, class and proto layout, request and response shapes beyond the fields 09-notification and this spec name, config key names other than those named here, template wording (owner reviews in the PR), the fake Resend's internals, and test isolation. Anything visible outside notification, or anything affecting the design, that 09-notification and this spec don't cover goes back to the owner (AGENTS.md §7).

## 11. Open items (owner)

None. The builder check raised nothing that needs the owner.
