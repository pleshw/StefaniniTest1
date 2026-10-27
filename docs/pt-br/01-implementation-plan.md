# Plataforma de Pedidos e Catálogo — Plano de implementação

26 de out. de 2026 · Elyton Victor

## Visão geral

Construímos uma plataforma de pedidos em .NET 10 com três partes de back-end: a Orders API (PostgreSQL, CQRS, RabbitMQ), o serviço Catalog (MongoDB) e um BFF que é o único ponto de entrada para o navegador. Duas páginas de front-end ficam por cima: uma página Shop para fluxos de cliente e uma página Admin que gerencia catálogo e descontos e acompanha os eventos de pedidos ao vivo. Tudo roda localmente com Docker Compose e é entregue em 8 fases (veja Plano de implementação em fases).

Decisões confirmadas:

| Tema | Decisão |
| --- | --- |
| "amount" do pedido (AC2) | Significa quantidade. O pedido armazena `itemCount` (soma das quantidades dos itens) e cada item armazena `quantity`. Os campos monetários são `subtotal`, `discountTotal`, `total`. |
| Acesso do front-end | O navegador conversa apenas com o BFF, que pertence ao lado Orders. O navegador nunca chama o Catalog diretamente. |
| BFF | Aceito: um único host de BFF, `Orders.Bff`, com dois grupos de rotas, `/bff/shop` e `/bff/admin`. Justificativa em Arquitetura. |
| Páginas de front-end | Página Shop (navegar, carrinho, checkout, meus pedidos) e página Admin (eventos de pedidos ao vivo, ações sobre pedidos, DLQ, descontos, regras de confirmação, catálogo). |
| Runtime | .NET 10 LTS, C# 14, SDK fixado com `global.json`. |
| Moeda | Multimoeda: cada pedido tem exatamente uma moeda ISO 4217; o Catalog mantém um preço por SKU por moeda; BRL é o padrão; sem conversão. |
| Quem cria pedidos | Compradores autenticados, para si mesmos, e admins em nome de um cliente. Todo pedido armazena `created_by` e `channel` (`shop` ou `admin`). |
| Confirmação de pedidos | Automática no Orders.Worker, por regras de confirmação configuráveis; admins podem confirmar, colocar em espera ou bloquear pedidos pela página Admin. |
| Eventos de pedidos | Consumidos pelo Orders.Worker (máquina de estados de status) e pelo BFF (feed ao vivo para o Admin). |
| Preços | O Catalog é a autoridade de preços. O Orders busca os preços no servidor em uma única chamada em lote e nunca confia em preços vindos do cliente. |
| Retenção | Chaves de idempotência por 24 h; log de eventos do Admin (`order_event_log`) por 30 dias. As linhas de outbox são do Wolverine, que as apaga assim que o broker confirma a publicação. |
| CI/CD | GitHub Actions; todo deploy passa por um gate obrigatório (veja o [Guia de execução](02-execution-guide.md#cicd-com-github-actions)). |
| Auditoria | Armazenada como dado de domínio em cada serviço, não derivada de logs. |

## Arquitetura

As requisições fluem navegador → nginx → BFF → Orders API, e a Orders API chama o Catalog de forma síncrona para obter os preços. Os efeitos colaterais fluem de forma assíncrona: o Orders grava eventos em um outbox, um relay os publica no RabbitMQ, e o Orders.Worker e o BFF os consomem.

```mermaid
flowchart TB
    browser["Browser<br/>Shop + Admin pages"] -->|same origin :8080| nginx["nginx<br/>static files + proxy"]
    nginx -->|/bff/*| bff["Orders.Bff<br/>compose, auth, SSE"]
    bff -->|REST| orders["Orders API<br/>CQRS, idempotency"]
    bff -->|products, admin writes| catalog["Catalog API<br/>products, prices"]
    orders -->|batch prices| catalog
    orders -->|outbox relay| rabbit["RabbitMQ<br/>orders.events + DLQ"]
    rabbit -->|events| bff
    rabbit -->|consume, retry| worker["Orders.Worker<br/>status state machine"]
    worker -->|writes| pg[("PostgreSQL<br/>orders, outbox, inbox")]
    orders -->|EF Core| pg
    catalog -->|C# driver| mongo[("MongoDB<br/>products, history")]
```

Apenas o nginx é exposto no host; o BFF é a única entrada para o navegador, e o RabbitMQ alimenta tanto o Worker quanto a visão ao vivo do Admin.

### Por que um BFF

Um BFF se encaixa neste projeto por quatro motivos:

- **Uma única porta para o navegador.** A página Shop precisa navegar pelos produtos, mas o navegador só pode conversar com o lado Orders. O BFF lê o Catalog em nome dele, de modo que a Orders API continua sendo um serviço de domínio puro, e não um proxy de catálogo.
- **Dois front-ends, formatos diferentes.** O Shop precisa de payloads pequenos, no formato voltado ao cliente. O Admin precisa de agregados (pedido + histórico de status + eventos) e de um feed ao vivo. Os grupos de rotas `/bff/shop` e `/bff/admin` mantêm cada contrato sob medida.
- **Eventos ao vivo.** O BFF assina a exchange `orders.events` e envia os eventos para a página Admin com Server-Sent Events (`TypedResults.ServerSentEvents`, nativo no .NET 10). Nenhum acesso ao broker a partir do navegador.
- **Segurança.** O BFF mantém o cookie de sessão (padrão de segurança BFF). Os tokens nunca chegam ao navegador, e as rotas de admin exigem a role `Admin`.

Regras que mantêm o BFF enxuto: nenhuma lógica de domínio, nenhum banco de dados, nenhum cálculo de preço ou desconto. Ele apenas compõe, mapeia para view models e encaminha. Ele fica no repositório do Orders como um host próprio, `Orders.Bff`, para que seu deploy seja feito separadamente da Orders API. Rotas de passagem (pass-through) usam YARP; rotas compostas usam HttpClients tipados.

### Comunicação

| De → Para | Estilo | Motivo |
| --- | --- | --- |
| Navegador → BFF | REST + SSE na mesma origem (nginx) | Sem CORS, autenticação por cookie |
| BFF → Orders API | REST (client tipado) | Commands e queries |
| BFF → Catalog | REST, somente leitura para o Shop; escritas de admin encaminhadas | O navegador nunca vê o Catalog |
| Orders API → Catalog | REST, uma chamada de preços em lote por pedido | Preços imediatos e atualizados (Req 4, AC11) |
| Orders API → RabbitMQ | Outbox durável do Wolverine no Postgres + publisher confirms | Nenhum evento perdido, sem dual writes |
| RabbitMQ → Orders.Worker | Quorum queue `orders.worker.placed` lida pelo inbox durável; retentativas agendadas pelo Wolverine (5 s / 30 s / 5 min), depois `orders.dlq` via `orders.dlx` | Máquina de estados de status, retentativas (Req 2) |
| RabbitMQ → BFF | Fila própria não durável por instância | Feed ao vivo do Admin |

### Stack tecnológica

| Aspecto | Escolha | Observação |
| --- | --- | --- |
| Runtime | .NET 10 LTS, ASP.NET Core Minimal APIs | Com suporte até nov. de 2028 |
| Banco do Orders | PostgreSQL 17, EF Core 10 + Npgsql | Dapper permitido para projeções de leitura |
| Banco do Catalog | MongoDB 8, driver oficial de C# | Replica set de nó único no Docker |
| Mensageria | RabbitMQ 4 | Quorum queues, publisher confirms |
| Biblioteca de CQRS + mensageria | Wolverine (MIT) | Handlers, outbox/inbox no Postgres, transporte RabbitMQ. Alternativa: Mediator com source generator + RabbitMQ.Client 7 |
| Resiliência | Microsoft.Extensions.Http.Resilience | Timeout, retry e circuit breaker nas chamadas ao Catalog |
| Gateway no BFF | YARP | Rotas de passagem |
| Clients HTTP gerados | NSwag (`NSwag.ApiDescription.Client`, `OpenApiReference`) | Clients C# gerados no build a partir de `catalog/openapi/catalog.v1.json` (Orders e BFF) e `orders/openapi/orders.v1.json` (BFF) |
| Feed ao vivo no BFF | RabbitMQ.Client 7 | Fila exclusiva do BFF em `orders.events`; mesma versão que o transporte do Wolverine usa |
| Autenticação entre serviços | Microsoft.AspNetCore.Authentication.JwtBearer | JWT de 5 min assinado pelo BFF para cada serviço (HMAC-SHA256, chave do `.env`); provisório até o token exchange de um provedor de identidade |
| Documentação da API | Microsoft.AspNetCore.OpenApi + Scalar | OpenAPI 3.1 |
| Validação | FluentValidation 12 (Apache-2.0) | A única forma de validar entradas, em todas as camadas; veja [Regras de design](02-execution-guide.md#regras-de-design-para-todas-as-etapas) |
| Mapeamento de objetos | Mapperly (Riok.Mapperly, MIT, com source generator) | Escolhido em vez do AutoMapper: a 14.x (última MIT) tem o advisory GHSA-rvv3-g6hj-g44x e quebra o build, e a 15+ é comercial |
| Erros | ProblemDetails (RFC 9457) | Mesmo formato em todos os serviços |
| Observabilidade | OpenTelemetry → OTel Collector → Aspire Dashboard | Profile `observability` do Compose; instrumentação de ASP.NET Core, HttpClient e Runtime |
| Testes de back-end | xUnit v3, NSubstitute, Shouldly, Testcontainers, NetArchTest, WireMock.Net | Evitar FluentAssertions v8 (licença comercial); WireMock.Net faz o papel do Catalog nos testes de integração do Orders |
| Teste de carga | k6 (imagem `grafana/k6`, profile `load` do Compose) | Roda `tests/load/orders.js` com o perfil de pico do dimensionamento; sem pacote npm ou NuGet; os thresholds são as metas de latência |
| Front-end | TypeScript, Vite, Vitest + jsdom, Bootstrap 5.3 + Bootstrap Icons via npm | axe-core para verificações de acessibilidade; ferramentas de desenvolvimento: ESLint (typescript-eslint) + Prettier, @testing-library/dom, MSW para stubs do BFF, @vitest/coverage-v8 |

## Estrutura do repositório e das solutions

Um único repositório Git contém duas solutions .NET (`.slnx`) e um pacote de front-end. Os serviços não compartilham código de domínio; o Orders se comunica com o Catalog por meio de um client gerado a partir do documento OpenAPI do Catalog.

```text
/
├─ global.json                     # pins .NET 10 SDK
├─ Directory.Build.props           # nullable, warnings as errors, analyzers (EF1002 = error)
├─ Directory.Packages.props        # central package versions
├─ .gitattributes                  # *.sh text eol=lf
├─ .config/dotnet-tools.json       # pins dotnet-ef
├─ docker-compose.yml  .env.example
├─ deploy/nginx/  deploy/otel/  deploy/mongo/  deploy/rabbitmq/
├─ deploy/compose.staging.yml  deploy/compose.production.yml  deploy/images.txt
├─ scripts/                        # check-docs.mjs, smoke.sh, deploy.sh, images.sh
├─ tests/load/orders.js            # k6 load test (Compose profile load)
├─ shared/ServiceDefaults/         # shared OTel, health checks, resilience, service tokens; in both .slnx
├─ orders/Orders.slnx
│  ├─ openapi/orders.v1.json        # generated at build
│  ├─ src/Orders.Domain             # aggregates, value objects, discount strategies, no dependencies but FluentValidation
│  ├─ src/Orders.Application        # commands, queries, handlers, ports (ICatalogClient, IClock...)
│  ├─ src/Orders.Infrastructure     # EF Core, migrations, Catalog client, Wolverine outbox/inbox, RabbitMQ
│  ├─ src/Orders.Contracts          # event records (OrderPlacedV1, OrderStatusChangedV1)
│  ├─ src/Orders.Api                # Minimal API endpoints, ProblemDetails, auth
│  ├─ src/Orders.Worker             # RabbitMQ consumers, status state machine
│  ├─ src/Orders.Bff                # BFF host: YARP, composition, SSE, cookie auth
│  └─ tests/ Orders.Domain.UnitTests  Orders.Application.UnitTests  Orders.Bff.UnitTests
│            Orders.IntegrationTests  Orders.ArchitectureTests
├─ catalog/Catalog.slnx
│  ├─ openapi/catalog.v1.json       # generated at build
│  ├─ src/Catalog.Domain  Catalog.Application  Catalog.Infrastructure  Catalog.Api
│  └─ tests/ Catalog.Domain.UnitTests  Catalog.Application.UnitTests  Catalog.IntegrationTests  Catalog.ArchitectureTests
└─ web/
   ├─ index.html  shop.html  admin.html
   ├─ src/shared/  (api/ domain/ ui/: http client, ProblemDetails, SSE, idempotency, formatting, shared views)
   ├─ src/shop/    (api/ domain/ ui/: catalog, cart, orders)
   ├─ src/admin/   (api/ domain/ ui/: live events, inspector, rules, catalog editor)
   ├─ mocks/       (MSW handlers for the BFF)
   └─ tests/
```

Regra de dependência, garantida pelo NetArchTest em `ArchitectureTests`: o Domain não depende de nada, o Application depende do Domain, e o Infrastructure e os hosts dependem para dentro. A mesma divisão em camadas vale em `web/`: `ui` pode importar `domain` e `api`, nunca o contrário.

## Superfície da API

Todas as APIs são versionadas em `/api/v1`, retornam ProblemDetails em caso de erro e são documentadas com OpenAPI 3.1. O header `Idempotency-Key` (UUID) é obrigatório na criação de pedidos (`POST /api/v1/orders` e `POST /api/v1/admin/orders`); as demais escritas se protegem com `If-Match` sobre a versão ou com upsert, e a criação de regras não usa a chave.

### Orders API (interna, chamada pelo BFF)

| Método | Caminho | Finalidade | AC |
| --- | --- | --- | --- |
| POST | `/api/v1/orders` | Cria um pedido; header `Idempotency-Key`; retorna 201 + pedido | AC1, AC3 |
| GET | `/api/v1/orders` | Lista com filtros `clientId`, `status`, `from`, `to`, `q` (busca full-text); paginação keyset `after`, `limit` | AC1 |
| GET | `/api/v1/orders/{id}` | Detalhe do pedido: cliente, itens, itemCount, subtotal, discountTotal, total, status | AC1, AC2 |
| GET | `/api/v1/orders/{id}/history` | Histórico de status (auditoria) | AC1 |
| POST | `/api/v1/admin/orders/{id}/cancel` | Cancelamento pelo admin, só a partir de Confirmed; motivo obrigatório; `If-Match` na versão do pedido (428 sem ele, 412 se desatualizado) | - |
| GET | `/api/v1/admin/events` | Log de eventos de pedidos, com paginação keyset | Página Admin |
| GET | `/api/v1/admin/dead-letters` | Mensagens retidas na DLQ | Req 2 |
| POST | `/api/v1/admin/dead-letters/{messageId}/replay` | Republica uma mensagem retida | Req 2 |
| GET/POST/PUT | `/api/v1/admin/discount-rules[/{id}]` | Gerencia regras de desconto; o GET da lista aceita o filtro `active`; o PUT exige `If-Match` (428 sem ele, 412 se desatualizado) | AC5 |
| POST | `/api/v1/admin/discount-rules/{id}/deactivate` | Grava uma nova versão inativa da regra; `If-Match` opcional (412 se desatualizado) | AC5 |
| POST | `/api/v1/admin/discount-rules/preview` | Simula (dry-run) as regras contra um carrinho de exemplo | AC5 |
| POST | `/api/v1/admin/orders` | O admin cria um pedido em nome de um cliente; mesmo fluxo idempotente, `Idempotency-Key` com escopo pelo cliente, channel = admin | AC1, AC3 |
| POST | `/api/v1/admin/orders/{id}/confirm \| /hold \| /block` | O admin sobrepõe o fluxo automático; motivo obrigatório para hold e block; `If-Match` na versão do pedido (428 sem ele, 412 se desatualizado) | Decisão |
| GET/POST/PUT | `/api/v1/admin/confirmation-rules[/{id}]` | Gerencia as regras de confirmação automática (versionadas); o GET da lista aceita o filtro `active`; o PUT exige `If-Match` | Decisão |
| POST | `/api/v1/admin/confirmation-rules/{id}/deactivate` | Grava uma nova versão inativa da regra; `If-Match` opcional (412 se desatualizado) | Decisão |

### Catalog API (interna, chamada pela Orders API e pelo BFF)

| Método | Caminho | Finalidade | AC |
| --- | --- | --- | --- |
| GET | `/api/v1/products` | Busca e pagina produtos com os preços atuais (`q`, `category`, `after`, `limit`) | AC9, AC10 |
| GET | `/api/v1/products/{sku}` | Um produto; retorna `ETag` | AC9 |
| POST | `/api/v1/products/prices:query` | Consulta de preços em lote, body `{ currency, skus: [...] }`, no máximo 100 SKUs, uma única query `$in` | AC11 |
| PUT | `/api/v1/admin/products/{sku}` | Cria ou substitui um produto (upsert idempotente); `If-Match` na atualização | Item 9 |
| PATCH | `/api/v1/admin/products/{sku}/price` | Altera o preço; `If-Match`; grava o histórico de preços | Item 9 |
| POST | `/api/v1/admin/products/{sku}/deactivate` | Retira um produto de venda | - |

### BFF (público, a única API que o navegador vê)

| Método | Caminho | Mapeia para |
| --- | --- | --- |
| GET | `/bff/user` | Usuário da sessão: subject, nome e roles; 401 sem sessão |
| GET, POST | `/bff/login` | Formulário do login de desenvolvimento; existe só quando `Bff__DevLogin__Enabled` está ligado |
| POST | `/bff/logout` | Encerra a sessão; exige o header `X-CSRF` |
| GET | `/bff/shop/products` | Catalog `GET /products` (view model) |
| POST | `/bff/shop/cart/quote` | Preview do Orders: preços + descontos do carrinho, nada é persistido |
| POST | `/bff/shop/orders` | Orders `POST /orders`, encaminha o `Idempotency-Key` |
| GET | `/bff/shop/orders`, `/bff/shop/orders/{id}` | Queries do Orders, restritas ao cliente autenticado |
| GET | `/bff/admin/events` | Orders `GET /admin/events`: log de eventos, usado como backfill do feed ao vivo |
| GET | `/bff/admin/events/stream` | Feed SSE ao vivo: a mensagem do RabbitMQ só nomeia o pedido, e o BFF envia as novas entradas do log de eventos |
| GET | `/bff/admin/orders` | Composto: fila de OnHold (mais antigos primeiro, até 100) + uma página de pedidos com os filtros |
| GET | `/bff/admin/orders/{id}` | Composto: pedido + histórico + últimos 50 eventos |
| POST | `/bff/admin/orders` | Orders `POST /admin/orders`, encaminha o `Idempotency-Key` |
| POST | `/bff/admin/orders/{id}/(confirm\|hold\|block\|cancel)` | Ações de admin do Orders (YARP) |
| \* | `/bff/admin/discount-rules/**`, `/bff/admin/confirmation-rules/**`, `/bff/admin/dead-letters/**` | Rotas de admin do Orders (YARP) |
| GET | `/bff/admin/products/{sku}` | Catalog `GET /products/{sku}`: lê o produto com a versão (`ETag`) que o editor envia como `If-Match` (YARP, política Admin) |
| \* | `/bff/admin/products/{sku}/{**rest}` | Rotas de admin do Catalog (YARP): `PUT` do produto, `PATCH .../price`, `POST .../deactivate` |

Observação: `/bff/shop/cart/quote` precisa de um endpoint correspondente no Orders, `POST /api/v1/orders/quote`, que executa o mesmo caminho de código de preços e descontos da criação de pedidos, sem persistir nada.

## Modelo de dados e idempotência

A idempotência é garantida em quatro camadas: o navegador reutiliza uma chave por checkout, a Orders API armazena a chave junto com a resposta, os resgates de desconto têm restrições de unicidade, e os consumers do Worker eliminam duplicatas por meio de um inbox. Valores monetários são `numeric(19,4)` no Postgres, `decimal` em C# e `Decimal128` no Mongo, sempre com um código de moeda ISO 4217. Arredondamento: duas casas decimais, `MidpointRounding.AwayFromZero`, aplicado uma vez por linha e uma vez no total.

| Projeto | Ameaça | Mecanismo | Armazenamento |
| --- | --- | --- | --- |
| Orders API | Envio duplo ou retentativa de `POST /orders` | `Idempotency-Key` com escopo por cliente + hash SHA-256 da requisição | `idempotency_requests` |
| Orders API | Mesmo desconto aplicado duas vezes | Restrições de unicidade; id + versão da regra registrados como snapshot no pedido | `discount_redemptions` |
| Orders API | Eventos perdidos | Outbox na mesma transação do pedido | `outbox_messages` |
| Orders.Worker / BFF | Reentrega do RabbitMQ | Inbox indexado por id da mensagem + consumer | `inbox_messages` (apenas no Worker; o feed do BFF é só para exibição) |
| Catalog | Escritas repetidas de admin | Upsert via `PUT` sobre `sku` único + `If-Match`; a coleção de chaves foi criada com índices para algum POST futuro, mas nenhum endpoint a usa | coleção `idempotency_requests` |
| Front-end | Duplo clique, timeout seguido de retentativa | Um `crypto.randomUUID()` por tentativa de checkout, mantido em `sessionStorage` | Navegador |

### Orders (PostgreSQL)

```sql
CREATE TABLE orders (
    id              uuid          PRIMARY KEY,
    number          text          NOT NULL UNIQUE,          -- human-readable, from the id, e.g. ORD-2026-3F9A1C2B7D40
    client_id       uuid          NOT NULL,
    client_name     text          NOT NULL,                 -- snapshot
    created_by      text          NOT NULL,                 -- shopper id or admin id
    channel         text          NOT NULL,                 -- shop | admin
    status          text          NOT NULL,                 -- Pending | OnHold | Confirmed | Blocked | Cancelled | Failed
    item_count      int           NOT NULL CHECK (item_count > 0),   -- AC2 "amount" = quantity
    currency        char(3)       NOT NULL,
    subtotal        numeric(19,4) NOT NULL,
    discount_total  numeric(19,4) NOT NULL DEFAULT 0,
    total           numeric(19,4) NOT NULL,
    created_at      timestamptz   NOT NULL,
    updated_at      timestamptz   NOT NULL,
    row_version     int           NOT NULL DEFAULT 0,       -- optimistic concurrency
    search_vector   tsvector GENERATED ALWAYS AS
        (to_tsvector('simple', number || ' ' || client_name)) STORED
);
CREATE INDEX ix_orders_client_created ON orders (client_id, created_at DESC, id);
CREATE INDEX ix_orders_status_created ON orders (status, created_at DESC, id);
CREATE INDEX ix_orders_created ON orders (created_at DESC, id);          -- migration 0004
CREATE INDEX ix_orders_search ON orders USING GIN (search_vector);
CREATE INDEX ix_orders_client_trgm ON orders USING GIN (client_name gin_trgm_ops);

CREATE TABLE order_items (
    order_id        uuid          NOT NULL REFERENCES orders (id),
    line_no         int           NOT NULL,
    sku             text          NOT NULL,
    product_name    text          NOT NULL,                 -- snapshot
    unit_price      numeric(19,4) NOT NULL,                 -- snapshot from Catalog
    catalog_version int           NOT NULL,
    quantity        int           NOT NULL CHECK (quantity > 0),
    line_discount   numeric(19,4) NOT NULL DEFAULT 0,
    line_total      numeric(19,4) NOT NULL,
    PRIMARY KEY (order_id, line_no)
);

CREATE TABLE order_status_history (                       -- audit
    order_id    uuid        NOT NULL REFERENCES orders (id),
    seq         int         NOT NULL,
    from_status text,
    to_status   text        NOT NULL,
    reason      text,
    actor       text        NOT NULL,                     -- user id or 'system:worker'
    trace_id    text,
    changed_at  timestamptz NOT NULL,
    PRIMARY KEY (order_id, seq)
);

CREATE TABLE discount_rules (
    id              uuid        NOT NULL,
    version         int         NOT NULL,
    name            text        NOT NULL,
    type            text        NOT NULL,                 -- Percentage | FixedAmount | BuyXGetY | Tiered
    parameters      jsonb       NOT NULL,                 -- validated per type
    conditions      jsonb       NOT NULL DEFAULT '[]',    -- min subtotal, SKUs, categories, client segment (never matches yet)
    priority        int         NOT NULL,
    stackable       boolean     NOT NULL DEFAULT false,
    once_per_client boolean     NOT NULL DEFAULT false,
    valid_from      timestamptz NOT NULL,
    valid_to        timestamptz,
    is_active       boolean     NOT NULL DEFAULT true,
    created_by      text        NOT NULL,
    created_at      timestamptz NOT NULL,
    PRIMARY KEY (id, version)                             -- each edit = new version, history kept
);

CREATE TABLE order_discounts (
    order_id         uuid          NOT NULL REFERENCES orders (id),
    discount_rule_id uuid          NOT NULL,
    rule_version     int           NOT NULL,
    amount           numeric(19,4) NOT NULL,
    PRIMARY KEY (order_id, discount_rule_id),
    FOREIGN KEY (discount_rule_id, rule_version) REFERENCES discount_rules (id, version)
);

CREATE TABLE idempotency_requests (
    client_id       uuid        NOT NULL,
    idempotency_key uuid        NOT NULL,
    request_hash    bytea       NOT NULL,
    response_status smallint    NOT NULL,
    response_body   jsonb       NOT NULL,
    order_id        uuid        NOT NULL,
    created_at      timestamptz NOT NULL DEFAULT now(),
    expires_at      timestamptz NOT NULL,                 -- created_at + 24 h
    PRIMARY KEY (client_id, idempotency_key)
);
CREATE INDEX ix_idempotency_expires ON idempotency_requests (expires_at);

CREATE TABLE discount_redemptions (
    discount_rule_id uuid        NOT NULL,
    client_id        uuid        NOT NULL,
    order_id         uuid        NOT NULL REFERENCES orders (id),
    once_per_client  boolean     NOT NULL,
    redeemed_at      timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (discount_rule_id, order_id)
);
CREATE UNIQUE INDEX ux_redemption_once_per_client
    ON discount_redemptions (discount_rule_id, client_id) WHERE once_per_client;

CREATE TABLE outbox_messages (
    id           uuid        PRIMARY KEY,
    type         text        NOT NULL,                    -- OrderPlaced.v1, OrderStatusChanged.v1
    payload      jsonb       NOT NULL,
    trace_parent text,
    occurred_at  timestamptz NOT NULL,
    published_at timestamptz,
    attempts     int         NOT NULL DEFAULT 0
);
CREATE INDEX ix_outbox_pending ON outbox_messages (occurred_at) WHERE published_at IS NULL;

CREATE TABLE inbox_messages (
    message_id   uuid        NOT NULL,
    consumer     text        NOT NULL,
    processed_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (message_id, consumer)
);
```

Como o Wolverine foi adotado, ele cria e gerencia suas próprias tabelas de outbox e inbox (schema `wolverine`); `outbox_messages` e `inbox_messages` acima documentam apenas o formato e não fazem parte das nossas migrations. O log de eventos do Admin (`GET /admin/events`) lê de uma tabela append-only, `order_event_log`, gravada na mesma transação de cada mudança de status e mantida por 30 dias.

### Catalog (MongoDB)

```js
// products: { _id, sku, name, description, category, attributes: {}, prices: [{ currency, amount: Decimal128 }],
//             isActive, version, updatedAt, updatedBy }
db.products.createIndex({ sku: 1 }, { unique: true });
db.products.createIndex({ category: 1, name: 1 });
db.products.createIndex({ name: "text", description: "text" });

// price_history (audit): { sku, oldPrice, newPrice, currency, version, changedBy, changedAt, traceId }
db.price_history.createIndex({ sku: 1, changedAt: -1 });

// idempotency_requests: { scope, key, requestHash, responseStatus, responseBody, resourceId, createdAt, expiresAt }
db.idempotency_requests.createIndex({ scope: 1, key: 1 }, { unique: true });
db.idempotency_requests.createIndex({ expiresAt: 1 }, { expireAfterSeconds: 0 });
```

Alteração de preço = uma transação (replica set) que atualiza `products` com um filtro em `{ sku, version }` e insere em `price_history`. Uma divergência de versão retorna 412 Precondition Failed.

### Chave de idempotência no front-end

1. Uma chave é gerada com `crypto.randomUUID()` quando o usuário abre o checkout e é salva em `sessionStorage` sob o id do carrinho.
2. Todo envio e toda retentativa após um timeout ou erro de rede enviam a mesma chave.
3. O botão de envio fica desabilitado enquanto há uma requisição em andamento.
4. Em caso de 201, a chave é apagada. Se o carrinho mudar, uma nova chave é gerada.
5. Em caso de 409 ou 422, a página mostra o erro e mantém a chave até o usuário alterar o carrinho.

### Fluxo de criação de pedido (Orders API)

1. **Caminho rápido.** Buscar `(client_id, key)`. Mesmo hash → fazer o replay da resposta armazenada. Hash diferente → 422.
2. **Preços.** Uma chamada a `POST /products/prices:query` com todos os SKUs. SKU ausente ou inativo → 422 listando-os.
3. **Descontos.** Carregar as regras ativas (em cache), avaliá-las no domínio e produzir as regras aplicadas com suas versões.
4. **Transação.** `INSERT idempotency_requests ... ON CONFLICT DO NOTHING`. Uma duplicata concorrente fica bloqueada na primary key até que a primeira faça commit; em seguida, vê o conflito e faz o replay.
5. **Mesma transação.** Inserir o pedido, os itens, `order_discounts`, `discount_redemptions`, a primeira linha do histórico de status e a linha de outbox `OrderPlaced.v1`; depois, fazer commit.
6. **Violação de unicidade no resgate** → rollback, retornar 409 "discount already used".

Nenhuma chamada HTTP acontece enquanto houver uma transação de banco de dados aberta.

## Motor de regras de desconto

Descontos são dados, não código: os admins criam e versionam regras pela API, e o código contém um conjunto fixo de tipos de regra. Isso atende ao AC5 sem um motor de scripts.

- **Tipos de regra (Strategy).** Uma `IDiscountStrategy` por `type`: `Percentage`, `FixedAmount`, `BuyXGetY`, `Tiered`. Cada uma valida o próprio JSON de `parameters` ao salvar e calcula descontos por linha ou por pedido.
- **Condições (Specification).** `MinSubtotal`, `SkuIn`, `CategoryIn`, `MinQuantity`, `ClientSegment`, `DateWindow`. Combinadas com AND. Tipos de condição desconhecidos falham na validação ao salvar. `ClientSegment` passa na validação, mas ainda nunca corresponde: não existe fonte de segmentos de cliente.
- **Fluxo.** Ordenação configurável pelo admin por meio de `priority` (o menor executa primeiro) e `stackable`. O motor aplica a melhor regra não cumulativa e, em seguida, todas as regras cumulativas em ordem de prioridade. Um teto (`maxDiscountPercent`, configuração, padrão de 50%) impede o acúmulo descontrolado.
- **Determinismo.** O motor é um serviço de domínio puro: entrada = linhas do carrinho com preços do catálogo, informações do cliente, conjunto de regras ativas e o horário vindo do `TimeProvider`. Saída = regras aplicadas com versões e valores. Mesma entrada, mesma saída.
- **Versionamento.** Toda edição insere uma nova linha `(id, version)`; os pedidos referenciam a versão que usaram, de modo que o histórico continua explicável e um replay nunca recalcula nada.
- **Cache.** As regras ativas ficam em cache em memória (`IMemoryCache`, por processo) por 60 s; uma escrita de admin invalida a chave na instância da API que a atendeu, e as outras instâncias veem a mudança quando a entrada delas expira. As regras de confirmação usam o mesmo cache por 5 s, para o Worker usar uma alteração logo. O `HybridCache` do plano original não foi adotado.
- **Preview.** `POST /admin/discount-rules/preview` e `/orders/quote` reutilizam exatamente o mesmo caminho do motor usado na criação de pedidos.

Exemplo de `parameters` e `conditions`:

```json
{
  "type": "Percentage",
  "parameters": { "percent": 10, "appliesTo": "order" },
  "conditions": [ { "kind": "MinSubtotal", "amount": 200.00, "currency": "BRL" } ],
  "priority": 10, "stackable": false, "oncePerClient": true
}
```

### Regras de confirmação de pedidos

A confirmação segue o mesmo modelo orientado a dados dos descontos. O Orders.Worker avalia as regras ativas em `OrderPlaced`: se alguma regra `Hold` corresponder, o pedido vai para OnHold e aguarda um admin; caso contrário, fica Confirmed. Os admins podem confirmar ou bloquear um pedido Pending ou OnHold, colocar em espera só um pedido Pending (de OnHold o pedido vai apenas para Confirmed ou Blocked) e cancelar só um pedido Confirmed. O seed de desenvolvimento cria uma regra, "Large BRL order" (`TotalAbove` 5000 BRL).

```sql
CREATE TABLE confirmation_rules (
    id          uuid        NOT NULL,
    version     int         NOT NULL,
    name        text        NOT NULL,
    action      text        NOT NULL CHECK (action IN ('Hold')),
    conditions  jsonb       NOT NULL,          -- e.g. TotalAbove {amount, currency}, FirstOrderOfClient, ChannelIs
    priority    int         NOT NULL,
    is_active   boolean     NOT NULL DEFAULT true,
    created_by  text        NOT NULL,
    created_at  timestamptz NOT NULL,
    PRIMARY KEY (id, version)
);
```

As condições reutilizam as classes Specification do motor de descontos. As ações de admin gravam em `order_status_history` com o admin como ator e publicam `OrderStatusChanged.v1`.

## Front-end

Duas páginas, `shop.html` e `admin.html`, construídas com Vite a partir de HTML e TypeScript puros e estilizadas com Bootstrap 5.3 e Bootstrap Icons via npm. Ambas rodam em `http://localhost:8080` atrás do nginx e chamam apenas `/bff/*`. A página de admin é uma entrada separada, então seu código nunca é entregue aos usuários do shop.

A interface está em português do Brasil (`lang="pt-BR"`), em texto simples, sem biblioteca de i18n; valores e datas usam `pt-BR` por padrão. As mensagens de erro que vêm das APIs (ProblemDetails) aparecem como chegam; o front-end só escreve em pt-BR as mensagens que ele mesmo gera.

### Página Shop (fluxos de cliente)

| Tela / estado | O que faz | Chamada ao BFF |
| --- | --- | --- |
| Catálogo | Busca, filtro por categoria, cards de produto paginados com o preço atual | `GET /bff/shop/products` |
| Carrinho | Adicionar, remover, alterar quantidade; cotação ao vivo com subtotal, descontos e total | `POST /bff/shop/cart/quote` |
| Checkout | Confirmar e fazer o pedido com a chave de idempotência armazenada; botão desabilitado durante o envio | `POST /bff/shop/orders` |
| Meus pedidos | Lista com badges de status, "carregar mais" com keyset | `GET /bff/shop/orders` |
| Detalhe do pedido | Itens, itemCount, subtotal, desconto, total, linha do tempo de status | `GET /bff/shop/orders/{id}` |

O estado do carrinho fica no cliente (em memória + `sessionStorage`). Os preços exibidos são sempre os da última cotação; o servidor recalcula na criação do pedido.

### Página Admin (eventos e gestão de pedidos)

| Tela / estado | O que faz | Chamada ao BFF |
| --- | --- | --- |
| Eventos ao vivo | Tabela em streaming de `OrderPlaced.v1`, `OrderStatusChanged.v1` e `DeadLetterReplayed.v1`; filtro por tipo e status; pausar/retomar; reconecta com `Last-Event-ID` | `GET /bff/admin/events/stream` (SSE) + `GET /bff/admin/events` para backfill |
| Inspetor de pedidos | Um pedido com o histórico de status e seus eventos; ação de cancelar | `GET /bff/admin/orders/{id}` |
| Dead letters | Mensagens retidas com o erro e o número de tentativas; replay de uma delas | `/bff/admin/dead-letters/**` |
| Regras de desconto | Listar, criar, editar (nova versão), desativar, pré-visualizar contra um carrinho de exemplo | `/bff/admin/discount-rules/**` |
| Catálogo | Editar produto e preço com confirmação imediata; o 412 mostra "Este produto foi alterado por outra pessoa. Recarregue-o para ver o estado atual." | `/bff/admin/products/**` |
| Ações sobre pedidos | Criar um pedido para um cliente; fila de OnHold (mais antigos primeiro); confirmar, colocar em espera ou bloquear com um motivo | `/bff/admin/orders/**` |
| Regras de confirmação | Listar, criar, editar e desativar regras de confirmação automática (limites por moeda, condições) | `/bff/admin/confirmation-rules/**` |

### Organização do código e acessibilidade

- Cada pasta (`shared`, `shop`, `admin`) tem `api/`, `domain/` e `ui/`. `shared/api`: client fetch tipado, parsing de ProblemDetails, retentativa com a mesma chave de idempotência, wrapper de SSE. `shared/domain`: ciclo de vida da chave de idempotência, mapeamento de status, linha do tempo, formatação com `Intl.NumberFormat`. `shop/domain/cart.ts`: cálculos do carrinho para exibição. `admin/domain`: feed de eventos, ações permitidas por status, regras. `shop/api`, `admin/api`: as chamadas ao BFF de cada página. `shared/ui`, `shop/ui`, `admin/ui`: apenas renderização do DOM.
- Acessibilidade: landmarks semânticos, controles de formulário com rótulo, foco visível, fluxos operáveis apenas pelo teclado, `aria-live="polite"` para status e novos eventos, cores do Bootstrap com contraste seguro, respeito a `prefers-reduced-motion`. O axe-core roda na suíte do Vitest em cada tela renderizada.

## Observabilidade, auditoria e segurança

Todo serviço exporta traces, métricas e logs via OpenTelemetry; a auditoria fica no banco de dados de cada serviço; as rotas de admin exigem autenticação e a role `Admin`.

**Observabilidade**

- O projeto `ServiceDefaults` (`shared/ServiceDefaults/`) configura o OTel com instrumentação de ASP.NET Core e HttpClient (traces e métricas) e de Runtime (métricas), mais o medidor `Orders` na Orders API e no Worker, exportando OTLP para o Collector quando `OTEL_EXPORTER_OTLP_ENDPOINT` está definida (o `docker-compose.yml` ainda não a define). Visualização local: container do Aspire Dashboard (`docker compose --profile observability up`). A instrumentação de EF Core/Npgsql, do driver do MongoDB, do RabbitMQ e do Wolverine, prevista no plano, não foi registrada (trabalho futuro).
- O contexto de trace atravessa o HTTP automaticamente. O lado RabbitMQ ainda não aparece nos traces, porque nenhuma fonte do Wolverine ou do RabbitMQ está registrada; a ideia original de levar o `traceparent` nas linhas de outbox não foi seguida, já que o outbox é do Wolverine.
- Logs: `ILogger` com `LoggerMessage` via source generator, saída JSON no console, trace id em toda entrada. Nada de dados pessoais ou corpos completos de requisição nos logs.
- Métricas customizadas: `orders.created`, `orders.duplicate_replayed`, `orders.on_hold`, `orders.on_hold.oldest_age`, `discount.applied`, `catalog.price_lookup.duration`, `rabbitmq.dlq.depth`, com os limites de alerta em [Métricas e alertas](operations/alerts.md).
- Health: `/health/live` e `/health/ready`, usados pelos healthchecks do compose (o comando `healthcheck` da própria app chama o ready). Só o check `self` está registrado, com a tag `live`; o `/health/ready` ainda não verifica dependências (DB, broker, Catalog acessível) e responde saudável assim que o processo sobe.

**Auditoria**

- Orders: `order_status_history` (ator, motivo, trace id), `discount_rules` versionadas com `created_by`.
- Catalog: `price_history` e `updatedBy` nos produtos.
- As linhas de auditoria são gravadas na mesma transação da alteração que descrevem.

**Segurança**

- BFF: sessão por cookie (HttpOnly, Secure, SameSite=Strict) + header `X-CSRF` em métodos não seguros. O login é provisório: o BFF serve um login de desenvolvimento (`/bff/login`) com os usuários `shopper` e `admin`, senhas vindas do `.env` (`Bff__DevLogin__*`), ligado só no compose de desenvolvimento (`docker-compose.yml`); os compose de staging e de produção não o ligam. As credenciais nunca ficam no código.
- BFF → serviços: o BFF assina para cada serviço um JWT de 5 minutos com o `sub`, o nome e as roles do usuário (HMAC-SHA256, chave `SERVICE_TOKEN_SIGNING_KEY` do `.env`), de modo que a identidade do usuário chega a cada serviço; Orders e Catalog validam issuer, audience, validade e roles. Os serviços não são publicados em portas do host, exceto por meio do nginx.
- **Trabalho futuro:** o identity provider com seed do plano original (container Keycloak, realm em arquivos de realm) não foi construído. A melhoria prevista é OpenID Connect (code flow + PKCE) no BFF e o token exchange do Keycloak no lugar do JWT assinado pelo BFF.
- Validação de entrada com FluentValidation: um validator por command e por query, executado pelo respectivo handler antes de qualquer I/O; limites de tamanho de página (100) e de tamanho de lote (100 SKUs).
- Queries: apenas EF Core LINQ ou interpolação com `FromSql` (parametrizadas); `FromSqlRaw` com concatenação é proibido e EF1002 é erro de build (AC6). O Mongo usa apenas filtros tipados `Builders<T>`.

## Testes e CI/CD

Os testes unitários carregam o peso: pelo menos um teste por caminho de código; os testes de integração continuam poucos e focados no comportamento de banco de dados, broker e idempotência. O gate de cobertura de branches só é cobrado no front-end (`web/vitest.config.ts`); no .NET a cobertura ainda não é coletada (veja o comentário em `.github/workflows/ci.yml` e [Qualidade](quality.md)), então os gates abaixo marcados como planejados não rodam.

| Camada | Ferramenta | Escopo | Gate |
| --- | --- | --- | --- |
| Domain (unitário) | xUnit v3, Shouldly | Agregado Order, máquina de estados, valores monetários, cada estratégia e condição de desconto | Planejado: cobertura de branches ≥ 90% (não cobrado) |
| Application (unitário) | xUnit v3, NSubstitute | Cada caminho de handler de command/query: sucesso, validação, SKU ausente, replay, divergência de hash, conflito de resgate | Planejado: cobertura de branches ≥ 85% (não cobrado) |
| BFF (unitário) | xUnit v3, NSubstitute | Composição, mapeamento, políticas de autorização, serialização SSE | Planejado: cobertura de branches ≥ 80% (não cobrado) |
| Arquitetura | NetArchTest | Regra de dependência, nenhum tipo de Infrastructure em Domain/Application | Deve passar |
| Integração | Testcontainers (Postgres, Mongo, RabbitMQ), WebApplicationFactory | Criação de pedido de ponta a ponta, 10 requisições idênticas concorrentes → 1 pedido + 1 resgate, consulta de preços em lote, retentativa → DLQ após N tentativas, concorrência otimista no Mongo | Deve passar |
| Front-end (unitário) | Vitest, jsdom, axe-core | client de API (retentativa mantém a chave), cálculos do carrinho, ciclo de vida da chave de idempotência, reconexão SSE, cada tela renderiza e não tem violações do axe | Cobertura de branches ≥ 80% |
| Smoke | docker compose + `scripts/smoke.sh` | A stack sobe saudável, o shop faz um pedido, o feed do admin o recebe, a mesma chave devolve o mesmo pedido | Não roda no CI: roda à mão na stack local e no `deploy.yml` depois de cada deploy, contra `vars.APP_URL`; usa o login de desenvolvimento, que só o compose de desenvolvimento liga |

Mantendo os testes enxutos: um comportamento por teste, builders para dados de teste, nada de testar código do framework, nada de mocks de tipos nossos no Domain. O plano previa o Stryker.NET rodando semanalmente no Domain para mostrar que os testes detectam mudanças reais; a etapa C57 (baseline e `mutation.yml`) não foi feita, e o motivo está em [Qualidade](quality.md).

**Pipeline de Git e CI** (GitHub Actions ou equivalente)

1. Trunk-based: uma etapa do Guia de execução por branch curta e PR para a `main`, Conventional Commits, squash merge, review obrigatório.
2. `ci.yml` em todo PR: format, build com warnings como erros, testes unitários + de arquitetura (sem coleta de cobertura no .NET por enquanto), testes de integração com Testcontainers, lint + Vitest do front-end, scans de dependências e de segredos, verificação de que as duas árvores de documentação estão em sincronia (`scripts/check-docs.mjs`) e, por fim, o job obrigatório `gate`.
3. `cd.yml` depois que o `ci` passa na `main`: build das imagens uma única vez, atestação de proveniência, scan com Trivy, aprovação no `deploy-gate`, deploy em staging, smoke test, rollback em caso de falha.
4. Tag de release `v*.*.*`: promover as mesmas imagens, passar pelo `deploy-gate` mais a aprovação de produção, fazer deploy em produção, smoke test. Nenhuma tag foi criada ainda (não existe `v1.0.0`).
5. Banco de dados: o serviço `orders-migrator` roda `dotnet Orders.Api.dll migrate` (imagem de `orders/src/Orders.Infrastructure/Migrator.Dockerfile`), que aplica as migrations do EF Core, cria as tabelas do Wolverine e, no compose de desenvolvimento, faz o seed da regra de confirmação, antes de o `orders-api` iniciar; as migrations continuam retrocompatíveis (expand, depois contract).

**Serviços do Docker Compose:** `nginx` (web + proxy reverso, :8080), `orders-bff`, `orders-api`, `orders-worker`, `orders-migrator`, `catalog-api`, `postgres`, `mongo` (replica set `rs0` + script de init), `rabbitmq` (UI de gerenciamento, arquivo de definitions para exchanges, filas, DLX), no profile `observability`: `otel-collector`, `aspire-dashboard`, e no profile `load`: `k6`. Todos com healthchecks e `depends_on: condition: service_healthy`; dados em volumes nomeados. Seed só no compose de desenvolvimento: 50 produtos no Catalog (preços em BRL e USD) e uma regra de confirmação, "Large BRL order" (`TotalAbove` 5000 BRL); nenhuma regra de desconto. O `keycloak` do plano original não existe (veja Segurança). Staging e produção usam `deploy/compose.staging.yml` e `deploy/compose.production.yml`, sem seed e sem login de desenvolvimento.

## Plano de implementação em fases

Oito fases, cada uma terminando com a `main` mergeada e verde e um resultado demonstrável. As fases 1 e 2 podem rodar em paralelo assim que a Fase 0 for entregue, porque o Orders é codificado primeiro contra um stub do contrato do Catalog.

Etapas commit a commit e a configuração do GitHub Actions: [Guia de execução](02-execution-guide.md). Regras de review: [Guia de code review](03-code-review-guide.md). Esforço, equipe e dimensionamento: [Dimensionamento técnico](04-tech-dimensioning.md). Ferramenta e processo de IA: [Desenvolvimento assistido por IA](05-ai-development.md).

### Fase 0 — Fundações

- [x] Criar o repositório, `.gitignore`, `.gitattributes` (`*.sh text eol=lf`), `global.json`, `Directory.Build.props`, `Directory.Packages.props`
- [x] Criar o scaffold de `Orders.slnx` e `Catalog.slnx` com projetos em Clean Architecture e projetos de teste vazios
- [x] `ServiceDefaults` (em `shared/ServiceDefaults/`): OTel, health checks, ProblemDetails, padrões de resiliência
- [x] Infraestrutura no Compose: postgres, mongo (init do replica set), rabbitmq (arquivo de definitions), nginx, profile otel
- [x] Pipeline de CI: build, verificação de formatação, testes unitários, placeholder do job de front-end
- [ ] Gates de cobertura do .NET — não feitos: a cobertura não é coletada (veja [Qualidade](quality.md))
- [x] Testes de arquitetura para a regra de dependência

Pronto quando: `docker compose up` sobe a infraestrutura saudável e o CI fica verde com uma solution vazia.

### Fase 1 — Serviço Catalog

- [x] Agregado Product com preço em `Decimal128`, moeda e versão
- [x] Repositório Mongo com filtros tipados e índices criados na inicialização
- [x] `GET /products`, `GET /products/{sku}` com `ETag`
- [x] `POST /products/prices:query` (uma única query `$in`, projeção, no máximo 100 SKUs)
- [x] Admin `PUT /admin/products/{sku}`, `PATCH .../price` com `If-Match` + `price_history` em uma única transação
- [x] Dados de seed (50 produtos), documento OpenAPI gerado no build em `catalog/openapi/catalog.v1.json`
- [x] Testes unitários por caminho + testes de integração (Testcontainers Mongo): query em lote, 412 em versão desatualizada

Pronto quando: o Catalog roda no compose, o OpenAPI é publicado e uma edição de preço retorna o novo recurso imediatamente.

### Fase 2 — Núcleo do Orders

- [x] Domain: agregado `Order`, `OrderItem`, `Money`, máquina de estados de status, `itemCount`
- [x] Mapeamentos do EF Core + primeira migration (pedidos, itens, histórico, idempotência); outbox e inbox ficam nas tabelas do próprio Wolverine
- [x] Client do Catalog gerado a partir do OpenAPI (NSwag) + handler de resiliência
- [x] Command `CreateOrder` com o fluxo idempotente de seis passos
- [x] `GET /orders` (filtros, `q` full-text, paginação keyset), `GET /orders/{id}`, `/history`, `POST /orders/quote`
- [x] Testes unitários para cada caminho de handler; teste de integração com 10 requisições idênticas concorrentes

Pronto quando: um pedido pode ser criado, listado e consultado; duplicatas fazem o replay da resposta armazenada.

### Fase 3 — Motor de descontos e regras de admin

- [x] Estratégias: Percentage, FixedAmount, BuyXGetY, Tiered; specifications de condição
- [x] Motor com prioridade, stackable e teto; `TimeProvider` injetado
- [x] Migrations de `discount_rules` versionadas, `order_discounts` e `discount_redemptions`
- [x] Endpoints de admin + preview; `IMemoryCache` de 60 s com invalidação na escrita (no lugar do `HybridCache`)
- [x] Testes: cada estratégia e condição, ordem de acúmulo, teto, conflito de once-per-client → 409

Pronto quando: um admin altera uma regra e a próxima cotação a reflete sem deploy.

### Fase 4 — Mensageria, retentativas e DLQ

- [x] Outbox durável do Wolverine publicando `OrderPlaced.v1` e `OrderStatusChanged.v1` (contratos em `Orders.Contracts`) com publisher confirms; o `OrderFailed.v1` do plano não existe
- [x] Topologia de exchanges/filas: topic exchange `orders.events`, quorum queue `orders.worker.placed`, retentativas agendadas pelo Wolverine de 5 s / 30 s / 5 min a partir do inbox durável (sem filas de retentativa com TTL), depois a fila de retenção `orders.dlq` via `orders.dlx`
- [x] `Orders.Worker`: deduplicação via inbox, transições de status Pending → Confirmed / OnHold pelas regras de confirmação, linhas de histórico
- [x] Admin `GET /admin/dead-letters` e replay (registrado como `DeadLetterReplayed.v1` no log de eventos); `GET /admin/events`
- [x] Testes de integração: reentrega processada uma única vez; poison message cai na DLQ após a última retentativa; o replay funciona

Pronto quando: derrubar o worker no meio do fluxo não perde nada e não processa nada duas vezes.

### Fase 5 — BFF e autenticação

- [x] Host `Orders.Bff` com rotas YARP e endpoints compostos
- [x] Sessão por cookie + antiforgery (`X-CSRF`); login de desenvolvimento do BFF (`/bff/login`) com `shopper` e `admin`
- [ ] Realm de dev do Keycloak e token exchange — não feitos; no lugar, o login de desenvolvimento acima e o JWT de serviço assinado pelo BFF (veja Segurança)
- [x] JWT de serviço de 5 minutos para Orders e Catalog; políticas de role nas rotas de admin
- [x] Assinatura no RabbitMQ + endpoint SSE `/bff/admin/events/stream` com backfill via `Last-Event-ID`
- [x] Testes unitários para mapeamento, composição e políticas; teste de integração para SSE

Pronto quando: as rotas de shop e de admin funcionam de ponta a ponta pelo nginx com as roles corretas.

### Fase 6 — Front-end

- [x] Setup multipágina do Vite, Bootstrap 5.3 + Icons via npm, client de API compartilhado
- [x] Shop: catálogo, carrinho + cotação, checkout com chave de idempotência, meus pedidos, detalhe do pedido
- [x] Admin: eventos ao vivo, inspetor de pedidos, dead letters, regras de desconto, regras de confirmação, fila de OnHold, criação de pedido, editor de catálogo
- [x] Testes com Vitest + jsdom + axe para cada tela e cada caminho do lado do cliente
- [x] Configuração do nginx servindo `dist/` e fazendo proxy de `/bff`

Pronto quando: um comprador faz um pedido em `localhost:8080` e o admin o vê chegar ao vivo.

### Fase 7 — Hardening

- [x] Smoke test do Compose em `scripts/smoke.sh`, rodado à mão e no `deploy.yml` depois de cada deploy
- [ ] Smoke test do Compose no CI na `main` — não feito
- [x] Métricas customizadas e limites de alerta documentados em [Métricas e alertas](operations/alerts.md), incluindo a profundidade da DLQ
- [ ] Dashboards próprios para as métricas customizadas — não feitos; o Aspire Dashboard mostra as métricas
- [x] Revisão de queries: `EXPLAIN ANALYZE` nas queries de listagem e de busca (`docs/perf/`, índice `ix_orders_created` na migration 0004); confirmar uma chamada ao Catalog por pedido
- [x] Teste de carga com k6 (`tests/load/orders.js`, profile `load`)
- [ ] Baseline do Stryker.NET no Domain — não feito (etapa C57; veja [Qualidade](quality.md))
- [x] README: execução, testes, diagrama de arquitetura, ADRs para BFF, Wolverine e replica set do Mongo

Pronto quando: todo critério de aceite da tabela abaixo tem um teste passando ou uma verificação documentada.

## Rastreabilidade, riscos e questões em aberto

Todo requisito e critério de aceite é mapeado para uma fase e um teste de verificação.

| Item | Coberto por | Fase | Verificado por |
| --- | --- | --- | --- |
| Req 1 Banco relacional para o Orders | PostgreSQL + EF Core | 0, 2 | Testes de integração |
| Req 2 Retentativas + DLQ no RabbitMQ, tabelas de idempotência | Retentativas agendadas do Wolverine, `orders.dlq`, tabelas de idempotência/inbox/resgate | 2, 4 | Testes de integração de reentrega e DLQ |
| Req 3 Catalog é dono dos produtos e preços | Serviço Catalog, autoridade de preços | 1 | Testes unitários do Catalog |
| Req 4 Orders consome o Catalog diretamente | Client REST gerado | 2 | Teste de integração de criação de pedido |
| Req 5 Clean Code + Clean Architecture | Projetos em camadas, camadas em `web/` | 0 | NetArchTest, lint |
| AC1 Criar, listar e consultar pedidos | Endpoints do Orders | 2 | Testes de handler + endpoint |
| AC2 Campos do pedido (amount = quantidade) | `item_count`, campos monetários, status | 2 | Testes do Domain |
| AC3 Idempotência para pedidos e descontos | Fluxo de seis passos, restrições de unicidade | 2, 3 | Teste de integração de concorrência, testes de conflito |
| AC4 Suíte unitária enxuta, um teste por caminho | Gate de cobertura do front-end; gates do .NET e Stryker não feitos | todas | Gates de CI (só o do front-end); veja [Qualidade](quality.md) |
| AC5 Descontos configuráveis | Regras versionadas, API de admin | 3 | Testes de estratégia e do motor |
| AC6 Queries parametrizadas | EF Core LINQ/`FromSql`, EF1002 como erro | 2 | O build falha em caso de violação |
| AC7 Testes para pedidos, catálogo e front-end | xUnit, Vitest | 1, 2, 6 | CI |
| AC8 Git | Trunk-based, PRs, Conventional Commits | 0 | Proteção de branch |
| AC9 Consultas de produtos com preços atuais | `GET /products` | 1 | Testes do Catalog |
| AC10 API REST do Catalog para o Orders | Catalog API + OpenAPI | 1 | Contrato via client gerado |
| AC11 Verificações de preço eficientes | Uma chamada `$in` em lote por pedido | 1, 2 | Teste verifica uma única chamada; revisão com `EXPLAIN` |
| AC12 Front-end moderno e acessível em localhost | Shop + Admin via BFF na :8080 | 6 | Vitest + axe, smoke test |

### Riscos

| Risco | Impacto | Mitigação |
| --- | --- | --- |
| Catalog fora do ar bloqueia a criação de pedidos | Nenhum pedido novo | Circuit breaker, ProblemDetails 503 claro; o indicador de health na página Admin não foi feito |
| Preços mudam entre a cotação e o pedido | O cliente vê um total diferente | O pedido usa o preço do servidor e a resposta traz o total gravado; o 409 com a nova cotação quando o total difere da última não foi feito |
| O Wolverine não atende a alguma necessidade | Retrabalho da camada de mensageria | Manter os handlers atrás de ports da Application; a alternativa é Mediator + RabbitMQ.Client 7 |
| Mongo sem replica set em algum ambiente | Sem transações para o histórico de preços | Script de init no Compose; a verificação na inicialização falha rápido |
| O BFF acumula lógica de domínio | Regras duplicadas | Teste de arquitetura: o BFF referencia apenas contratos; checklist de review |
| A suíte de testes incha | CI lento, testes frágeis | Um comportamento por teste, builders, mutation testing em vez de contagens brutas |

### Decisões (antigas questões em aberto)

- **Moeda:** multimoeda, uma moeda por pedido, preço por SKU por moeda, BRL como padrão.
- **Quem cria pedidos:** compradores, para si mesmos, e admins, em nome de um cliente.
- **Confirmação:** automática por regra no Orders.Worker; os admins confirmam, colocam em espera ou bloqueiam pela página Admin.
- **Retenção:** chaves de idempotência por 24 h, `order_event_log` por 30 dias (job de retenção no Worker); as linhas de outbox ficam com o Wolverine.
- **Plataforma de CI:** GitHub Actions, com um gate obrigatório antes de todo deploy.
