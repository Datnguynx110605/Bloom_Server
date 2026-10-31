# 10 — Accounts and Access

| | |
|---|---|
| **Status** | Approved (2026-10-31) |
| **Last updated** | 2026-10-31 |
| **Related ADRs** | [PD-ADR-008](adr/PD-ADR-008-account-required-staff-mfa.md) |

## 1. Purpose and scope

Customer accounts identify buyers. Staff accounts give controlled back-office access under fixed roles, with every change audited.

**In scope:** customer sign-up and sign-in, Google sign-in, email verification, password reset, address book, order history, account deletion, staff invitation, staff MFA, fixed roles, audit log.

**Out of scope:** guest checkout, configurable roles, customer MFA, SSO for staff.

## 2. Business rules

### Customer accounts

| ID | Rule |
|---|---|
| ACC-R01 | Customers sign up by verifying their email with a 6-digit code **first**; only then do they set a password and the account is created. No unverified customer account can exist. |
| ACC-R02 | Customers can also sign in with Google. If a Bloom customer account with the same email exists, Google is linked automatically when Google reports the email as verified (every Bloom customer email was verified at sign-up, ACC-R01). Staff emails can't use Google. |
| ACC-R03 | Customers can reset a forgotten password by email. |
| ACC-R04 | Customers keep an address book of US shipping addresses. |
| ACC-R05 | Customers can see their order history. |
| ACC-R06 | Deleting an account anonymizes personal data. Orders are kept for accounting. Reviews stay visible with the author shown as "Former customer" (REV-R08). |
| ACC-R13 | Account deletion is **refused** while any order is unsettled: not yet delivered, an open return request, or still within the 14-day return window. The customer is told why and when they can try again. |

### Staff accounts

| ID | Rule |
|---|---|
| ACC-R07 | Staff accounts are separate from customer accounts. There is no staff self sign-up. |
| ACC-R08 | An Admin invites staff by email and assigns **exactly one** role. |
| ACC-R14 | An email address can belong to only one account, customer **or** staff. Staff who want to shop at Bloom use a different email. |
| ACC-R09 | Staff sign in with email, password and a **required** authenticator-app code (TOTP). |
| ACC-R10 | An Admin can deactivate a staff account, which takes effect immediately. |
| ACC-R11 | Every staff action that changes data is written to the audit log: who, what, when (BI-06). |

### Roles

| ID | Rule |
|---|---|
| ACC-R12 | Roles are fixed: **Admin**, **Catalog manager**, **Warehouse**, **Support**. Permissions are defined by the product, not configured at runtime. |

## 3. Permission matrix

| Capability | Admin | Catalog manager | Warehouse | Support |
|---|:-:|:-:|:-:|:-:|
| Manage staff accounts | ✓ | | | |
| Products, SKUs, prices, warranty plans | ✓ | ✓ | | |
| Launches (time, allocation, limits) | ✓ | ✓ | | |
| Coupons | ✓ | ✓ | | |
| View stock levels | ✓ | ✓ | ✓ | ✓ |
| View stock ledger | ✓ | | ✓ | |
| Goods receipt, stock adjustments | ✓ | | ✓ | |
| Pick, pack, scan serials | ✓ | | ✓ | |
| Inspect returns | ✓ | | ✓ | |
| View and cancel orders | ✓ | | | ✓ |
| Return decisions, refunds, lost-package decisions | ✓ | | | ✓ |
| Hide reviews | ✓ | | | ✓ |
| View audit log | ✓ | | | |

## 4. Open questions

| ID | Question |
|---|---|
| — | None open. OQ-10-1 to OQ-10-6 resolved 2026-10-31 (ACC-R02, ACC-R06, ACC-R08, ACC-R13, ACC-R14, §3). |
