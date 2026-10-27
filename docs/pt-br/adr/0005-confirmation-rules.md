# ADR 0005 — Confirmação de pedidos por regras configuráveis

**Status:** aceito

## Contexto

Alguns pedidos (valor alto, primeiro pedido de um cliente, certo canal) precisam de uma pessoa antes de serem confirmados; os demais devem ser confirmados sozinhos. A regra muda com o negócio, sem novo deploy.

## Decisão

As regras de confirmação são dados versionados (`confirmation_rules`, chave `(id, version)`), com o mesmo modelo dos descontos e as mesmas condições (`TotalAbove`, `FirstOrderOfClient`, `ChannelIs`...), e uma única ação, `Hold`. O Orders.Worker avalia as regras ativas ao consumir `OrderPlaced.v1`: se alguma `Hold` casar, o pedido vai para `OnHold` e espera um admin; senão, fica `Confirmed`. Os admins confirmam, retêm, bloqueiam ou cancelam pela página Admin enquanto o pedido estiver `Pending` ou `OnHold`; toda mudança grava `order_status_history` (com o ator) e publica `OrderStatusChanged.v1`. As regras ativas ficam em cache por processo (`Orders__Confirmation__CacheDuration`, 5 s): a escrita de um admin esvazia o cache da API e o Worker espera esse tempo.

## Consequências

- Mudar a regra é editar dados, com histórico de versões e prévia; nenhum deploy.
- Um pedido retido espera por uma pessoa: por isso existe o alerta de "OnHold há mais de 4 h" (veja [Métricas e alertas](../operations/alerts.md)).
- O cache é por instância; várias instâncias convergem dentro do tempo de cache.
- A regra de desenvolvimento "reter pedidos em BRL acima de 5.000,00" só entra com `Orders__Confirmation__SeedEnabled`, nunca em staging ou produção.
- Segmentos de cliente ainda não têm fonte de dados: a condição `ClientSegment` nunca casa.

## Alternativas consideradas

- **Regras no código:** exigiria deploy a cada mudança.
- **Confirmação manual para tudo:** inviável no volume previsto (50.000 pedidos por mês).
