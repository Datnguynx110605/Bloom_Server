# PD-ADR-003: Charge in full at checkout, online payments only

[Docs](../../README.md) › [Product design](../00-overview.md) › [Decision records](README.md)

| | |
|---|---|
| **Status** | Accepted |
| **Date** | 2026-10-31 |
| **Deciders** | Project owner |
| **Related** | CHK-R10, CHK-R11, ORD-R06, LCH-R15 |

## Context
Bloom sells high-value devices, including pre-orders that may wait weeks for stock. Card authorizations usually expire after about 7 days, so "authorize now, capture at shipment" breaks for long pre-order waits. Cash on delivery means shipping unpaid high-value devices that customers can refuse at the door.

## Decision drivers
- One reliable payment flow for all order types, including pre-orders.
- Money correctness: charged once, refunds traceable.
- Low risk for high-value goods.

## Considered options
**Timing**
1. Charge at checkout
2. Authorize at checkout, capture at shipment
3. Split by order type (authorize-then-capture for in-stock, charge for pre-orders)

**Methods**
1. Online only (card and wallets through the provider)
2. Online + cash on delivery
3. Online + store credit

## Decision
Chosen: **charge the full amount at checkout for every order, using online payment through the provider only**. One flow works for every order type, and cancellations become refunds.

## Pros and cons of the options
### Charge at checkout
- ✅ One flow. No authorization expiry problems.
- ❌ Every cancellation needs a refund (provider fees, customer waits for money back).

### Authorize, capture at shipment
- ✅ Fair to customers. No refunds for pre-shipment cancellations.
- ❌ Pre-orders longer than ~7 days need re-authorization, which can fail.

### Split by order type
- ❌ Two payment flows to build and test.

### Cash on delivery
- ❌ Ships unpaid high-value goods. Refused deliveries. Separate flow.

### Store credit
- ❌ Adds a balance ledger and split payments.

## Consequences
- ✅ Payment state is simple: an order is either unpaid or paid in full.
- ❌ Cancellations and returns always go through provider refunds.
- ⚠️ The backend must guarantee one charge per order even with retries (BI-05) and handle asynchronous payment confirmation.

## Links
- [05-checkout-payment](../05-checkout-payment.md), [06-orders-fulfillment](../06-orders-fulfillment.md)
