# HLD-ADR-011: Gateway passes a trusted identity context; Linkerd mTLS secures internal calls

| | |
|---|---|
| **Status** | Accepted |
| **Date** | 2026-10-31 |
| **Deciders** | Project owner |
| **Related** | HLD-ADR-010, [hld.md §9.1](../hld.md#91-security) |

## Context
The owner wants JWT verification only at the gateway. But resource-level authorization ("does order #123 belong to this customer?") needs data only the owning service has. Without it, any customer could read others' orders by changing IDs (IDOR). Services therefore need a caller identity they can trust.

## Decision drivers
- Verify tokens once, at the edge.
- Services must check resource ownership.
- Clients must not be able to forge identity.

## Considered options
1. Trusted context + mTLS (Linkerd)
2. Trusted context + Kubernetes NetworkPolicy
3. Forward the JWT; services re-verify

## Decision
Chosen: **trusted context + mTLS**. The gateway verifies the JWT, applies coarse role rules per route, **strips identity headers sent by clients**, and forwards a trusted context (user ID, user type, role, auth time). Services trust it and check **resource ownership**. **Linkerd** provides mTLS and allow rules, so only the gateway and permitted services can reach each service. Calls without a user (consumers, saga steps) are authorized by service identity.

## Pros and cons of the options
### Context + mTLS
- ✅ Single verification point; encrypted, authenticated internal traffic.
- ❌ A mesh to run.

### Context + NetworkPolicy
- ❌ Unencrypted internal traffic; weak identity for calls without a user.

### Re-verify everywhere
- ❌ Duplicates the gateway's job.

## Consequences
- ✅ IDOR prevented by ownership checks in services.
- ❌ Security depends on the mesh allow rules being correct.

## Links
- [hld.md §9.1](../hld.md#91-security)
