# ADR 0002 — Wolverine para CQRS, outbox e RabbitMQ

**Status:** aceito

## Contexto

O Orders precisa de handlers de comando e consulta, de publicação transacional de eventos (sem escrita dupla no banco e no broker), de consumo idempotente com retentativas e de uma fila de mensagens mortas (DLQ), tudo sobre PostgreSQL e RabbitMQ.

## Decisão

Usar o Wolverine (licença MIT): handlers, outbox e inbox duráveis no PostgreSQL (schema `wolverine`) e o transporte do RabbitMQ. A API grava o pedido, a chave de idempotência e o evento `OrderPlaced.v1` na mesma transação; o evento sai pelo outbox. O Worker consome de uma quorum queue, com retentativas após 5 s, 30 s e 5 min, e depois envia a mensagem para `orders.dlq` por `orders.dlx` com os cabeçalhos do erro. Os handlers ficam atrás de ports da camada Application, e os tipos do Wolverine ficam em Infrastructure e nos hosts.

## Consequências

- Nenhum evento é perdido se o broker estiver fora do ar na hora da gravação (outbox durável em todos os endpoints de evento), e o consumo repetido de uma mesma mensagem é tratado uma vez (inbox).
- As tabelas do Wolverine pertencem ao Wolverine; `outbox_messages` e `inbox_messages` do modelo de dados descrevem só o formato.
- Pontos em aberto, à espera de decisão: o pacote de compilação em tempo de execução do Wolverine (C17), o aviso "no x-delivery-limit" da fila `orders.worker.placed` e o fato de que essa fila só existe depois da primeira partida do Worker.
- O custo de sair do Wolverine é limitado pelos ports da Application.

## Alternativas consideradas

- **Mediator com source generator + RabbitMQ.Client 7:** mais código para outbox, inbox e retentativas. Continua sendo a saída se o Wolverine deixar de atender.
