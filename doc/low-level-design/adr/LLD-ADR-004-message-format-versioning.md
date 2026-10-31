# LLD-ADR-004: JSON messages with a standard envelope, additive changes, versioned types

| | |
|---|---|
| **Status** | Accepted |
| **Date** | 2026-10-31 |
| **Deciders** | Project owner |
| **Related** | [00-reliability.md §5](../00-reliability.md#5-message-contracts) |

## Context
Messages cross service boundaries and outlive deployments. They are inspected in the RabbitMQ UI and the DLQ tool. Producers and consumers deploy independently.

## Decision drivers
- Debuggable messages.
- Independent deployments without breaking consumers.

## Considered options
- **Format:** JSON + shared C# contracts · Protobuf
- **Evolution:** additive + versioned types · additive only

## Decision
Chosen: **JSON** with a standard envelope (§5.2); payload types as C# records in one contracts package per producer. **Additive changes only** within a version; consumers ignore unknown fields; a breaking change creates `…v<N+1>`, published alongside the old version until consumers move.

## Pros and cons of the options
### JSON
- ✅ Readable in the broker UI and DLQ tool.
- ❌ Larger and untyped on the wire.

### Protobuf
- ✅ Typed, compact, same language as the APIs.
- ❌ Unreadable without decoding.

### Versioned types
- ✅ A path for breaking changes.
- ❌ Dual publishing during migration.

## Consequences
- ✅ Producers and consumers deploy independently.
- ❌ Contracts packages must be versioned and shared via the monorepo.

## Links
- [00-reliability.md §5](../00-reliability.md)
