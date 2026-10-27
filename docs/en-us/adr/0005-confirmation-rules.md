# ADR 0005 — Order confirmation by configurable rules

**Status:** accepted

## Context

Some orders (a high value, a client's first order, a given channel) need a person before they are confirmed; the others should confirm by themselves. The rule changes with the business, without a new deploy.

## Decision

Confirmation rules are versioned data (`confirmation_rules`, key `(id, version)`), with the same model as discounts and the same conditions (`TotalAbove`, `FirstOrderOfClient`, `ChannelIs`...), and a single action, `Hold`. Orders.Worker evaluates the active rules when it consumes `OrderPlaced.v1`: if any `Hold` matches, the order goes to `OnHold` and waits for an admin; otherwise it becomes `Confirmed`. Admins confirm, hold, block or cancel from the Admin page while the order is `Pending` or `OnHold`; every change writes `order_status_history` (with the actor) and publishes `OrderStatusChanged.v1`. Active rules are cached per process (`Orders__Confirmation__CacheDuration`, 5 s): an admin's write empties the API's cache and the Worker waits that long.

## Consequences

- Changing the rule is editing data, with a version history and a preview; no deploy.
- A held order waits for a person: that is why the "OnHold for more than 4 h" alert exists (see [Metrics and alerts](../operations/alerts.md)).
- The cache is per instance; several instances converge within the cache time.
- The development rule "hold BRL orders above 5,000.00" is only added with `Orders__Confirmation__SeedEnabled`, never in staging or production.
- Client segments still have no data source: the `ClientSegment` condition never matches.

## Alternatives considered

- **Rules in code:** every change would need a deploy.
- **Manual confirmation for everything:** not viable at the expected volume (50,000 orders a month).
