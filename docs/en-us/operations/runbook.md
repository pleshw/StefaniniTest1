# Operations runbook

Steps for the three situations that most often need a person: messages in the DLQ, rule changes and rolling back a version. The alerts that call the person are in [Metrics and alerts](alerts.md).

## Replaying the DLQ

A message reaches `orders.dlq` after failing three retries (5 s, 30 s and 5 min) in the Worker. It will not move by itself.

1. Open the Admin, "Mensagens mortas" section (`/admin.html`; the Admin screen is in Portuguese). The list shows, per message, the type, the queue it came from, the number of attempts and the error (`x-exception-type`, `x-exception-message`). Looking at the list takes no message out of the queue.
2. **Fix the cause before replaying.** Replaying a message that will fail again only puts it back in the DLQ, after three more retries.
3. Choose "Reprocessar" (replay) on the message and confirm. Orders publishes a copy with a new `message-id` (the Worker's inbox remembers the old one) to the exchange and routing key of the message's type, without the failure headers and with `x-replayed-by` (who asked) and `x-original-message-id`; only after the broker confirms the publish does the message leave the DLQ. The replay is recorded in the event log (`DeadLetterReplayed.v1`).
4. If Orders refuses the replay, or cannot record it, the message goes back to the DLQ untouched; the error shows on the screen.
5. Check that `rabbitmq.dlq.depth` is back to 0 and that the order moved on (Confirmed or OnHold).

If the list does not open, Orders or RabbitMQ is down (the screen shows the service's error message, for example "Messaging unavailable", with a "Tentar novamente" (Try again) button); check the compose health (`docker compose ps`) before insisting.

## Rule changes

Discount and confirmation rules are data edited in the Admin ("Regras de desconto" and "Regras de confirmação" sections).

- **Every save creates a new version** of the rule; the version open on the screen is the one sent in `If-Match`. If someone else saved first, the screen warns ("Esta regra foi alterada por outra pessoa. Recarregue a lista e abra-a novamente.", that is, someone else changed this rule) and reloads the list.
- **Discounts:** before activating, use "Simular esta regra" in the editor (or the "Simulação" item under "Regras de desconto" in the Admin menu); it shows the quote of a sample cart with the rule and whether it applied, without saving. An order already created is not recalculated, and a repeat of the same `Idempotency-Key` returns the stored answer.
- **Confirmation rules:** the API uses the new rule at once; the Worker, after `Orders__Confirmation__CacheDuration` (5 s by default). Orders already held stay held until an admin acts.
- **To undo:** save the previous values again (that creates another version) or deactivate the rule ("Desativar"). There is no delete.
- After activating a confirmation rule that holds orders, watch `orders.on_hold` and the "Em espera" (OnHold) queue: someone has to work it (see the 4 h alert).

## Rolling back

Database migrations are compatible with the previous version (they only add; destructive changes need an ADR and two releases), so rolling back a version does not require rolling back the database.

- **Automatic:** in staging and production `deploy.yml` runs the smoke test after the deploy and, if it fails, runs `scripts/deploy.sh <environment> --rollback`, which re-applies the images of the previous release (`releases/previous.env`). **Caution:** the smoke test signs in through the development sign-in, which only `docker-compose.yml` turns on; in staging and production it fails, so today every deploy to those environments would end in a rollback until the smoke test gets another way to authenticate.
- **Manual:** `bash scripts/deploy.sh staging --rollback` (or `production`), with the same SSH key and host as the deploy. Only one previous release is kept; if there is none, the script says so and exits with an error (code 1), changing nothing.
- **A bad version reached production:** roll back first; then fix on `main` and release a new tag. A `v*` tag never rebuilds images: it promotes the images that already passed staging.
- **If the cause is an index created by a migration** (for example `ix_orders_created`), the index can be dropped without losing data (`DROP INDEX`), and the previous code keeps working.
- After any rollback, check the container health on the host (`docker compose ps`; each service's `/health/ready` is not routed through nginx), the smoke test and the alerts.
