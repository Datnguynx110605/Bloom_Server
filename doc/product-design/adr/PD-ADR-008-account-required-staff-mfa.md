# PD-ADR-008: Require an account to buy; invite staff and require MFA

| | |
|---|---|
| **Status** | Accepted |
| **Date** | 2026-10-31 |
| **Deciders** | Project owner |
| **Related** | CHK-R01, LCH-R06, ACC-R01–R12, BI-06 |

## Context
Purchase limits, verified-buyer reviews and returns all need a known customer identity. Staff can issue refunds and change prices, so a stolen staff login does more damage than a stolen customer login.

## Decision drivers
- Enforce per-account launch limits.
- Identity for reviews, returns and order history.
- Strong protection and separation of duties for staff.

## Considered options
**Customers**
1. Account required
2. Guest checkout allowed
3. Guest checkout except pre-orders

**Customer sign-in:** email + password · + Google · + MFA

**Staff:** Admin-invited + required MFA · Admin-invited, no MFA · shared customer login with a role flag

**Roles:** fixed · configurable · single admin role

## Decision
Chosen:
- **An account with a verified email is required to buy** and to join a waiting room. Sign-in with **email + password or Google**.
- **Staff accounts are separate, invited by an Admin, and require TOTP MFA.** They can be deactivated immediately, and all their data-changing actions are audited.
- **Fixed roles:** Admin, Catalog manager, Warehouse, Support.

## Pros and cons of the options
### Account required
- ✅ Simpler limits, reviews, returns and history.
- ❌ Friction at checkout.

### Guest checkout
- ✅ Less friction.
- ❌ Order lookup by token, guest-to-account linking, weak launch limits.

### Google sign-in
- ✅ Less friction. Shows OAuth/OIDC integration.
- ❌ Needs account-linking rules. Decided: auto-link when Google reports the email as verified; pre-hijacking is impossible because every Bloom customer email is verified before the account exists (ACC-R01, ACC-R02).

### Staff MFA required
- ✅ Protects refund and price powers.
- ❌ Extra setup for staff.

### Fixed roles
- ✅ Separation of duties without building a permission manager.
- ❌ Changing permissions requires a release.

## Consequences
- ✅ Every order, review and return belongs to a known account.
- ❌ No guest buyers.
- ⚠️ Staff hold exactly one role, and an email can belong to only one account (customer or staff). So staff who shop need a separate email (ACC-R08, ACC-R14).

## Links
- [10-accounts-access](../10-accounts-access.md)
