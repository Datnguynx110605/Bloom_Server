# Identity service: design

| | |
|---|---|
| **Status** | Approved (2026-11-01) |
| **Date** | 2026-11-01 |
| **Branch / PR base** | `feat/identity` → `feat/api-gateway` |
| **Implements** | [06-identity](../../../doc/low-level-design/06-identity.md) (all sections), ACC-R01 … R14, BI-06, HLD §5, §7.1, §9.1, HLD-ADR-010, HLD-ADR-011, HLD-ADR-014 (API/worker split), HLD-ADR-016, [LLD-ADR-008](../../../doc/low-level-design/adr/LLD-ADR-008-verify-first-signup.md) |
| **Builds on** | `Bloom.Shared` ([shared-library spec](2026-10-31-shared-library-design.md)), gateway contract ([api-gateway spec](2026-11-01-api-gateway-design.md)) |

## 1. Intent

`identity` knows who every customer and staff member is, issues the tokens the gateway verifies, revokes them, and keeps the staff audit log (06-identity §1). The gateway already verifies identity's tokens and consumes `identity.tokens-revoked.v1`; this branch builds the service on the other end.

06-identity is the approved, complete design. This spec does not restate it. It records only the owner decisions and builder calls the LLD leaves open. Where this spec is silent, 06-identity decides.

## 2. Goals

1. Identity **API** deployment: every method of 06-identity §5.1, the JWKS endpoint, and the shared DLQ admin service (`MapBloomDeadLetterAdmin`, LLD-ADR-009).
2. Identity **worker** deployment: outbox relay, the audit consumer (§6.10), cleanup jobs (§3), retention purger (from `Bloom.Shared`).
3. Every message of §5.2 published through the outbox with a payload record in `Bloom.Identity.Contracts`.
4. `Bloom.Order.Contracts` holding the `HasUnsettledOrders` proto (owner decision, §4).
5. Automated tests for ID-T01 … ID-T16 and every edge case of §8.

**Success criteria:** `dotnet build` passes with warnings as errors. `dotnet test` passes locally (Colima running) and in the existing CI workflow. Each test case ID-T01 … ID-T16 has at least one test named after its ID.

## 3. Non-goals and anti-goals

**Non-goals:** first-Admin bootstrap (owner decision, §11). The order service itself (only its contracts project). Dockerfile, Compose entries, Kubernetes manifests. Any change to `Bloom.Shared` or the gateway. Any change under `doc/` (proposed edits in §9). ID-07 latency is checked by a test on the hash cost only; p95 under load is deferred to the cluster load test.

**Anti-goals** (failure even if it technically works):
- Any method, message, field, claim or error reason that 06-identity or this spec doesn't list (AGENTS.md §1).
- Revealing whether an email has an account on sign-in or reset (ID-05).
- An account row for an email not proven by a code, Google or an invitation (ID-08).
- Passwords, codes, tokens, TOTP secrets or registration tokens in logs, outbox rows or broker messages (§9, ID-T01d).
- Taking the email for `CompleteSignUp` from anywhere but the registration token (ID-T01c).

## 4. Owner decisions

