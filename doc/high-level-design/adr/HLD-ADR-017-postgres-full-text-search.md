# HLD-ADR-017: Catalog search uses PostgreSQL full-text search

| | |
|---|---|
| **Status** | Accepted |
| **Date** | 2026-10-31 |
| **Deciders** | Project owner |
| **Related** | CAT-R09, QA-02 |

## Context
Customers search by keyword, filter by category, price and options, and sort by price, newest or rating (CAT-R09). The catalog is a few hundred products and a few thousand SKUs.

## Decision drivers
- p95 < 150 ms for catalog reads (QA-02).
- No extra system or sync pipeline unless needed.

## Considered options
1. PostgreSQL full-text search
2. Elasticsearch / OpenSearch

## Decision
Chosen: **PostgreSQL full-text search** with indexed filters in catalog's database, with catalog's Redis as a read cache.

## Pros and cons of the options
### PostgreSQL
- ✅ No extra cluster; always consistent with the catalog.
- ❌ No typo tolerance; weaker relevance tuning.

### Elasticsearch
- ✅ Typo tolerance, facets, relevance.
- ❌ A cluster plus a sync pipeline that can lag. Overkill at this size.

## Consequences
- ✅ Simple and consistent.
- ⚠️ Revisit if the catalog grows by orders of magnitude or fuzzy search becomes a requirement.

## Links
- [hld.md §5](../hld.md#5-components)
