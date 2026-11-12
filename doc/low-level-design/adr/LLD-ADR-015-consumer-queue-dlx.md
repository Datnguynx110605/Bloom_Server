# LLD-ADR-015: Consumer queues dead-letter at the delivery limit

[Docs](../../README.md) › [Low-level design](../README.md) › [Decision records](README.md)

| | |
|---|---|
| **Status** | Accepted |
| **Date** | 2026-11-12 |
| **Deciders** | Project owner |
| **Related** | LLD-ADR-002, HLD-ADR-019, LLD-ADR-009, [00-reliability.md §6](../00-reliability.md#6-broker-topology-lld-adr-002) |

## Context
Consumer queues are quorum queues. A quorum queue drops a message once it has been delivered more times than its delivery limit (default 20) and the queue has no dead-letter target. Bloom's retry logic counts attempts itself (`x-attempt`, 5 tries), but a message NACKed with requeue is not counted that way: during a consumer pause on the service's own database outage (HLD-ADR-019) the same message is redelivered every time consuming resumes and fails again, and after repeated consumer crashes it is redelivered too. Past the limit the broker deletes the message silently, with no DLQ copy and no alert.

## Decision drivers
- No lost message (BI-05 spirit: reliability guarantees of HLD-ADR-007).
- A message dropped by the broker must be visible and replayable like any other dead letter (LLD-ADR-009).
- No change to the retry logic or the message envelope.

## Considered options
1. Leave as is (the broker drops the message at the limit)
2. Raise or disable the delivery limit
3. Dead-letter the consumer queue to `<queue>.dlq`

## Decision
Chosen: **dead-letter to `<queue>.dlq`**. Consumer queues get `x-dead-letter-exchange = ""` and `x-dead-letter-routing-key = <queue>.dlq`. The default delivery limit (20) stays.

## Pros and cons of the options
### Leave as is
- ✅ No change.
- ❌ A message can vanish with no trace after a long outage or repeated crashes.

### Raise or disable the delivery limit
- ✅ No message dropped at 20.
- ❌ A poison message that is requeued forever is never surfaced; the limit is the safety net that stops it.

### Dead-letter to the DLQ
- ✅ A message over the limit lands in the DLQ, where the alert and the replay tool of LLD-ADR-009 already work.
- ✅ Uses the topology that exists (the DLQ is declared per consumer queue, LLD-ADR-002).
- ❌ Existing consumer queues must be re-declared: RabbitMQ rejects changed arguments on a live queue.
- ❌ Such messages carry the broker's `x-death` header instead of `x-failure-reason` and `x-failed-at`.

## Consequences
- ✅ No message is lost at the delivery limit; it is replayable through LLD-ADR-009.
- ➖ The DLQ admin service already treats missing `x-failure-reason` / `x-failed-at` as empty strings, so listing and replay work unchanged.
- ➖ Existing queues must be re-declared. For local development, remove the RabbitMQ volume.

## Links
- [00-reliability.md §6](../00-reliability.md#6-broker-topology-lld-adr-002)
- [LLD-ADR-002](LLD-ADR-002-rabbitmq-topology.md)
- [LLD-ADR-009](LLD-ADR-009-dlq-admin-endpoint.md)
- [HLD-ADR-019](../../high-level-design/adr/HLD-ADR-019-consumers-pause-during-own-db-outage.md)
