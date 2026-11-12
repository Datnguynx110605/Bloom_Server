# Bloom — Product Design Overview

[Docs](../README.md) › Product design

| | |
|---|---|
| **Status** | Approved (2026-11-12) |
| **Last updated** | 2026-10-31 |
| **Scope** | Business domain and product scope. No technical design (see `doc/high-level-design/`). |

## Summary

Bloom is an independent online store that sells Apple devices and accessories to consumers in the United States. One business sells its own stock from one warehouse. The product covers the full purchase lifecycle (browse, checkout, pay, ship, deliver, return, refund), plus three device-specific capabilities: **launch-day pre-orders with a waiting room**, **serial / IMEI tracking per unit**, and **warranty add-on plans**.

Bloom is a portfolio project for a backend engineer. Scope is chosen for **depth over breadth**: a small set of hard problems (no overselling under contention, charging exactly once, consistent order state, full stock traceability) solved and documented well.

---

## 1. Context and purpose

| | |
|---|---|
| **Business model** | Single-vendor B2C. Bloom owns all stock. ([PD-ADR-001](adr/PD-ADR-001-single-vendor-b2c-single-warehouse.md)) |
| **Products** | Apple devices and accessories, sold as an independent store. |
| **Market** | United States only, USD only. ([PD-ADR-002](adr/PD-ADR-002-us-market-flat-sales-tax.md)) |
| **Why it exists** | To demonstrate backend engineering depth in a resume portfolio. |
| **Delivery order** | Product design → backend design (HLD, LLD) → frontend design. |

## 2. Goals

| ID | Goal |
|---|---|
| G1 | Support the complete purchase lifecycle: browse → checkout → pay → pick/pack → ship → deliver → return → refund. |
| G2 | Stay correct under heavy contention on launch day: fixed allocations and purchase limits are never exceeded. |
| G3 | Make every physical unit and every stock movement traceable: serial-level tracking, a stock ledger, and a staff audit log. |
| G4 | Keep money correct: each order is charged once, refunds never exceed what was paid, and discounts are allocated per line. |

## 3. Business invariants (success criteria)

These must hold at all times. Each is a testable success criterion for the backend.

| ID | Invariant | Source |
|---|---|---|
| BI-01 | Units sold or held for a launch SKU never exceed its pre-order allocation. | [04-launch](04-launch.md) |
| BI-02 | Units sold or held for a regular SKU never exceed its on-hand stock. | [03-inventory](03-inventory.md) |
| BI-03 | Units bought or held by one account for a launch product never exceed that product's purchase limit. | [04-launch](04-launch.md) |
| BI-04 | Coupon uses (held + consumed) never exceed the coupon's total or per-customer limit. | [08-promotions](08-promotions.md) |
| BI-05 | An order is charged at most once. Total refunds on an order line never exceed the line's paid amount. | [05-checkout-payment](05-checkout-payment.md) |
| BI-06 | Every stock change has a stock ledger entry. Every staff action that changes data has an audit log entry. | [03-inventory](03-inventory.md), [10-accounts-access](10-accounts-access.md) |
| BI-07 | Every shipped serialized unit is linked to exactly one order line. | [03-inventory](03-inventory.md) |

