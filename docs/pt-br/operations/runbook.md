# Runbook de operação

Passos para as três situações que mais pedem uma pessoa: mensagens na DLQ, mudança de regras e reversão de uma versão. Os alertas que chamam a pessoa estão em [Métricas e alertas](alerts.md).

## Replay da DLQ

Uma mensagem chega a `orders.dlq` depois de falhar três retentativas (5 s, 30 s e 5 min) no Worker. Ela não anda sozinha.

1. Abra o Admin, seção "Mensagens mortas" (`/admin.html`; a tela do Admin é em português). A lista mostra, por mensagem, o tipo, a fila de origem, o número de tentativas e o erro (`x-exception-type`, `x-exception-message`). Olhar a lista não tira nenhuma mensagem da fila.
2. **Corrija a causa antes de reenviar.** Reenviar uma mensagem que vai falhar de novo só a devolve à DLQ, depois de mais três retentativas.
3. Escolha "Reprocessar" na mensagem e confirme. O Orders publica uma cópia com um novo `message-id` (o inbox do Worker lembra do antigo) na exchange e na routing key do tipo da mensagem, sem os cabeçalhos de falha e com `x-replayed-by` (quem pediu) e `x-original-message-id`; só depois que o broker confirma a publicação a mensagem sai da DLQ. O replay fica registrado no log de eventos (`DeadLetterReplayed.v1`).
4. Se o Orders recusar o replay, ou se não conseguir registrá-lo, a mensagem volta para a DLQ intacta; o erro aparece na tela.
5. Confira que `rabbitmq.dlq.depth` voltou a 0 e que o pedido seguiu (Confirmed ou OnHold).

Se a lista não abre, o Orders ou o RabbitMQ está fora do ar (a tela mostra "serviço indisponível"); veja o health do compose (`docker compose ps`) antes de insistir.

## Mudanças de regras

As regras de desconto e de confirmação são dados editados no Admin (seções "Regras de desconto" e "Regras de confirmação").

- **Cada gravação cria uma nova versão** da regra; a versão aberta na tela é a que vai no `If-Match`. Se outra pessoa gravou antes, a tela avisa ("alterado por outra pessoa") e recarrega a lista.
- **Descontos:** use "Simular regra" antes de ativar; ela mostra a cotação com a regra, sem gravar. Um pedido já criado não é recalculado, e uma repetição da mesma `Idempotency-Key` devolve a resposta guardada.
- **Regras de confirmação:** a API passa a usar a nova regra na hora; o Worker, depois de `Orders__Confirmation__CacheDuration` (5 s por padrão). Pedidos já retidos continuam retidos até um admin agir.
- **Para desfazer:** grave de novo os valores anteriores (isso cria outra versão) ou desative a regra ("Desativar"). Não há exclusão.
- Depois de ativar uma regra de confirmação que retém pedidos, acompanhe `orders.on_hold` e a fila "Em espera": alguém precisa tratá-la (veja o alerta de 4 h).

## Reversão (rollback)

As migrations do banco são compatíveis com a versão anterior (só adicionam; mudanças destrutivas exigem um ADR e duas releases), então reverter a versão não exige reverter o banco.

- **Automática:** em staging e em produção o `deploy.yml` roda o smoke test depois do deploy e, se falhar, executa `scripts/deploy.sh <ambiente> --rollback`, que reaplica as imagens da release anterior (`releases/previous.env`).
- **Manual:** `bash scripts/deploy.sh staging --rollback` (ou `production`), com a mesma chave SSH e o mesmo host do deploy. Só existe uma release anterior guardada; se não houver, o script avisa e não faz nada.
- **Uma versão ruim chegou à produção:** reverta primeiro; depois corrija na `main` e libere uma tag nova. Uma tag `v*` nunca reconstrói imagens: ela promove as imagens que já passaram em staging.
- **Se a causa for um índice criado por migration** (por exemplo `ix_orders_created`), o índice pode ser removido sem perda de dados (`DROP INDEX`), e o código anterior continua funcionando.
- Depois de qualquer reversão, confira `/health/ready`, o smoke test e os alertas.
