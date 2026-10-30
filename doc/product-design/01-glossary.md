# Glossary

| | |
|---|---|
| **Status** | Approved (2026-10-31) |
| **Last updated** | 2026-10-31 |

Terms are grouped by domain area. Every document uses these terms with exactly these meanings.

## Catalog

| Term | Definition |
|---|---|
| **Product** | A sellable item as customers see it, for example "iPhone 17 Pro". Has option types and one or more SKUs. Belongs to a category. |
| **Category** | A grouping of products used for browsing and filtering, for example "iPhone" or "Accessories". |
| **Option type** | A dimension along which a product varies, for example color, storage, chip, memory. |
| **Option value** | One value of an option type, for example "256 GB" or "Black Titanium". |
| **SKU** | One predefined combination of option values of a product. The unit of pricing and stock. Also called a *variant*. |
| **Physical product** | A product that is stocked, shipped and returned. |
| **Warranty plan** | A non-physical product that extends coverage of an eligible physical product for a fixed length. |
| **Serialized product** | A physical product whose units are tracked individually by serial number. Set by a per-product flag. |

## Inventory

| Term | Definition |
|---|---|
| **Serial unit** | One physical unit of a serialized SKU, identified by its serial number (and IMEI for cellular devices). |
| **On-hand** | Quantity of a SKU physically in the warehouse and sellable. |
| **Reserved** | Quantity of a SKU held for checkouts in progress. |
| **Available** | On-hand minus reserved. What can still be sold. |
| **Reservation** | A time-limited hold of SKU quantity for one checkout. Created at checkout start, expires after 15 minutes unless payment succeeds. |
| **Goods receipt** | A Warehouse record of stock arriving: SKU and quantity, or scanned serials for serialized SKUs. |
| **Stock adjustment** | A manual correction of stock (for example damaged or lost in the warehouse), with a required reason code and note. |
| **Stock ledger** | The record of every stock change. Entries are never edited; corrections are new entries. |

## Launch

| Term | Definition |
|---|---|
| **Launch** | A scheduled release event grouping one or more launch SKUs, for example "iPhone 17 Pro family". Has one waiting room. |
| **Launch SKU** | A SKU sold through a launch, with a fixed pre-order allocation. |
| **Launch time** | When the launch moves to *Pre-order open* and admission from the waiting room begins. |
| **Pre-order allocation** | The fixed number of units of a launch SKU that can be sold as pre-orders. |
| **Waiting room** | The virtual queue for a launch. Opens 15 minutes before launch time. |
| **Admission pass** | Permission for a queued customer to start one checkout. Valid for 10 minutes. |
| **Release date** | When a launch moves to *Released* automatically and its SKUs start selling as regular SKUs. |
| **Purchase limit** | The maximum units of a launch product one account may buy, set per launch product by staff. |
| **Pre-order** | A paid order for launch SKUs whose stock may not have arrived yet. |

## Orders and money

| Term | Definition |
|---|---|
| **Cart** | A customer's saved list of items. Holds no stock. Shows live prices. |
| **Checkout** | The step that locks prices, discount, tax and shipping, reserves stock, holds the coupon use and takes payment. |
| **Order** | A customer's purchase. Created at checkout start in *Pending payment*. |
| **Regular order** | An order of regular (non-launch) items. |
| **Launch order** | An order containing only one launch's items and their warranty plans. |
| **Order line** | One SKU (or warranty plan) and quantity within an order. |
| **Paid amount** | What the customer actually paid for an order line after its share of the discount. The basis of every refund. |
| **Shipment** | A package sent by the carrier, with a label and tracking number. |
| **Standard / Express shipping** | The two shipping options. Standard is free; Express has a flat fee set by staff. |
| **Refund** | Money returned to the original payment method. |

## Returns and warranty

| Term | Definition |
|---|---|
| **Return request** | A customer's request to return one or more units of a delivered order within 14 days of delivery. |
| **Return label** | A prepaid carrier label issued by Bloom for a return. |
| **Inspection** | Warehouse's check of a returned unit, recording a result and notes. |
| **Return decision** | Support's outcome after inspection: full refund, partial refund or rejection. |
| **Returned unit** | A unit back from a customer. Held as non-sellable and never resold. |
| **Coverage** | The period a warranty plan covers a specific serial unit. Starts at delivery. |

## Promotions and reviews

| Term | Definition |
|---|---|
| **Coupon** | A code customers enter at checkout for a percent or fixed-amount discount. |
| **Coupon use** | One redemption of a coupon by one order. States: *held*, *consumed*, *released*, *restored*. |
| **Eligible line** | An order line a coupon may discount: a physical product, not a launch item, within the coupon's scope. |
| **Review** | A customer's 1–5 star rating and text for a product. |
| **Verified buyer** | A customer who has had a delivered order containing the product. |

## Accounts

| Term | Definition |
|---|---|
| **Customer account** | An account for buying. Signs in with email + password or Google. |
| **Staff account** | An account for the back-office. Invited by an Admin, requires MFA. |
| **Role** | One of the fixed staff roles: Admin, Catalog manager, Warehouse, Support. |
| **Audit log** | The record of every staff action that changes data. |