| Topic | Decision |
|---|---|
| Scope | All of 06-identity, nothing more |
| Order dependency | `HasUnsettledOrders` `.proto` in a new order-owned project `src/Order/Bloom.Order.Contracts` (03-order §5: `account_id` → `bool`). Identity calls it with a real gRPC client; tests use a fake order server. Until order is deployed, deletion returns `ORDER_SERVICE_UNAVAILABLE` (§6.8 fail-safe). |
| Sign-out family | The access token carries `sid` = `family_id`. The gateway does not forward claims (LLD 07 §4.3), so the client reads `sid` from its own access token and sends it in the `SignOut` request body. Identity revokes that family only if it belongs to the caller (`x-bloom-user-id`), and clears the `bloom_rt` cookie (expired `Set-Cookie` with the same attributes). No gateway change. Doc edit §9.1. |
| Error reasons not in 06-identity §5.3 | Reused: wrong current password (`ChangePassword`, re-enrol accept) → `INVALID_CREDENTIALS`, counted toward the `signin:fail` / `signin:block` lockout; wrong first TOTP code at enrolment → `MFA_INVALID`; invalid Google ID token → `INVALID_CREDENTIALS`; `InviteStaff` for an email with an account → `EMAIL_TAKEN`. New: `INVITATION_INVALID` (`FAILED_PRECONDITION`, unknown / expired / used invite token); `VALIDATION_FAILED` (`INVALID_ARGUMENT`, bad email, name, address field or TOTP secret; field name in `ErrorInfo` metadata); `NOT_FOUND` (`NOT_FOUND`, unknown address, staff id, or a customer id given to a staff action); `ADDRESS_LIMIT_REACHED` (`FAILED_PRECONDITION`, 11th address); `PASSWORD_REQUIRED` (`FAILED_PRECONDITION`, `UnlinkGoogle` with no password); `GOOGLE_ALREADY_LINKED` (`ALREADY_EXISTS`, Google account linked to another account). Deactivating a deactivated account or reactivating an active one succeeds as a no-op. Doc edit §9.6. |
| Staff without a TOTP secret (after `ResetStaffMfa`) | `SignIn` runs the normal password check (failures count toward lockout), then returns `INVALID_CREDENTIALS` with no MFA challenge. The only way back in is the re-enrol link. Doc edit §9.2. |
| First Admin | Not built. Tests insert admin accounts directly. Open item §11. |
| MFA re-enrol after `ResetStaffMfa` | `ResetStaffMfa` writes a `staff_invitations` row for the existing staff email (no schema change) and publishes `staff-invited`. `AcceptStaffInvitation` with a token whose email already has a staff account requires the **current password** (no name / new password) plus TOTP enrolment, sets the new secret, issues tokens. Doc edit §9.2. |
| Dependencies | `Sodium.Core` (libsodium) for Argon2id; SecLists `10-million-password-list-top-100000.txt` (MIT) bundled as an embedded resource; Google ID tokens validated with the installed `Microsoft.IdentityModel.JsonWebTokens` against Google's JWKS; TOTP written on `System.Security.Cryptography.HMACSHA1`. |
| Deliverables | Code + tests only |
| Branch | `feat/identity` off `feat/api-gateway`, PR into `feat/api-gateway` |

## 5. Layout and dependencies (builder's call)

```
src/Identity/
  Bloom.Identity/            # one ASP.NET Core app; runs as identity-api or identity-worker by config
  Bloom.Identity.Contracts/  # message payload records (TokensRevokedV1 already there)
  Bloom.Identity.Tests/      # xUnit v3 + Testcontainers (PostgreSQL, Redis, RabbitMQ)
src/Order/
  Bloom.Order.Contracts/     # order-owned .proto for HasUnsettledOrders (client + server stubs)
```

- One app with a mode setting (`api` / `worker`), like the gateway (HLD-ADR-014).
- `AddBloom<IdentityDbContext>` provides outbox, inbox, consumer host, retention purger, error mapping, metrics; `AddBloomTelemetry` for OTel.
- EF Core migrations in `Bloom.Identity`; the tables of 06-identity §4.1 plus `AddBloomReliabilityTables`.
- New packages: `Sodium.Core`, `Microsoft.AspNetCore.DataProtection.EntityFrameworkCore` (named by §4.1 for the key ring). Already pinned: `Grpc.AspNetCore`, JSON transcoding, `Npgsql.EntityFrameworkCore.PostgreSQL`, `StackExchange.Redis`, `Microsoft.IdentityModel.JsonWebTokens`, Testcontainers.
- All new projects added to `Bloom.slnx`.

## 6. Routes (builder's call)

Methods are gRPC services transcoded to JSON; paths sit under the gateway prefixes of LLD 07 §5.1. Every method is `POST` unless stated.

| Gateway prefix (auth) | Methods → path |
|---|---|
| `/api/auth/**` (public) | `StartSignUp` `/api/auth/sign-up/start` · `VerifySignUpCode` `/api/auth/sign-up/verify` · `CompleteSignUp` `/api/auth/sign-up/complete` · `SignIn` `/api/auth/sign-in` · `VerifyStaffMfa` `/api/auth/staff/mfa` · `SignInWithGoogle` `/api/auth/google` · `RequestPasswordReset` `/api/auth/password-reset/request` · `ResetPassword` `/api/auth/password-reset/confirm` · `AcceptStaffInvitation` `/api/auth/staff/invitations/accept` |
| `POST /auth/refresh` (cookie) | `Refresh` |
| `/api/me/**` (any user) | `GetMe` `GET /api/me/profile` · `SignOut` `/api/me/sign-out` · `SignOutEverywhere` `/api/me/sign-out-everywhere` · `ChangeEmail` `/api/me/email` · `ConfirmEmailChange` `/api/me/email/confirm` · `ChangePassword` `/api/me/password` · `LinkGoogle` `/api/me/google` · `UnlinkGoogle` `DELETE /api/me/google` · `ListAddresses` `GET /api/me/addresses` · `UpsertAddress` `PUT /api/me/addresses` · `DeleteAddress` `DELETE /api/me/addresses/{id}` · `DeleteAccount` `DELETE /api/me/account` |
| `/api/admin/staff/**` (Admin) | `InviteStaff` `/api/admin/staff/invitations` · `ListStaff` `GET /api/admin/staff/members` · `ChangeStaffRole` `/api/admin/staff/members/{id}/role` · `DeactivateStaff` `…/{id}/deactivate` · `ReactivateStaff` `…/{id}/reactivate` · `ResetStaffMfa` `…/{id}/reset-mfa` |
| `/api/admin/audit/**` (Admin) | `QueryAuditLog` `GET /api/admin/audit/entries` |
| internal, not routed | `GET /.well-known/jwks.json` (plain endpoint, not gRPC) |

