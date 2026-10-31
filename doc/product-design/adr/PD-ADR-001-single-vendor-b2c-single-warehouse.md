# PD-ADR-001: Bloom is a single-vendor B2C store with a single warehouse

[Docs](../../README.md) › [Product design](../00-overview.md) › [Decision records](README.md)

| | |
|---|---|
| **Status** | Accepted |
| **Date** | 2026-10-31 |
| **Deciders** | Project owner |
| **Related** | INV-R01, [00-overview](../00-overview.md) |

## Context
Bloom is a backend portfolio project. The business model decides who sells, who owns stock and how money flows, so it shapes the whole domain model. The scope must leave room to go deep on hard backend problems instead of spreading effort across many shallow features.

## Decision drivers
- Depth over breadth: concurrency, payment correctness and order consistency should get the effort.
- A domain small enough to finish and document well.
- Realistic for the product vertical (Apple devices).

## Considered options
1. Single-vendor B2C
2. Multi-vendor marketplace
3. Multi-tenant SaaS
4. B2B wholesale

Separately, for locations: one warehouse, or multiple warehouses / store pickup.

## Decision
Chosen: **single-vendor B2C with one warehouse**, because it keeps the domain small and focuses effort on inventory, payment and order correctness.

## Pros and cons of the options
### Single-vendor B2C
- ✅ Smallest domain. No sellers, payouts or tenants.
- ✅ Leaves effort for the hard problems.
- ❌ Fewer "platform" talking points than a marketplace.

### Multi-vendor marketplace
- ✅ Strong resume story (split orders, payouts, commissions).
- ❌ Much larger scope: seller onboarding, split fulfillment, payouts.

### Multi-tenant SaaS
- ✅ Tenant isolation is an interesting topic.
- ❌ Very large scope. Pulls focus from commerce correctness.

### B2B wholesale
- ❌ Quotes, invoices, credit terms. Narrower audience, uncommon in portfolios.

### Multiple warehouses
- ✅ Richer inventory model.
- ❌ Adds stock per location, routing and transfers without improving the core story.

## Consequences
- ✅ One stock pool per SKU. Simpler reservation and ledger model.
- ❌ No marketplace, payout or multi-location features to show.
- ⚠️ Multiple warehouses are listed as future scope. Adding them later changes the inventory model.

## Links
- [03-inventory](../03-inventory.md)
