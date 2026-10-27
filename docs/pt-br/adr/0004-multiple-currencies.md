# ADR 0004 — Várias moedas, uma por pedido, sem conversão

**Status:** aceito

## Contexto

O catálogo precisa vender em mais de uma moeda (BRL e USD hoje), e o dinheiro nunca pode perder centavos ao passar entre serviços.

## Decisão

Cada pedido tem exatamente uma moeda ISO 4217 (BRL por padrão). O Catalog guarda um preço por SKU por moeda (`Decimal128`); as moedas aceitas são uma lista fixa (`AllowedCurrencies`). Não há conversão: um SKU sem preço na moeda pedida é tratado como ausente, e o pedido é recusado. O Orders pergunta os preços em uma única chamada em lote, na moeda do pedido, e nunca confia em preço enviado pelo cliente. Valores monetários são `numeric(19,4)` no PostgreSQL e `decimal` em todo o código; o documento OpenAPI marca `decimal` para o client gerado não ler dinheiro como `double`.

## Consequências

- Totais e descontos são calculados na moeda do pedido, com arredondamento em um ponto só (por pedido ou por linha, conforme a regra).
- Acrescentar uma moeda é acrescentar à lista e dar preço aos produtos; a lista está repetida em `web/src/admin/api/products.ts`, e uma terceira moeda faria o editor do Admin perder o preço ao salvar (o `PUT` substitui os preços).
- Não há taxa de câmbio nem pedido com moedas misturadas (o domínio recusa).

## Alternativas consideradas

- **Uma moeda só:** simples, mas não atende o catálogo em dólar.
- **Conversão por taxa:** exige uma fonte de taxas e regras de arredondamento e de auditoria que o projeto não tem.