- Per-method checks from the trusted context (LLD 07 §4.3): customer-only methods (`ChangeEmail`, `ConfirmEmailChange`, Google link / unlink, addresses, `DeleteAccount`) refuse staff; admin methods re-check `role = admin`. Refusal → `PERMISSION_DENIED` / `ROLE_NOT_ALLOWED` (00-reliability §8).
- `UpsertAddress`: no `id` → create; `id` + `version` → update with optimistic locking (`CONCURRENCY_CONFLICT`).
- `Refresh` and the sign-in methods read / write the cookie through the underlying `HttpContext`. Refresh responses carry `Cache-Control: no-store` (§9).

## 7. Implementation notes (builder's calls unless marked)

- **Tokens.** ES256 JWT with the claims of §4.3 plus `sid` (owner decision). Signing keys come from configuration: a list of `{kid, PEM private key}` plus `ActiveKeyId`. The JWKS publishes the public part of every listed key; the operator follows the §4.3 rotation timing by editing the list. Tests generate keys.
- **Registration token.** Data Protection time-limited protector, purpose `bloom.registration.v1`, 30 min, key ring in `data_protection_keys`, keys protected with a configured certificate (tests: unprotected ephemeral ring).
- **Codes.** HMAC-SHA256(pepper, subject ‖ purpose ‖ code), where subject = `account_id`, or the normalized email for `signup` (no account exists yet; §6.5 says "account"). Doc edit §9.3.
- **Encrypted code** in `identity.verification-code.v1`: AES-256-GCM, 12-byte random nonce, 16-byte tag, `encryptedCode` = base64url(nonce ‖ ciphertext ‖ tag) of the UTF-8 code, no associated data. Notification decrypts with the same layout.
- **TOTP.** RFC 6238, SHA-1, 6 digits, 30 s, ±1 step. Secret stored as AES-256-GCM (nonce ‖ ciphertext ‖ tag) with the configured TOTP key. Replay block ("a code can't be reused within its window", §6.1): Redis key `mfa:used:{accountId}:{step}`, TTL 90 s, set with `NX`. Doc edit §9.4.
- **TOTP enrolment** in `AcceptStaffInvitation`: the client sends a base32 secret (≥ 160 bits) and the first code, as §5.1 lists ("secret + first code"); the server verifies the code before storing the secret.
- **Passwords.** NFKC first; length 12–128 counted in Unicode scalar values; breached-list check case-insensitive against the bundled list. Argon2id via `Sodium.Core` (`opsLimit` 3, `memLimit` 64 MiB; libsodium parallelism is 1, matching §6.1); rehash on sign-in when the stored parameters are lower. The dummy hash for unknown emails is computed once at startup.
- **Google.** Google JWKS URL is configuration (default `https://www.googleapis.com/oauth2/v3/certs`), cached in memory for 1 h and refetched on an unknown `kid`. `iss` ∈ {`accounts.google.com`, `https://accounts.google.com`}, `aud` = configured client ID, `email_verified = true`.
- **Order client.** Deadline 2 s. Any `RpcException` → `ORDER_SERVICE_UNAVAILABLE`. `UNSETTLED_ORDERS` carries no retry date (order's contract returns only a `bool`).
- **Messages.** Payload records in `Bloom.Identity.Contracts`: `CustomerRegisteredV1`, `ContactChangedV1`, `AccountDeletedV1`, `VerificationCodeV1`, `StaffInvitedV1`, plus the existing `TokensRevokedV1` (`issuedBefore` Unix ms, as the gateway decoded it). Published to exchange `identity.events` with the message type as routing key (00-reliability §5–6). `contact-changed` is published on `ConfirmEmailChange`.
- **Invite URL** = configured base URL + `?token=` + the raw invite token (only its hash is stored).
- **Audit consumer.** Queue `identity.audit`, consumer host from `Bloom.Shared`; payload is the existing `Bloom.Shared.DeadLetters.StaffActionRecorded`; `id` = envelope `messageId`, `occurred_at` = envelope `occurredAt`; `INSERT … ON CONFLICT DO NOTHING` in the inbox transaction. Identity's own staff actions write `audit_log` directly in their transaction (§6.9).
- **`SignOut`** with a `sid` that is unknown or belongs to another account → `NOT_FOUND`; a family that is already revoked → success (idempotent).
- **`ChangePassword`** follows §6.4 as written: `revoke_all`, no new tokens in the response. The caller is signed out everywhere, this device included, and signs in again with the new password.
- **Identity's own audit rows** (§6.9): `service` = `identity`, `target_type` = `staff_account`, `action` ∈ `identity.staff-invited`, `identity.staff-role-changed`, `identity.staff-deactivated`, `identity.staff-reactivated`, `identity.staff-mfa-reset` (the `<service>.<event>` pattern of §4.1's examples); `details` holds before / after role or status where relevant.
- **Audit queue bindings**: `identity.audit` binds `identity.staff-action-recorded.v1` on the `<service>.events` exchange of all eight services (catalog, inventory, launch, order, payment, fulfillment, identity, notification), since every service hosts the DLQ admin that publishes it (LLD-ADR-009).
- **`LAST_ADMIN`.** In the same transaction: lock the active admin rows (`FOR UPDATE`), count, refuse if the change leaves zero.
- **Cleanup jobs** (worker, hourly): delete codes, invitations and refresh tokens whose `expires_at` is more than 1 day past.
- **Metrics** of §10 on meter `Bloom.Identity`.

