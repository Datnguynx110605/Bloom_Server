# Low-Level Design — Index

[Docs](../README.md) › Low-level design

One LLD per component. Each is consistent with the [high-level design](../high-level-design/high-level-design.md) and traces back to the [product rules](../product-design/00-overview.md). Read [00-reliability](00-reliability.md) first: every service builds on it.

## Components

| # | Document | Component | Purpose | Builds on | ADRs |
|---|---|---|---|---|---|
| 00 | [00-reliability](00-reliability.md) | Shared library | Outbox + relay, inbox, retries / DLQ, idempotency keys, conditional updates, optimistic locking, error contract | — | [001](adr/LLD-ADR-001-two-phase-idempotency.md) … [006](adr/LLD-ADR-006-inbox-key.md), [009](adr/LLD-ADR-009-dlq-admin-endpoint.md) |
| 01 | [01-launch](01-launch.md) | launch | Waiting room, admission passes, allocation and purchase-limit holds | 00 | — |
| 02 | [02-inventory](02-inventory.md) | inventory | Stock counters, reservations, backorders (strict FIFO), serial units, receipts, ledger | 00 | — |
| 03 | [03-order](03-order.md) | order | Cart, checkout saga, pricing, coupons, order lifecycle, cancellation | 00, 01, 02 | — |
| 04 | [04-payment](04-payment.md) | payment | Stripe Checkout sessions, webhooks, refunds, reconciliation | 00, 03 | [007](adr/LLD-ADR-007-stripe-checkout-hosted.md) |
| 05 | [05-fulfillment](05-fulfillment.md) | fulfillment | Packing, Shippo labels and tracking, returns, delivery failures, warranty coverage | 00, 02, 03, 04 | — |
| 06 | [06-identity](06-identity.md) | identity | Verify-first sign-up, tokens and revocation, staff MFA, audit log | 00, 03 | [008](adr/LLD-ADR-008-verify-first-signup.md) |
| 07 | [07-api-gateway](07-api-gateway.md) | api-gateway | Routing, JWT, revocation check, rate limits, trusted context | 06 | — |
| 08 | [08-catalog](08-catalog.md) | catalog | Products, SKUs, prices, search, cache, reviews, images | 00, 01 | — |
| 09 | [09-notification](09-notification.md) | notification | Emails, in-app notifications, WebSocket push | 00, 06 | — |

## Section template

Every service LLD uses the same sections, so readers know where to look:

1. Problem and scope · 2. Requirements and constraints · 3. Component structure · 4. Data model · 5. Interfaces (API, outgoing calls, messages, error reasons) · 6. Behaviour · 7. Concurrency and consistency · 8. Edge cases · 9. Security and validation · 10. Observability · 11. Test cases · 12. Open questions

## Shared conventions

| Topic | Where |
|---|---|
| Message naming, envelope, versioning | [00-reliability §5](00-reliability.md#5-message-contracts) |
| Broker topology (exchanges, queues, retries, DLQ) | [00-reliability §6](00-reliability.md#6-broker-topology-lld-adr-002) |
| Error contract (gRPC status + reason) | [00-reliability §8](00-reliability.md#8-error-contract-lld-adr-003) |
| Gateway routes and trusted identity headers | [07-api-gateway §4.3, §5.1](07-api-gateway.md#51-routes) |
| Decision records | [adr/README.md](adr/README.md) |
