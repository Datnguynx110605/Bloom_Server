# LLD-ADR-003: Errors are gRPC status codes with a stable ErrorInfo reason

| | |
|---|---|
| **Status** | Accepted |
| **Date** | 2026-10-31 |
| **Deciders** | Project owner |
| **Related** | HLD-ADR-003, [00-reliability.md §8](../00-reliability.md#8-error-contract-lld-adr-003) |

## Context
Services are gRPC; clients reach them as JSON through gRPC JSON transcoding, which maps gRPC status codes to HTTP. HTTP 422 isn't part of that mapping. Clients need to tell apart conflicts, key reuse and business rejections.

## Decision drivers
- Clients can branch reliably.
- No custom mapping layer in every service.

## Considered options
1. gRPC status + `google.rpc.ErrorInfo` reason
2. Custom HTTP mapping (exact 409 / 422)

## Decision
Chosen: **standard gRPC status plus `ErrorInfo` (`domain = "bloom"`, stable `reason`)**: `ABORTED` for conflicts and in-progress keys (409), `INVALID_ARGUMENT` for key problems (400), `FAILED_PRECONDITION` for business rejections (400, reason defined by the owning service).

## Pros and cons of the options
### Status + reason
- ✅ Works unchanged with transcoding; reasons are explicit.
- ❌ Reused keys return 400, not the REST-idiomatic 422.

### Custom HTTP mapping
- ❌ Custom middleware in every service.

## Consequences
- ✅ One error model for gRPC and JSON clients.
- ⚠️ Each service LLD lists its own business `reason` codes.

## Links
- [00-reliability.md §8](../00-reliability.md)
