# AGENTS.md

Instructions for any AI agent working in this repository.

## 1. The documentation decides

`doc/` is the approved design of Bloom. Build what it says, nothing more.

- Do not add behaviour, endpoints, messages, fields or rules the docs don't describe.
- Every design choice in code must trace back to a rule, requirement or ADR ID.
- The owner makes every decision. When in doubt, stop and ask.

## 2. Every session: read the core (≈ 3k tokens)

1. [doc/README.md](doc/README.md): layers, ID conventions, rules for changing documents.
2. The **Summary** section of [high-level-design.md](doc/high-level-design/high-level-design.md) (lines 12–18).

Then read only what the task needs (§3). Goal: understand the part of the system you touch, and how it connects to the rest, before writing code.

## 3. Reading route

| Task | Read |
|---|---|
| Build or change a service | Its LLD in full ([index](doc/low-level-design/README.md)), the [00-reliability](doc/low-level-design/00-reliability.md) sections it uses, and the ADRs it links |
| Shared reliability library | [00-reliability](doc/low-level-design/00-reliability.md) in full, [LLD-ADR-001 … 006](doc/low-level-design/adr/README.md) |
| Messages (naming, envelope, versioning) | 00-reliability §5; producer and consumer LLD §5 (Messages); HLD §7.2 |
| Broker topology, retries, DLQ | 00-reliability §6–7 |
| Errors returned by an API | 00-reliability §8; the service LLD §5 (Error reasons) |
| Concurrency, idempotency, locking | 00-reliability §7, §9; HLD §9.2 |
| gRPC call between services | HLD §7.1; caller and callee LLD §5 |
| Cross-service flow or saga | HLD §8.x for the flow; each participant's LLD §6 |
| Auth, tokens, roles, gateway routes | [06-identity](doc/low-level-design/06-identity.md), [07-api-gateway](doc/low-level-design/07-api-gateway.md), HLD §9.1 |
| External providers (Stripe, Shippo, email) | HLD §7.3; the owning LLD |
| Deployment, observability | HLD §9.3, §9.5; the service LLD §10 |
| Data ownership (who owns which table) | HLD §6; the service LLD §4 |
| Meaning of a domain term | [01-glossary](doc/product-design/01-glossary.md) |
| A business rule ID | The product document for its prefix (below) |
| Why a decision was made | The ADR it cites (`adr/` of that layer) |

**Rule prefix → product document** (`doc/product-design/`):
`CAT` 02-catalog · `INV` 03-inventory · `LCH` 04-launch · `CHK` 05-checkout-payment · `ORD` 06-orders-fulfillment · `RET`, `WAR` 07-returns-refunds · `PRM` 08-promotions · `REV` 09-reviews · `ACC` 10-accounts-access · `NTF` 11-notifications. `BI-`, `G` → 00-overview.

Every service LLD has the same sections: 1 Scope · 2 Requirements · 3 Components · 4 Data model · 5 Interfaces · 6 Behaviour · 7 Concurrency · 8 Edge cases · 9 Security · 10 Observability · 11 Test cases · 12 Open questions.

## 4. Read cheaply

- List headings first (`grep -n '^#' <file>`), then read only the line ranges you need.
- Find an ID with search (`grep -rn 'ORD-R13' doc`) instead of opening whole files.
- Follow links only when the current section doesn't answer the question.
- Don't re-read a file already read in this session.

## 5. When the route isn't enough

Go one layer up at a time: service LLD → HLD → product design. Read the whole documentation set (≈ 90k tokens) only when targeted reading still leaves a gap. Never fill a gap by guessing.

## 6. Before writing code

State in 3–5 lines what you are building and the design it rests on, citing IDs (e.g. `ORD-R13`, `HLD-ADR-006`, `OR-01`). If you can't cite them, you haven't read enough.

## 7. When the docs don't cover something

- **Internal details** (naming, file layout, private helpers, test structure): your call.
- **Anything visible outside the service or affecting the design** (APIs, messages, data model, rules, behaviour, dependencies): stop and ask the owner.

## 8. When docs and code disagree

1. Stop. Do not change the docs or work around the mismatch.
2. Report to the owner: where they disagree, which one is right in your view, and the exact edit you propose.
3. If the change contradicts an approved decision or affects the system design, start the report with **DESIGN CHANGE** and include a new ADR in the proposal. Follow the rules in [doc/README.md](doc/README.md#rules-for-changing-the-documents): upstream layer first, the ADR sits in the most upstream layer it affects, supersede accepted ADRs instead of rewriting them, IDs are never renumbered.
4. Edit only after the owner says yes.

## 9. Hard rule

**Never create, edit, move or delete anything under `doc/` without the owner's explicit approval for that change.**
