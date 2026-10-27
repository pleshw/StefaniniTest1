# ADR 0002 — Wolverine for CQRS, outbox and RabbitMQ

**Status:** accepted

## Context

Orders needs command and query handlers, transactional event publishing (no dual write to the database and the broker), idempotent consumption with retries, and a dead letter queue (DLQ), all on PostgreSQL and RabbitMQ.

## Decision

Use Wolverine (MIT licence): handlers, a durable outbox and inbox in PostgreSQL (schema `wolverine`) and the RabbitMQ transport. The API writes the order, the idempotency key and the `OrderPlaced.v1` event in the same transaction; the event leaves through the outbox. The Worker consumes from a quorum queue, with retries after 5 s, 30 s and 5 min, and then sends the message to `orders.dlq` through `orders.dlx` with the error headers. Handlers sit behind ports of the Application layer, and Wolverine's types stay in Infrastructure and the hosts.

## Consequences

- No event is lost if the broker is down at write time (a durable outbox on every event endpoint), and a message delivered twice is handled once (inbox).
- Wolverine's tables belong to Wolverine; `outbox_messages` and `inbox_messages` in the data model only describe the shape.
- Open points awaiting a decision: Wolverine's runtime compilation package (C17), the "no x-delivery-limit" warning on the `orders.worker.placed` queue, and the fact that this queue only exists after the Worker's first start.
- The cost of leaving Wolverine is bounded by the Application ports.

## Alternatives considered

- **Mediator with a source generator + RabbitMQ.Client 7:** more code for outbox, inbox and retries. It remains the way out if Wolverine stops fitting.
