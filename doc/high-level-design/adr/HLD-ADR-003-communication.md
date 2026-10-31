# HLD-ADR-003: gRPC internally, RabbitMQ for async, REST/JSON at the edge via JSON transcoding

| | |
|---|---|
| **Status** | Accepted |
| **Date** | 2026-10-31 |
| **Deciders** | Project owner |
| **Related** | [hld.md §4, §7](../hld.md) |

## Context
Some interactions need an immediate answer (checkout holds, prices). Others must happen eventually and survive outages (notifications, compensations). Browser clients need a plain HTTP API.

## Decision drivers
- Immediate answers where the customer waits.
- Throughput and resilience for everything else.
- One API contract per service.

## Considered options
- **Client API:** REST/JSON → gRPC transcoding · REST in each service · GraphQL at the gateway
- **Internal:** gRPC (sync) + broker (async)

## Decision
Chosen: **gRPC for synchronous internal calls**, **RabbitMQ for asynchronous events and commands**, and **REST/JSON for clients**. Each service defines its API once in `.proto`. HTTP/JSON endpoints are generated from `.proto` annotations by ASP.NET Core gRPC JSON transcoding, hosted inside the service. The YARP gateway proxies client HTTP to them.

## Pros and cons of the options
### JSON transcoding
- ✅ Services implement only gRPC. One contract. Browsers get JSON.
- ❌ JSON shape follows protobuf conventions.

### REST in each service
- ❌ Two API surfaces per service.

### GraphQL
- ❌ An extra schema layer with resolver fan-out. Little backend value.

## Consequences
- ✅ One `.proto` per service is the single contract.
- ❌ Transcoding runs in the services, not the gateway (YARP can't transcode), so the gateway forwards HTTP to services.

## Links
- [hld.md §4](../hld.md#4-architecture-overview)
