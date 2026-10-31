# HLD-ADR-018: Stripe, Shippo and Resend, each behind an interface in its owning service

| | |
|---|---|
| **Status** | Accepted |
| **Date** | 2026-10-31 |
| **Deciders** | Project owner |
| **Related** | CHK-R10, ORD-R09, RET-R03, NTF-R01, HA-02 |

## Context
Bloom needs a payment provider, a US carrier aggregator and an email provider, all usable in test mode.

## Decision drivers
- A usable sandbox / test mode.
- Idempotency and signed callbacks (payments).
- Swappable later.

## Considered options
- **Payment:** Stripe
- **Carrier:** EasyPost · Shippo
- **Email:** Amazon SES · SendGrid · Resend

## Decision
Chosen: **Stripe** (owned by payment), **Shippo** (owned by fulfillment), **Resend** (owned by notification). Each sits behind an interface in its owning service, and no other service calls a provider.

## Pros and cons of the options
### Stripe
- ✅ Excellent test mode, idempotency keys, signed webhooks, client-side card entry.

### Shippo
- ✅ US labels, return labels, tracking webhooks, a free test mode.

### Resend
- ✅ Simple API, developer-friendly.

## Consequences
- ✅ Providers can be swapped by implementing the interface.
- ⚠️ Local development email approach is open (OQ-HLD-7).

## Links
- [hld.md §7.3](../hld.md#73-external-integrations)
