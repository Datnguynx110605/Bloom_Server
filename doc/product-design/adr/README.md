# Product-Design Decision Records

Decision records for business and product decisions that are **hard to reverse or shape the architecture**. Simple business parameters (for example the 14-day return window) live in the domain documents with a short rationale instead.

## Conventions

| | |
|---|---|
| **ID format** | `PD-ADR-NNN` (product design). HLD and LLD use `HLD-ADR-NNN` and `LLD-ADR-NNN` in their own `adr/` folders. |
| **File name** | `PD-ADR-NNN-short-kebab-title.md` |
| **Home** | A decision lives in the most upstream folder that drives it. Downstream documents link to it and never copy it. |
| **Statuses** | `Proposed` → `Accepted` → (`Deprecated` \| `Superseded by <ID>`) |
| **Immutability** | Once accepted, an ADR is not rewritten. A changed decision gets a new ADR that supersedes the old one. |

## Index

| ID | Title | Status | Date |
|---|---|---|---|
| [PD-ADR-001](PD-ADR-001-single-vendor-b2c-single-warehouse.md) | Single-vendor B2C, single warehouse | Accepted | 2026-10-31 |
| [PD-ADR-002](PD-ADR-002-us-market-flat-sales-tax.md) | US market with a flat sales-tax rate | Accepted | 2026-10-31 |
| [PD-ADR-003](PD-ADR-003-charge-at-checkout-online-only.md) | Charge in full at checkout, online payments only | Accepted | 2026-10-31 |
| [PD-ADR-004](PD-ADR-004-reserve-stock-at-checkout.md) | Reserve stock at checkout start, with a timeout | Accepted | 2026-10-31 |
| [PD-ADR-005](PD-ADR-005-launch-allocation-waiting-room.md) | Launch: fixed allocation, waiting room, per-launch purchase limits | Accepted | 2026-10-31 |
| [PD-ADR-006](PD-ADR-006-per-product-serial-tracking.md) | Serial tracking per product flag; returned units never resold | Accepted | 2026-10-31 |
| [PD-ADR-007](PD-ADR-007-coupon-allocation-and-holding.md) | Coupon discount split per line; coupon uses held at checkout | Accepted | 2026-10-31 |
| [PD-ADR-008](PD-ADR-008-account-required-staff-mfa.md) | Account required to buy; staff invited with required MFA | Accepted | 2026-10-31 |

## Template (MADR-style)

Copy this for every new ADR, in any folder.

```markdown
# <PREFIX>-ADR-NNN: <Decision title, as a statement>

| | |
|---|---|
| **Status** | Proposed \| Accepted \| Deprecated \| Superseded by <ID> |
| **Date** | YYYY-MM-DD |
| **Deciders** | <who decided> |
| **Related** | <rule IDs, other ADRs, documents> |

## Context
<The problem and the forces at play. Facts only, no solution yet.>

## Decision drivers
- <Requirement, constraint or quality goal that decides between options>

## Considered options
1. <Option A>
2. <Option B>

## Decision
Chosen: **<Option>**, because <reason tied to the drivers>.

## Pros and cons of the options
### <Option A>
- ✅ <pro>
- ❌ <con>

### <Option B>
- ✅ <pro>
- ❌ <con>

## Consequences
- ✅ <positive consequence>
- ❌ <negative consequence or cost accepted>
- ⚠️ <follow-up work or risk this creates>

## Links
- <Related documents and rules>
```
