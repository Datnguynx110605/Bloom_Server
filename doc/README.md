# Bloom — Documentation

Bloom is an independent online store selling Apple devices to US consumers, built as a backend portfolio project. This folder holds its complete design, in three layers, each one refining the one above it.

| Layer | Answers | Entry point | Decision records |
|---|---|---|---|
| **Product design** | What Bloom does: business domain, rules, scope | [product-design/00-overview.md](product-design/00-overview.md) | [PD-ADR index](product-design/adr/README.md) |
| **High-level design** | How the system is shaped: services, data ownership, interfaces, quality targets | [high-level-design/high-level-design.md](high-level-design/high-level-design.md) | [HLD-ADR index](high-level-design/adr/README.md) |
| **Low-level design** | How each component is built: data models, APIs, flows, edge cases, tests | [low-level-design/README.md](low-level-design/README.md) | [LLD-ADR index](low-level-design/adr/README.md) |

## Reading order

1. [Product overview](product-design/00-overview.md) and [glossary](product-design/01-glossary.md) (≈ 10 min).
2. [High-level design](high-level-design/high-level-design.md) (≈ 15 min).
3. [Reliability building blocks](low-level-design/00-reliability.md), then the service LLD you need ([index](low-level-design/README.md)).
4. Domain documents ([02-catalog](product-design/02-catalog.md) … [11-notifications](product-design/11-notifications.md)) when you need the exact business rules behind a design.

## Document map

```
doc/
├── README.md                     ← you are here
├── product-design/
│   ├── 00-overview.md            vision, goals, invariants, scope, actors
│   ├── 01-glossary.md            domain terms
│   ├── 02-catalog.md … 11-notifications.md   one file per domain area
│   └── adr/                      PD-ADR-001 … 009
├── high-level-design/
│   ├── high-level-design.md      architecture, services, data, interfaces, cross-cutting
│   └── adr/                      HLD-ADR-001 … 020
└── low-level-design/
    ├── README.md                 component index
    ├── 00-reliability.md         shared outbox / inbox / idempotency / concurrency
    ├── 01-launch.md … 09-notification.md   one file per service
    └── adr/                      LLD-ADR-001 … 015
```

## Conventions

### Identifiers

| Prefix | Meaning | Defined in |
|---|---|---|
| `G1` … `G4` | Product goals | [00-overview §2](product-design/00-overview.md#2-goals) |
| `BI-01` … | Business invariants (testable success criteria) | [00-overview §3](product-design/00-overview.md#3-business-invariants-success-criteria) |
| `CAT-R`, `INV-R`, `LCH-R`, `CHK-R`, `ORD-R`, `RET-R`, `WAR-R`, `PRM-R`, `REV-R`, `ACC-R`, `NTF-R` | Business rules per domain area | The matching product-design document |
| `HG-1` … | HLD goals | [high-level-design §1](high-level-design/high-level-design.md#1-context-and-goals) |
| `QA-01` … | Quality attributes with targets | [high-level-design §3.2](high-level-design/high-level-design.md#32-quality-attributes) |
| `R-01` …, `HA-01` … | HLD risks, assumptions | [high-level-design §11, §12](high-level-design/high-level-design.md#11-risks) |
| `PD-ADR-`, `HLD-ADR-`, `LLD-ADR-` | Decision records per layer | Each layer's `adr/README.md` |
| `OQ-…` | Open questions (all resolved or deferred with a pointer) | End of each document |
| Two-letter IDs in an LLD (`LR-01`, `IR-01`, `OR-01`, …) | Component requirements | Section 2 of each LLD |

### Rules for changing the documents

- **Upstream first.** A product change updates product design before HLD and LLD; an LLD that must contradict the HLD changes the HLD first.
- **Decisions live in one place.** An ADR sits in the most upstream layer that drives it; other documents link to it, never copy it. Accepted ADRs are superseded by new ones rather than rewritten.
- **Stable IDs.** Rule, requirement and ADR IDs are never renumbered; new items take the next free number.
- **Status.** Every document carries `Draft — pending owner review` or `Approved (date)` in its header table.
