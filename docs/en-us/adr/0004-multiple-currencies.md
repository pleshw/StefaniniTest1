# ADR 0004 — Several currencies, one per order, no conversion

**Status:** accepted

## Context

The catalog must sell in more than one currency (BRL and USD today), and money must never lose cents when passing between services.

## Decision

Each order has exactly one ISO 4217 currency (BRL by default). Catalog keeps one price per SKU per currency (`Decimal128`); the accepted currencies are a fixed list (`AllowedCurrencies`). There is no conversion: a SKU with no price in the requested currency is treated as missing, and the order is refused. Orders asks for the prices in a single batch call, in the order's currency, and never trusts a price sent by the client. Money is `numeric(19,4)` in PostgreSQL and `decimal` throughout the code; the OpenAPI document marks `decimal` so the generated client does not read money as `double`.

## Consequences

- Totals and discounts are calculated in the order's currency, with rounding at one point only (per order or per line, depending on the rule).
- Adding a currency means adding it to the list and pricing the products; the list is repeated in `web/src/admin/api/products.ts`, and a third currency would make the Admin editor lose that price on save (the `PUT` replaces the prices).
- There is no exchange rate and no order with mixed currencies (the domain refuses it).

## Alternatives considered

- **A single currency:** simple, but it does not serve a dollar catalog.
- **Conversion by rate:** needs a source of rates and rounding and audit rules the project does not have.
