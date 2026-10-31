# LLD-ADR-009: The DLQ tool is an admin gRPC service hosted by each service

[Docs](../../README.md) › [Low-level design](../README.md) › [Decision records](README.md)

| | |
|---|---|
| **Status** | Accepted |
| **Date** | 2026-10-31 |
| **Deciders** | Project owner |
| **Related** | HLD-ADR-007, HLD-ADR-016, [00-reliability.md §7.7](../00-reliability.md#77-dlq-admin), [07-api-gateway.md §5.1](../07-api-gateway.md#51-routes) |

## Context
HLD-ADR-007 requires a tool to inspect, replay and discard dead-lettered messages. 00-reliability allowed an ops CLI or an admin endpoint, Admin only, with every replay or discard in the audit log. A CLI has no signed-in staff identity to enforce Admin or to name as the audit actor, and needs broker credentials across every service. The gateway route table had no route for an admin endpoint.

## Decision drivers
- Admin-only, enforced from the trusted identity context like every other staff action.
- Every replay or discard audited through the existing `identity.staff-action-recorded.v1` path (HLD-ADR-016).
- Per-service broker credentials stay per service (00-reliability §11).

## Considered options
1. Admin gRPC service in the shared library, hosted by each service's API
2. Ops CLI with broker-wide credentials
3. RabbitMQ management UI / shovel plugin only

## Decision
Chosen: **a `DeadLetterAdmin` gRPC service shipped by the shared library and hosted by each domain service's API deployment**, reached through `/api/admin/dlq/{service}/**` (Staff, Admin only). Operations: list DLQs with depth, peek the oldest messages, replay one by message ID, replay all in one DLQ, discard one by message ID. Each replay or discard publishes one `identity.staff-action-recorded.v1` per message through the outbox.

## Pros and cons of the options
### Admin service per service
- ✅ Admin role and actor come from the gateway's trusted context; audit uses the outbox like every staff action.
- ✅ Each service only touches its own DLQs with its own credentials.
- ❌ API pods now open a RabbitMQ connection (previously workers only).
- ❌ One more gateway route.

### Ops CLI
- ❌ No staff identity for Admin-only or the audit actor; broker-wide credentials.

### Management UI / shovel
- ❌ Separate user system, no audit. Remains usable for read-only inspection by operators.

## Consequences
- ✅ T-09 (replay after fix) runs through the real tool.
- ❌ The gateway's own revocation consumer is not covered (the gateway has no database, so no outbox for audit). Its DLQ handling is decided in the gateway's implementation.
- ❌ No "discard all": removing many poison messages is one call per message, on purpose.

## Links
- [00-reliability.md §7.7](../00-reliability.md#77-dlq-admin)
