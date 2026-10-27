# ADR 0003 — MongoDB como replica set, mesmo com um nó

**Status:** aceito

## Contexto

Mudar um preço no Catalog precisa atualizar o produto e gravar o histórico de preços (`price_history`) juntos: ou as duas coisas acontecem, ou nenhuma. Transações no MongoDB só existem em replica set.

## Decisão

O MongoDB 8 roda como replica set de um nó (`rs0`) no Docker Compose, iniciado por um script de init. A alteração de preço é uma transação que atualiza `products` com filtro em `{ sku, version }` e insere em `price_history`. Uma versão divergente responde 412 (`If-Match`). A verificação na inicialização do Catalog falha rápido se o replica set não existir.

## Consequências

- O histórico de preços nunca fica sem o preço correspondente (há teste de atomicidade: se a inserção do histórico falhar, a mudança de preço é desfeita).
- Todo ambiente precisa iniciar o replica set; um MongoDB avulso não serve.
- Um nó só não dá alta disponibilidade; em produção a decisão de quantos nós usar é de operação, e o código não muda.

## Alternativas consideradas

- **Sem transação (duas escritas):** admite uma janela com preço novo e sem histórico.
- **Histórico no mesmo documento:** o documento cresceria sem limite e a auditoria ficaria misturada com o dado atual.
