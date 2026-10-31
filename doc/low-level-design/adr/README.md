# LLD Decision Records

[Docs](../../README.md) › [Low-level design](../README.md) › Decision records

Component-level decisions. Conventions and the MADR template are defined once in [product-design/adr/README.md](../../product-design/adr/README.md) and apply here with the prefix `LLD-ADR-`.

## Index

| ID | Title | Component | Status | Date |
|---|---|---|---|---|
| [LLD-ADR-001](LLD-ADR-001-two-phase-idempotency.md) | Two-phase idempotency with resume for provider calls | reliability | Accepted | 2026-10-31 |
| [LLD-ADR-002](LLD-ADR-002-rabbitmq-topology.md) | RabbitMQ topology | reliability | Accepted | 2026-10-31 |
| [LLD-ADR-003](LLD-ADR-003-error-contract.md) | Error contract: gRPC status + reason | reliability | Accepted | 2026-10-31 |
| [LLD-ADR-004](LLD-ADR-004-message-format-versioning.md) | Message format and versioning | reliability | Accepted | 2026-10-31 |
| [LLD-ADR-005](LLD-ADR-005-explicit-row-version.md) | Explicit row version column | reliability | Accepted | 2026-10-31 |
| [LLD-ADR-006](LLD-ADR-006-inbox-key.md) | Inbox key (consumer, message ID) | reliability | Accepted | 2026-10-31 |
| [LLD-ADR-007](LLD-ADR-007-stripe-checkout-hosted.md) | Customers pay on Stripe Checkout (hosted) | payment | Accepted | 2026-10-31 |
| [LLD-ADR-008](LLD-ADR-008-verify-first-signup.md) | Verify-first sign-up with a Data Protection registration token | identity | Accepted | 2026-10-31 |
| [LLD-ADR-009](LLD-ADR-009-dlq-admin-endpoint.md) | DLQ tool is an admin gRPC service per service | reliability | Accepted | 2026-10-31 |
| [LLD-ADR-010](LLD-ADR-010-dlq-busy-advisory-lock.md) | One DLQ operation at a time, enforced by a PostgreSQL advisory lock | reliability | Accepted | 2026-10-31 |
