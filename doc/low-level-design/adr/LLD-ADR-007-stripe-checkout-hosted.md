# LLD-ADR-007: Customers pay on Stripe Checkout (hosted page)

| | |
|---|---|
| **Status** | Accepted |
| **Date** | 2026-10-31 |
| **Deciders** | Project owner |
| **Related** | CHK-R10, QA-09, HLD-ADR-018, [04-payment.md](../04-payment.md) |

## Context
Bloom charges in full at checkout and must never handle card data. Stripe offers an embedded Payment Element (customer stays on Bloom) or a hosted Checkout page (redirect). Bloom's checkout deadline is 15 minutes; a Checkout Session can't expire sooner than 30 minutes.

## Decision drivers
- Card data never touches Bloom (QA-09).
- Least payment UI to build and secure.
- Late payments must stay rare and always be refunded.

## Considered options
1. PaymentIntent + Payment Element (embedded)
2. Stripe Checkout (hosted)

## Decision
Chosen: **Stripe Checkout (hosted)**. `payment` creates one Checkout Session per order and returns its URL. When the order's checkout expires, `payment` expires the session through the API (cancel-payment command). A payment that still slips through is refunded as a late payment.

## Pros and cons of the options
### Payment Element
- ✅ Customer stays on Bloom; session lifetime fully under Bloom's control.
- ❌ More frontend work; Bloom hosts the payment form page.

### Stripe Checkout
- ✅ Stripe hosts the whole payment page, wallets and retries.
- ❌ Customer leaves Bloom and returns via redirect.
- ❌ Session lifetime minimum 30 min, so Bloom must expire sessions explicitly.

## Consequences
- ✅ No payment form to build or secure.
- ❌ A narrow late-payment window remains (payment in the same second as expiry), covered by the late-payment refund.
- ⚠️ `order` returns a `payment_url`, not a client secret.

## Links
- [04-payment.md §6.1, §6.4, §8](../04-payment.md)
