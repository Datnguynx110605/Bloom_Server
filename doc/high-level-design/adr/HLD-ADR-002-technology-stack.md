# HLD-ADR-002: Use .NET, PostgreSQL, Redis and RabbitMQ

| | |
|---|---|
| **Status** | Accepted |
| **Date** | 2026-10-31 |
| **Deciders** | Project owner |
| **Related** | HLD-ADR-006, 007, 009, 012 |

## Context
The stack should match the roles the owner targets and directly support the chosen patterns: gRPC, row versioning, `SKIP LOCKED` relays, dead-letter queues.

## Decision drivers
- Hiring signal for the owner's target roles.
- First-class gRPC and transactional data access.
- Native support for the reliability patterns.

## Considered options
- **Language:** .NET (ASP.NET Core) · Java (Spring Boot) · Go · Node.js (TypeScript)
- **Database:** PostgreSQL · SQL Server · mixed per service
- **Broker:** RabbitMQ · Kafka
- **Cache:** Redis

## Decision
Chosen: **.NET / ASP.NET Core**, **PostgreSQL**, **Redis**, **RabbitMQ**.

## Pros and cons of the options
### .NET
- ✅ First-class gRPC and JSON transcoding, EF Core optimistic concurrency, strong performance.
- ❌ Smaller open-source microservices ecosystem than Java.

### PostgreSQL
- ✅ `FOR UPDATE SKIP LOCKED`, JSONB, full-text search, strong transactions, free.
- ❌ SQL Server's native `rowversion` type would map more directly to the row-versioning pattern (Postgres uses a version column or `xmin`).

### RabbitMQ
- ✅ Native dead-letter exchanges, per-message ack, delayed retries. Enough throughput for Bloom.
- ❌ No ordering across competing consumers, so consumers must tolerate reordering.

### Kafka
- ✅ Ordering per key, replay, high throughput.
- ❌ Retry topics and DLQ have to be built; heavier to run.

## Consequences
- ✅ Each pattern maps to a native feature.
- ❌ Consumers need state-machine guards against reordering.

## Links
- [hld.md §9.2](../hld.md#92-reliability-patterns)
