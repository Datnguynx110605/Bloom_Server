# LLD-ADR-013: A revoked refresh token is not reuse

[Docs](../../README.md) › [Low-level design](../README.md) › [Decision records](README.md)

| | |
|---|---|
| **Status** | Accepted |
| **Date** | 2026-11-12 |
| **Deciders** | Project owner |
| **Related** | ID-03, [06-identity.md §6.3](../06-identity.md#63-issuing-and-refreshing-tokens), [§6.4](../06-identity.md#64-revocation) |

## Context
06-identity §6.3 treated a refresh token in state `rotated` or `revoked` as reuse: revoke every token of the account, publish `tokens-revoked`, return `REFRESH_INVALID` (ID-03). But `revoke_all` itself sets every token to `revoked` (§6.4), and so do `SignOutEverywhere`, a password change or reset, staff deactivation and account deletion. After `SignOutEverywhere`, a device that still holds an old refresh token and refreshes would be counted as a theft: it would revoke the session the user has just opened and raise `refresh_total{result="reuse"}`.

## Decision drivers
- Keep ID-03 for a rotated token: replay of a token that was already exchanged is the theft signal.
- A deliberate revocation must not revoke the user's new session.
- The `reuse` alert must stay meaningful (§10).

## Considered options
1. Keep `rotated` and `revoked` as reuse (§6.3 as written)
2. Only `rotated` is reuse; `revoked` returns `REFRESH_INVALID` with no side effects

## Decision
Chosen: **only `rotated` counts as reuse**. A token in state `revoked` gets `REFRESH_INVALID` with no side effects: no revoke-all, no outbox `tokens-revoked`, no `refresh_total{result="reuse"}`. ID-03 is unchanged. §6.3 splits the `rotated` / `revoked` row.

## Pros and cons of the options
### `rotated` and `revoked` are reuse
- ✅ Simplest rule.
- ❌ A sign-out everywhere kills the user's new session when an old device refreshes.
- ❌ The `reuse` alert fires on ordinary sign-outs.

### Only `rotated` is reuse
- ✅ ID-03 keeps its meaning: a replayed, already-exchanged token revokes everything.
- ✅ An old device after `SignOutEverywhere` gets `REFRESH_INVALID` and the new session stays valid.
- ❌ A thief who replays a token revoked by an earlier `revoke_all` is not detected as reuse. That token is already dead, so it gives no access.

## Consequences
- ✅ `refresh_total{result="reuse"}` counts only rotated-token replays.
- ✅ The client of an old device is simply asked to sign in again.

## Links
- [06-identity.md §6.3](../06-identity.md#63-issuing-and-refreshing-tokens)
- [06-identity.md §6.4](../06-identity.md#64-revocation)
