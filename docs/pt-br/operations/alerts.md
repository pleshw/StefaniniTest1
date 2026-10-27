# Métricas e alertas

Os serviços do Orders exportam métricas customizadas por OpenTelemetry (medidor `Orders`). O Aspire Dashboard, do profile `observability`, mostra os valores; a pilha não tem um motor de alertas, então este documento fixa **os limites** que qualquer motor (ou uma pessoa olhando o painel) deve usar. Os nomes abaixo são os que aparecem no painel.

## Métricas customizadas

| Métrica | Tipo | Unidade | Tags | Quem emite |
| --- | --- | --- | --- | --- |
| `orders.created` | contador | `{order}` | `channel`, `currency` | Orders API, depois que a transação do pedido confirma |
| `orders.duplicate_replayed` | contador | `{request}` | `channel` | Orders API, quando a resposta vem de uma chave de idempotência já guardada |
| `discount.applied` | contador | `{discount}` | `currency`, `once_per_client` | Orders API, um por desconto aplicado a um pedido criado (a cotação não conta) |
| `catalog.price_lookup.duration` | histograma | `ms` | `outcome` (`ok` ou `error`) | Orders, em cada consulta de preços em lote ao Catalog (retentativas incluídas) |
| `orders.on_hold` | gauge | `{order}` | nenhuma | Orders Worker, lido do banco a cada 30 s |
| `orders.on_hold.oldest_age` | gauge | `s` | nenhuma | Orders Worker; há quanto tempo o pedido `OnHold` mais antigo espera (sem série quando não há pedido retido) |
| `rabbitmq.dlq.depth` | gauge | `{message}` | nenhuma | Orders Worker, lido do RabbitMQ a cada 30 s (sem série quando a fila não existe ou o broker não responde) |

Os três gauges são lidos pelo Worker, não pela API, para não repetir a leitura em cada instância da API. Com mais de uma instância do Worker cada uma reporta o mesmo valor: use o **máximo**, nunca a soma. O intervalo é `Orders__Metrics__PollInterval` (padrão `00:00:30`).

## Alertas

| Alerta | Condição | Janela | Por que importa | Primeiro passo |
| --- | --- | --- | --- | --- |
| Mensagens na DLQ | `rabbitmq.dlq.depth` > 0 | por 10 minutos seguidos | Uma mensagem que o Worker desistiu de processar não vai andar sozinha: um pedido pode ficar sem confirmação | Abrir "Mensagens mortas" no Admin, ler o erro e fazer o replay depois de corrigir a causa |
| Pedido retido há muito tempo | `orders.on_hold.oldest_age` > 14.400 (4 h) | em qualquer leitura | Um pedido em `OnHold` espera uma pessoa; passou de 4 h, ninguém está olhando a fila | Abrir a fila "Em espera" no Admin e confirmar, bloquear ou cancelar |
| Criação de pedido lenta | p95 de `http.server.request.duration` do `orders-api`, com `http.request.method` = `POST` e `http.route` = `/api/v1/orders`, > 0,3 s | 5 minutos | É a meta de latência do [dimensionamento](../04-tech-dimensioning.md); acima disso o checkout degrada | Olhar `catalog.price_lookup.duration` (o Catalog está lento ou fora?) e o trace de um pedido lento |

Duas leituras ajudam a não confiar num painel vazio: se `orders.on_hold` ou `rabbitmq.dlq.depth` **some** por mais de 10 minutos, a leitura do Worker está falhando (o log do Worker diz qual) ou o Worker parou, e isso deve ser tratado como alerta. E `catalog.price_lookup.duration` com p95 acima de 50 ms é a meta de latência do lote de preços; ela não vira alerta aqui porque o teste de carga já a cobra, mas o painel mostra a tendência.
