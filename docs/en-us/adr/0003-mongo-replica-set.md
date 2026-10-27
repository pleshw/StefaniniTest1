# ADR 0003 — MongoDB as a replica set, even with one node

**Status:** accepted

## Context

Changing a price in Catalog must update the product and write the price history (`price_history`) together: either both happen or neither does. Transactions in MongoDB exist only on a replica set.

## Decision

MongoDB 8 runs as a one-node replica set (`rs0`) in Docker Compose, started by an init script. A price change is a transaction that updates `products` with a filter on `{ sku, version }` and inserts into `price_history`. A diverging version answers 412 (`If-Match`). Catalog's startup check fails fast if there is no replica set.

## Consequences

- The price history is never without its price (there is an atomicity test: if the history insert fails, the price change is rolled back).
- Every environment must start the replica set; a plain MongoDB will not do.
- One node gives no high availability; in production, how many nodes to use is an operations decision and the code does not change.

## Alternatives considered

- **No transaction (two writes):** allows a window with a new price and no history.
- **History in the same document:** the document would grow without bound and the audit would be mixed with current data.