## 8. Testing

- xUnit v3 + Testcontainers: real PostgreSQL, Redis and RabbitMQ (one container each per run). The app runs in-process (`WebApplicationFactory`), driven over transcoded HTTP (cookies, status codes) and gRPC where simpler.
- Fakes: Google JWKS endpoint with test RSA keys; an order gRPC server returning true / false / unavailable; `FakeTimeProvider` for code, token, block and family expiry.
- ID-T03 (timing) asserts that an unknown email runs exactly one Argon2id verification, not wall-clock timing.
- ID-07: one test asserts the configured Argon2id parameters; latency is not asserted.
- Tests need Colima running locally. All containers are removed after the run.

## 9. Proposed doc edits (not applied)

No edit under `doc/` is made in this project (AGENTS.md §9). For the owner to approve separately:

1. 06-identity §4.3 access-token claims: add `sid` = `family_id` of the refresh-token family; §5.1 `SignOut` takes `sid` in the request (read by the client from its own token); §6.4 `SignOut` revokes that family if it belongs to the caller and clears the cookie.
2. 06-identity §6.9 `ResetStaffMfa` / `AcceptStaffInvitation`: the re-enrol link is a `staff_invitations` row for the existing staff email; accepting it requires the current password (failures count toward sign-in lockout) and a new TOTP enrolment, then issues tokens. §6.1: a staff account with no TOTP secret gets `INVALID_CREDENTIALS` after the password check, with no challenge.
3. 06-identity §6.5: code HMAC subject is the account ID, or the normalized email for `signup`.
4. 06-identity §4.2: new Redis key `mfa:used:{accountId}:{step}`, TTL 90 s (TOTP replay block).
5. 06-identity §5.2: payload types of every identity message as recorded in `Bloom.Identity.Contracts`, and the `encryptedCode` layout of §7.
6. 06-identity §5.3: add `INVITATION_INVALID`, `VALIDATION_FAILED`, `NOT_FOUND`, `ADDRESS_LIMIT_REACHED`, `PASSWORD_REQUIRED`, `GOOGLE_ALREADY_LINKED`, and the reuse rules of §4 (Error reasons row).

## 10. Left to the builder

Internal names, class and proto layout (package `bloom.identity.v1`, one gRPC service per §3 component), request / response message shapes beyond the fields §5.1 names, test isolation, package versions (latest stable compatible with .NET 10). Anything visible outside identity or affecting the design that 06-identity and this spec don't cover goes back to the owner (AGENTS.md §7).

## 11. Open items (owner)

1. **First Admin** — how the first Admin account comes to exist. Deferred by owner decision; nothing built.
