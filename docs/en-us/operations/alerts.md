# Metrics and alerts

Orders' services export custom metrics through OpenTelemetry (the `Orders` meter). The Aspire Dashboard, from the `observability` profile, shows the values; the stack has no alerting engine, so this document fixes **the thresholds** any engine (or a person watching the dashboard) should use. The names below are the ones the dashboard shows.

## Custom metrics

| Metric | Type | Unit | Tags | Emitted by |
| --- | --- | --- | --- | --- |
| `orders.created` | counter | `{order}` | `channel`, `currency` | Orders API, after the order's transaction commits |
| `orders.duplicate_replayed` | counter | `{request}` | `channel` | Orders API, when the answer comes from an idempotency key already stored |
| `discount.applied` | counter | `{discount}` | `currency`, `once_per_client` | Orders API, one per discount applied to a created order (a quote does not count) |
| `catalog.price_lookup.duration` | histogram | `ms` | `outcome` (`ok` or `error`) | Orders, on each batch price query to Catalog (retries included) |
| `orders.on_hold` | gauge | `{order}` | none | Orders Worker, read from the database every 30 s |
| `orders.on_hold.oldest_age` | gauge | `s` | none | Orders Worker; how long the oldest `OnHold` order has been waiting (no series when no order is held) |
| `rabbitmq.dlq.depth` | gauge | `{message}` | none | Orders Worker, read from RabbitMQ every 30 s (no series when the queue does not exist or the broker does not answer) |

The three gauges are read by the Worker, not the API, so the reading is not repeated by every API instance. With more than one Worker instance each reports the same value: use the **maximum**, never the sum. The interval is `Orders__Metrics__PollInterval` (default `00:00:30`).

## Alerts

| Alert | Condition | Window | Why it matters | First step |
| --- | --- | --- | --- | --- |
| Messages in the DLQ | `rabbitmq.dlq.depth` > 0 | for 10 minutes in a row | A message the Worker gave up on will not move by itself: an order may be left unconfirmed | Open "Dead letters" in the Admin, read the error and replay once the cause is fixed |
| Order held for too long | `orders.on_hold.oldest_age` > 14,400 (4 h) | on any reading | An `OnHold` order waits for a person; past 4 h, nobody is watching the queue | Open the "OnHold" queue in the Admin and confirm, block or cancel |
| Slow order creation | p95 of `http.server.request.duration` of `orders-api`, with `http.request.method` = `POST` and `http.route` = `/api/v1/orders`, > 0.3 s | 5 minutes | It is the latency target of the [sizing document](../04-tech-dimensioning.md); above it checkout degrades | Look at `catalog.price_lookup.duration` (is Catalog slow or down?) and the trace of a slow order |

Two readings help not to trust an empty dashboard: if `orders.on_hold` or `rabbitmq.dlq.depth` **disappears** for more than 10 minutes, the Worker's reading is failing (the Worker log says which) or the Worker stopped, and that should be treated as an alert. And `catalog.price_lookup.duration` with a p95 above 50 ms is the price batch latency target; it is not an alert here because the load test already enforces it, but the dashboard shows the trend.
