# 02 — Catalog

[Docs](../README.md) › [Product design](00-overview.md)

| | |
|---|---|
| **Status** | Approved (2026-10-31) |
| **Last updated** | 2026-10-31 |
| **Related ADRs** | [PD-ADR-006](adr/PD-ADR-006-per-product-serial-tracking.md) |

## 1. Purpose and scope

The catalog defines what Bloom sells and at what price.

**In scope:** categories, products, option types, predefined SKUs, prices, the serialized flag, warranty plans, search and browsing, and the rating summary shown on products.

**Out of scope:** configure-to-order builds, automatic sale prices, bundles, recommendations, wishlists.

## 2. Business rules

| ID | Rule |
|---|---|
| CAT-R01 | Each product belongs to one category. Categories have **two levels**: a top level (for example Mac) and an optional sub-level (for example MacBook Pro). |
| CAT-R02 | A product defines its option types (for example color, storage, chip, memory). |
| CAT-R03 | Only combinations created by staff exist as SKUs. Customers can't configure combinations that aren't defined (no configure-to-order). |
| CAT-R04 | Each SKU has its own price in USD and its own stock. |
| CAT-R05 | Each product has a **serialized** flag that applies to all its SKUs. Serialized SKUs track each unit by serial number; others track quantity only. |
| CAT-R06 | A product is either a **physical product** or a **warranty plan**. |
| CAT-R07 | A warranty plan is linked to the physical products it can cover, has a price, and has a coverage length set per plan. |
| CAT-R08 | A price change applies to checkouts started after the change. A checkout in progress keeps the price locked at checkout start (see CHK-R04). |
| CAT-R09 | Customers can search by keyword, filter by category, price and option values, and sort by price, newest or rating. |
| CAT-R10 | Each product shows its average rating and review count, from visible reviews only (see [09-reviews](09-reviews.md)). |
| CAT-R11 | Launch SKUs are catalog SKUs. Their launch stage controls whether they're visible and buyable (see [04-launch](04-launch.md)). After *Released*, they follow the regular lifecycle as *Active*. |
| CAT-R12 | Regular products have a lifecycle: **Draft** (not visible) → **Active** (visible and buyable) → **Archived** (hidden from browsing and search, not buyable, kept for order history, reviews and warranty lookup). Catalog manager can move an archived product back to *Active*; its reviews and history carry over. |
| CAT-R13 | A product that has been sold is never hard-deleted. Stock left on an archived product stays in inventory for Warehouse to adjust. |
| CAT-R14 | The serialized flag can change only while the product has never had stock. After the first goods receipt it is **locked**. A mistake is fixed by archiving the product and creating a new one. |
| CAT-R15 | The catalog is **public**: browsing, search, product pages and reviews need no sign-in. Cart, checkout, waiting room, writing reviews and account pages require sign-in. |

## 3. Roles

| Action | Role |
|---|---|
| Create and edit products, SKUs, prices, warranty plans | Catalog manager, Admin |
| Browse and search | Anyone, including visitors who aren't signed in |

## 4. Open questions

| ID | Question |
|---|---|
| — | None open. OQ-02-1 to OQ-02-5 resolved 2026-10-31 (CAT-R01, CAT-R12 to CAT-R15). |
