# HLD-ADR-015: OpenTelemetry with Grafana, Tempo, Prometheus and Loki

| | |
|---|---|
| **Status** | Accepted |
| **Date** | 2026-10-31 |
| **Deciders** | Project owner |
| **Related** | QA-01 … QA-05, [hld.md §9.3](../hld.md#93-observability) |

## Context
A single checkout crosses gateway, order, catalog, launch or inventory, payment, the broker and consumers. Debugging and SLO verification need traces across all of them.

## Decision drivers
- Follow one request across sync and async hops.
- Measure SLOs.
- Free and self-hostable.

## Considered options
1. OpenTelemetry + Grafana stack (Tempo, Prometheus, Loki)
2. OpenTelemetry + Jaeger + ELK
3. Logs and metrics only

## Decision
Chosen: **OpenTelemetry** in every service, with trace context propagated through **gRPC metadata and RabbitMQ message headers**, exported to **Tempo, Prometheus and Loki**, viewed in **Grafana**.

## Pros and cons of the options
### Grafana stack
- ✅ One UI for traces, metrics and logs; light to run.

### Jaeger + ELK
- ❌ Elasticsearch is heavy locally.

### No tracing
- ❌ Can't follow a request across services.

## Consequences
- ✅ End-to-end traces including the outbox and consumers.
- ❌ Every message must carry trace headers (handled by the shared plumbing library).

## Links
- [hld.md §9.3](../hld.md#93-observability)
