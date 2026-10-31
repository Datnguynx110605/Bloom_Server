# 11 — Notifications

[Docs](../README.md) › [Product design](00-overview.md)

| | |
|---|---|
| **Status** | Approved (2026-10-31) |
| **Last updated** | 2026-10-31 |
| **Related ADRs** | — |

## 1. Purpose and scope

Bloom tells customers about events on their account and orders.

**In scope:** transactional email, in-app notifications for signed-in customers.

**Out of scope:** SMS, push notifications, marketing email, staff notifications.

## 2. Business rules

| ID | Rule |
|---|---|
| NTF-R01 | Channels are **email** and **in-app**. |
| NTF-R02 | In-app notifications are listed for the signed-in customer with read / unread state. |
| NTF-R03 | Every Bloom email is transactional. There is no marketing email, and customers can't opt out. |
| NTF-R04 | Order, shipping, return and refund events go to **both** email and in-app. Account-security events and staff invitations are **email only**, because the person may not be able to sign in yet. |
| NTF-R05 | In-app notifications are deleted after **90 days**. Order history remains the permanent record. |
| NTF-R06 | Admission from the waiting room is shown on the waiting-room page only. No email or in-app notification is sent. |

## 3. Notification events

| Event | Email | In-app | Source |
|---|:-:|:-:|---|
| Email verification, password reset | ✓ | | ACC-R01, ACC-R03 |
| Order confirmed (paid) | ✓ | ✓ | CHK-R12 |
| Checkout expired / order cancelled | ✓ | ✓ | CHK-R14, ORD-R06, ORD-R14 |
| Pre-order delayed (stock short) | ✓ | ✓ | INV-R11, LCH-R20 |
| Order shipped (with tracking) | ✓ | ✓ | ORD-R09 |
| Order delivered | ✓ | ✓ | ORD-R10 |
| Delivery failed (returned to sender / lost) | ✓ | ✓ | ORD-R12, ORD-R13 |
| Return label issued, return request expired | ✓ | ✓ | RET-R03, RET-R15 |
| Return received, return decision | ✓ | ✓ | RET-R04, RET-R05 |
| Refund issued | ✓ | ✓ | RET-R11, ORD-R06, ORD-R17 |
| Staff invitation | ✓ | | ACC-R08 |

## 4. Open questions

| ID | Question |
|---|---|
| — | None open. OQ-11-1 to OQ-11-4 resolved 2026-10-31 (NTF-R03 to NTF-R06). |
