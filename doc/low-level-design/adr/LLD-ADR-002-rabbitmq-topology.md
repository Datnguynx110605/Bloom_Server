# LLD-ADR-002: Per-producer topic exchanges, per-consumer queues with their own retry queues and DLQ

| | |
|---|---|
| **Status** | Accepted |
| **Date** | 2026-10-31 |
| **Deciders** | Project owner |
| **Related** | HLD-ADR-007, [00-reliability.md §6](../00-reliability.md#6-broker-topology-lld-adr-002) |

## Context
Messages must be routed to the right consumers, retried with delays (5 attempts in total), and dead-lettered without one consumer's failures affecting another. The relay must know a message is stored before marking it sent.

## Decision drivers
- Isolation between consumers.
- Clear ownership per producer.
- No message loss between relay and broker.

## Considered options
- **Exchanges:** topic exchange per producer · one shared exchange
- **Relay publishing:** publisher confirms · fire-and-forget

## Decision
Chosen: **`<producer>.events`** topic exchanges (routing key = message type) and **`<target>.commands`** direct exchanges; one quorum queue per consumer handler, with its own retry queues (`1s`, `5s`, `30s`, `2m`: TTL, dead-lettered back to the main queue) and its own DLQ. Every publish (relay, retry, DLQ) uses **publisher confirms** with persistent messages.

## Pros and cons of the options
### Per-producer exchanges
- ✅ Ownership visible in the broker; per-service permissions possible.
- ❌ More exchanges.

### Shared exchange
- ❌ No producer boundary at the broker.

### Publisher confirms
- ✅ A row is marked sent only once the broker has it.
- ❌ Slightly lower publish throughput (well within RL-07).

### Fire-and-forget
- ❌ Messages can be lost on broker failure, which defeats the outbox.

## Consequences
- ✅ A failing consumer only fills its own retry queues and DLQ.
- ❌ Four retry queues + one DLQ per consumer queue.

## Links
- [00-reliability.md §6, §7.2, §7.3](../00-reliability.md)
