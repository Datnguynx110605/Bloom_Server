# LLD-ADR-006: The inbox is keyed by (consumer, message ID)

| | |
|---|---|
| **Status** | Accepted |
| **Date** | 2026-10-31 |
| **Deciders** | Project owner |
| **Related** | HLD-ADR-007, [00-reliability.md §4.2](../00-reliability.md#42-inbox_messages) |

## Context
The owner's design keys the inbox by message ID (= outbox ID). A service can have more than one handler for the same message, for example `order` handling "refund completed" both for order status and for saga state.

## Decision drivers
- Each handler applies each message exactly once.
- No hidden rule developers must remember.

## Considered options
1. `(consumer, message_id)`
2. `message_id` only

## Decision
Chosen: **primary key `(consumer, message_id)`**, where `consumer` is the handler name.

## Pros and cons of the options
### Composite key
- ✅ Correct with any number of handlers per message.
- ❌ Slightly larger key.

### Message ID only
- ❌ The second handler would skip the message as a duplicate.

## Consequences
- ✅ Adding a handler never silently breaks another (T-06).

## Links
- [00-reliability.md §4.2, §7.3](../00-reliability.md)
