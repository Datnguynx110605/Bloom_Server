# 09 — Reviews

[Docs](../README.md) › [Product design](00-overview.md)

| | |
|---|---|
| **Status** | Approved (2026-10-31) |
| **Last updated** | 2026-10-31 |
| **Related ADRs** | — |

## 1. Purpose and scope

Verified buyers rate and review products. Kept deliberately thin.

**In scope:** verified-buyer reviews, star rating and text, post-moderation, average rating and count per product.

**Out of scope:** review photos, helpful votes, staff replies, pre-moderation, per-variant reviews.

## 2. Business rules

| ID | Rule |
|---|---|
| REV-R01 | Only **verified buyers** can review: customers who have had a delivered order containing the product. Returning the product doesn't remove eligibility. |
| REV-R02 | Reviews are **per product**, not per SKU. |
| REV-R03 | One review per product per customer. The customer can edit or delete it, and can write a new one after deleting. |
| REV-R04 | A review has a **required** rating of 1–5 stars and **optional** text of up to 2,000 characters. |
| REV-R05 | Reviews are visible as soon as they're posted. |
| REV-R06 | Support can hide abusive reviews. Hidden reviews don't count in the product's average rating or review count. The author is not notified. |
| REV-R07 | Each product shows its average rating and review count (CAT-R10). |
| REV-R08 | When a customer deletes their account, their reviews stay visible, with the author shown as "Former customer" (ACC-R06). |

## 3. Roles

| Action | Role |
|---|---|
| Write, edit and delete own review | Customer (verified buyer) |
| Hide or unhide a review | Support, Admin |

## 4. Open questions

| ID | Question |
|---|---|
| — | None open. OQ-09-1 to OQ-09-3 resolved 2026-10-31 (REV-R03, REV-R04, REV-R06). |
