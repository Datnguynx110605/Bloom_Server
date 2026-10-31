# HLD-ADR-010: ES256 JWTs verified at the gateway, strict refresh rotation, event-fed revocation

| | |
|---|---|
| **Status** | Accepted |
| **Date** | 2026-10-31 |
| **Deciders** | Project owner |
| **Related** | ACC-R01 … R11, QA-08 |

## Context
The owner's design: identity issues 15-minute JWT access tokens and 7-day refresh tokens with rotation; reusing an expired or revoked refresh token revokes the whole token family; the gateway validates tokens and services are stateless. ACC-R10 requires staff deactivation to take effect immediately, which a stateless 15-minute token can't do alone.

## Decision drivers
- Stateless verification at the edge.
- Immediate revocation (ACC-R10).
- A leaked verifier must not be able to mint tokens.

## Considered options
- **Signing:** ES256 + JWKS · HS256 shared secret
- **Revocation:** gateway check · shorter staff tokens · introspection per request
- **Revocation feed:** event-fed gateway Redis · gateway reads identity's Redis
- **Reuse detection:** strict · short grace window

## Decision
Chosen:
- identity signs with **ES256**, publishes public keys via **JWKS**, and rotates keys by key ID. Only the gateway verifies.
- Access tokens 15 min; refresh tokens 7 days, rotated on each use. **Strict reuse detection**: any reuse revokes the family, with no grace window.
- identity publishes **`Tokens revoked`** (user, issued-before time) through the outbox. The gateway stores it in **its own Redis** and checks every request. ACC-R10 is kept.

## Pros and cons of the options
### ES256 + JWKS
- ✅ The gateway can't mint tokens; key rotation without redeploys.

### Gateway check, event-fed
- ✅ Revocation within ~1 s, no cross-service reads.
- ❌ One Redis lookup per request.

### Shorter staff tokens
- ❌ Would have required relaxing ACC-R10.

### Strict reuse detection
- ✅ Maximum protection against stolen refresh tokens.
- ❌ Concurrent refreshes (two tabs) log the user out (R-04).

## Consequences
- ✅ Logout-everywhere and password-change revocation also work.
- ⚠️ Frontend constraint: only one refresh request in flight across tabs.
- ⚠️ Refined in [06-identity](../../low-level-design/06-identity.md): refresh token in an HttpOnly cookie; staff sessions 12 h absolute; reuse revokes all of the user's sessions, not only the family.

## Links
- [hld.md §9.1](../hld.md#91-security)