Quantitative targets (launch traffic, latency, availability) are defined in [HLD §3](../high-level-design/high-level-design.md#3-requirements).

## 4. Scope

| Area | In scope | Document |
|---|---|---|
| Catalog | Products, option types, predefined SKUs, prices, categories, warranty plans, search | [02-catalog](02-catalog.md) |
| Inventory | Single warehouse, stock per SKU, serial units, reservations, goods receipt, adjustments, stock ledger | [03-inventory](03-inventory.md) |
| Launch | Launch events, fixed pre-order allocation, waiting room, purchase limits | [04-launch](04-launch.md) |
| Checkout & payment | Cart, checkout with stock reservation, flat tax, shipping options, online payment, refunds | [05-checkout-payment](05-checkout-payment.md) |
| Orders & fulfillment | Order lifecycle, cancellation, pick/pack, carrier shipments, delivery failures | [06-orders-fulfillment](06-orders-fulfillment.md) |
| Returns & refunds | 14-day returns, inspection, full / partial refund or rejection | [07-returns-refunds](07-returns-refunds.md) |
| Promotions | Coupon codes with usage limits | [08-promotions](08-promotions.md) |
| Reviews | Verified-buyer product reviews, post-moderation | [09-reviews](09-reviews.md) |
| Accounts & access | Customer accounts, staff accounts, fixed roles, audit log | [10-accounts-access](10-accounts-access.md) |
| Notifications | Email and in-app notifications | [11-notifications](11-notifications.md) |

## 5. Out of scope (future)

| Capability | Note |
|---|---|
| Trade-in | Long-running quote → inspection → final value workflow. |
| Multiple warehouses / store pickup | Single warehouse only. |
| Cash on delivery | Online payment only ([PD-ADR-003](adr/PD-ADR-003-charge-at-checkout-online-only.md)). |
| Store credit, gift cards, loyalty points | No balance ledgers. |
| Warranty claims / repair workflow | Warranty is sell + coverage lookup only. |
| Configure-to-order (custom Mac builds) | Predefined SKUs only. |
| Purchase orders / supplier management | Goods receipt only. |
| Automatic sales, bundles, buy-X-get-Y | Coupon codes only. |
| SMS notifications | Email + in-app only. |
| Multi-currency, multi-country | US / USD only. |
| Real sales-tax calculation | Single flat rate ([PD-ADR-002](adr/PD-ADR-002-us-market-flat-sales-tax.md)). |
| Wishlists, recommendations | — |
| Open-box resale of returned units | Returned units are never resold ([PD-ADR-006](adr/PD-ADR-006-per-product-serial-tracking.md)). |

## 6. Actors

### People

| Actor | Description |
|---|---|
| Customer | Signed-in consumer with a verified email. Browses, buys, cancels, returns, reviews. |
| Admin | Staff with full access, including staff management. |
| Catalog manager | Staff managing products, prices, launches and coupons. |
| Warehouse | Staff receiving stock, picking and packing, scanning serials, inspecting returns, adjusting stock. |
| Support | Staff handling orders, cancellations, refunds, return decisions and review moderation. |

The full permission matrix is in [10-accounts-access](10-accounts-access.md).

### External systems

| System | Role | Vendor |
|---|---|---|
| Payment provider | Charges and refunds (card, wallets). Bloom never stores card data. | Chosen in HLD |
| Carrier aggregator | Shipping labels, return labels, tracking updates (US). | Chosen in HLD (sandbox / test mode) |
| Email provider | Transactional email. | Chosen in HLD |
| Google | "Sign in with Google" for customers. | Google |

## 7. Commercial rules at a glance

| Rule | Value |
|---|---|
| Currency | USD |
| Shipping destinations | US addresses only |
| Sales tax | One flat rate, set in configuration, applied to goods only |
| Shipping | Standard free; Express flat fee set by Admin |
| Payment | Online only, charged in full at checkout |
| Checkout reservation | 15 minutes |
| Return window | 14 days from delivery |
| Regular SKU quantity cap | 10 per order line |

## 8. Assumptions and constraints

| ID | Assumption / constraint |
|---|---|
| A-01 | Bloom is presented as an independent store selling Apple devices. It must not copy Apple's branding, layout or marketing assets (relevant to the frontend). |
| A-02 | All external integrations run in sandbox / test mode. Vendors are chosen in the HLD. |
| A-03 | Flat sales tax is a deliberate simplification. A real US store would calculate tax per address. |

## 9. Decision records

| ID | Decision |
|---|---|
| [PD-ADR-001](adr/PD-ADR-001-single-vendor-b2c-single-warehouse.md) | Single-vendor B2C, single warehouse |
| [PD-ADR-002](adr/PD-ADR-002-us-market-flat-sales-tax.md) | US market with a flat sales-tax rate |
| [PD-ADR-003](adr/PD-ADR-003-charge-at-checkout-online-only.md) | Charge in full at checkout, online payments only |
| [PD-ADR-004](adr/PD-ADR-004-reserve-stock-at-checkout.md) | Reserve stock at checkout start, with a timeout |
| [PD-ADR-005](adr/PD-ADR-005-launch-allocation-waiting-room.md) | Launch: fixed allocation, waiting room, per-launch purchase limits |
| [PD-ADR-006](adr/PD-ADR-006-per-product-serial-tracking.md) | Serial tracking per product flag; returned units never resold |
| [PD-ADR-007](adr/PD-ADR-007-coupon-allocation-and-holding.md) | Coupon discount split per line; coupon uses held at checkout |
| [PD-ADR-008](adr/PD-ADR-008-account-required-staff-mfa.md) | Account required to buy; staff invited with required MFA |
| [PD-ADR-009](adr/PD-ADR-009-no-price-change-flag-in-cart.md) | No price-change flag in the cart |

## 10. Document map

| File | Content |
|---|---|
| [01-glossary](01-glossary.md) | Domain terms used across all documents |
| [02-catalog](02-catalog.md) … [11-notifications](11-notifications.md) | One file per domain area: scope, rules, flows, states, roles, open questions |
| [adr/](adr/README.md) | Product-design decision records and template |

**Conventions**
- Business rules have stable IDs (`CAT-R01`, `INV-R01`, …) so the HLD and LLD can trace back to them.
- State names in diagrams are the product-level vocabulary. Exact state models are specified in the LLD.

## 11. Open questions

| ID | Question | Owner |
|---|---|---|
| — | None open. OQ-00-1 resolved 2026-10-31 in [HLD §3](../high-level-design/high-level-design.md#3-requirements). | — |

Each domain document lists its own open questions.
