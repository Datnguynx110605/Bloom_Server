# HLD Decision Records

[Docs](../../README.md) › [High-level design](../high-level-design.md) › Decision records

Architecture decisions for Bloom's backend. Conventions and the MADR template are defined once in [product-design/adr/README.md](../../product-design/adr/README.md) and apply here with the prefix `HLD-ADR-`.

## Index

| ID | Title | Status | Date |
|---|---|---|---|
| [HLD-ADR-001](HLD-ADR-001-microservices-boundaries.md) | Microservices: 8 domain services + gateway | Accepted | 2026-10-31 |
| [HLD-ADR-002](HLD-ADR-002-technology-stack.md) | .NET, PostgreSQL, Redis, RabbitMQ | Accepted | 2026-10-31 |
| [HLD-ADR-003](HLD-ADR-003-communication.md) | gRPC internally, RabbitMQ async, REST/JSON at the edge | Accepted | 2026-10-31 |
| [HLD-ADR-004](HLD-ADR-004-data-ownership.md) | Database per service, Redis per service, event-fed copies | Accepted | 2026-10-31 |
| [HLD-ADR-005](HLD-ADR-005-hybrid-sagas.md) | Hybrid sagas; compensations as outbox commands | Accepted | 2026-10-31 |
| [HLD-ADR-006](HLD-ADR-006-transactional-outbox.md) | Transactional outbox with polling relay | Accepted | 2026-10-31 |
| [HLD-ADR-007](HLD-ADR-007-inbox-retries-dlq.md) | Inbox, retries and dead-letter queue | Accepted | 2026-10-31 |
| [HLD-ADR-008](HLD-ADR-008-idempotency-payments.md) | Idempotency keys and four layers against double charges | Accepted | 2026-10-31 |
| [HLD-ADR-009](HLD-ADR-009-concurrency-control.md) | Atomic conditional updates and optimistic locking | Accepted | 2026-10-31 |
| [HLD-ADR-010](HLD-ADR-010-authentication-tokens.md) | JWT at the gateway, refresh rotation, revocation | Accepted | 2026-10-31 |
| [HLD-ADR-011](HLD-ADR-011-service-trust.md) | Trusted identity context and mTLS | Accepted | 2026-10-31 |
| [HLD-ADR-012](HLD-ADR-012-launch-path.md) | Launch path design | Accepted | 2026-10-31 |
| [HLD-ADR-013](HLD-ADR-013-checkout-orchestration.md) | Checkout orchestration, expiry, late payments | Accepted | 2026-10-31 |
| [HLD-ADR-014](HLD-ADR-014-deployment.md) | Deployment topology | Accepted | 2026-10-31 |
| [HLD-ADR-015](HLD-ADR-015-observability.md) | Observability stack | Accepted | 2026-10-31 |
| [HLD-ADR-016](HLD-ADR-016-audit-log.md) | Audit log ownership | Accepted | 2026-10-31 |
| [HLD-ADR-017](HLD-ADR-017-postgres-full-text-search.md) | Catalog search on PostgreSQL | Accepted | 2026-10-31 |
| [HLD-ADR-018](HLD-ADR-018-external-providers.md) | External providers behind interfaces | Accepted | 2026-10-31 |
| [HLD-ADR-019](HLD-ADR-019-consumers-pause-during-own-db-outage.md) | Consumers pause during their own database outage | Accepted | 2026-11-12 |
| [HLD-ADR-020](HLD-ADR-020-health-endpoints.md) | Health endpoints | Accepted | 2026-11-14 |
| [HLD-ADR-021](HLD-ADR-021-checkout-latency-excludes-payment.md) | Checkout start latency excludes the payment call | Accepted | 2026-11-14 |
| [HLD-ADR-022](HLD-ADR-022-stripe-rate-limit.md) | Stripe rate limit sized to the launch admission rate | Accepted | 2026-11-14 |
