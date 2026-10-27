import { listDeadLetters, replayDeadLetter, type DeadLetter } from '../api/deadLetters';
import type { HttpClient } from '../../shared/api/http';
import { confirmAction } from '../../shared/ui/dialog';
import { h } from '../../shared/ui/dom';
import { withIcon } from '../../shared/ui/icons';
import { errorMessage } from '../../shared/ui/errors';
import { actionButton } from '../../shared/ui/form';

export interface DeadLettersOptions {
  readonly http: HttpClient;
}

/** The messages parked in the dead-letter queue, with their errors, and a replay behind a confirmation. */
export function mountDeadLetters(container: HTMLElement, options: DeadLettersOptions): void {
  let letters: readonly DeadLetter[] = [];
  let loaded = false;
  let failure: string | undefined;
  let notice = '';
  let generation = 0;

  const status = h('p', { attrs: { role: 'status' } });
  const message = h('p', { className: 'fw-semibold', attrs: { role: 'status' } });
  const alertBox = h('div', {
    className: 'alert alert-danger d-flex align-items-center gap-3',
    attrs: { role: 'alert' },
  });
  const body = h('tbody');
  const table = h(
    'table',
    { className: 'table align-middle' },
    h('caption', { className: 'visually-hidden', text: 'Mensagens mortas' }),
    h(
      'thead',
      {},
      h(
        'tr',
        {},
        ...['Mensagem', 'Tipo', 'Destino', 'Erro', 'Tentativas', 'Ações'].map((name) =>
          h('th', { text: name, attrs: { scope: 'col' } }),
        ),
      ),
    ),
    body,
  );
  const refresh = actionButton(
    'Atualizar',
    'btn btn-outline-secondary btn-sm mb-2',
    () => void load(),
  );

  container.append(
    h(
      'section',
      { attrs: { 'aria-labelledby': 'dead-letters-heading' } },
      withIcon(
        h('h2', {
          className: 'h4',
          text: 'Mensagens mortas',
          attrs: { id: 'dead-letters-heading' },
        }),
        'bi-envelope-exclamation',
      ),
      h('p', {
        className: 'text-body-secondary',
        text: 'Mensagens que o worker não conseguiu processar após todas as novas tentativas. Reprocessar envia a mensagem de novo pela sua exchange e a remove desta fila.',
      }),
      refresh,
      message,
      status,
      alertBox,
      table,
    ),
  );

  function render(): void {
    body.replaceChildren(...letters.map(rowOf));
    table.hidden = letters.length === 0;
    message.hidden = notice === '';
    message.textContent = notice;
    status.textContent = !loaded
      ? 'Carregando a fila de mensagens mortas…'
      : failure !== undefined
        ? ''
        : letters.length === 0
          ? 'A fila de mensagens mortas está vazia.'
          : `${String(letters.length)} ${letters.length === 1 ? 'mensagem' : 'mensagens'} na fila de mensagens mortas`;
    alertBox.hidden = failure === undefined;
    alertBox.replaceChildren(
      ...(failure === undefined
        ? []
        : [
            h('span', { text: failure }),
            actionButton(
              'Tentar novamente',
              'btn btn-sm btn-outline-danger ms-auto',
              () => void load(),
            ),
          ]),
    );
  }

  function rowOf(letter: DeadLetter): HTMLElement {
    const replay = actionButton(
      'Reprocessar',
      'btn btn-outline-primary btn-sm',
      () => void replayLetter(letter),
    );
    replay.setAttribute('aria-label', `Reprocessar ${letter.messageId}`);
    const headers = Object.entries(letter.headers).map(([name, value]) => `${name}: ${value}`);

    return h(
      'tr',
      {},
      h(
        'th',
        { attrs: { scope: 'row' } },
        h('div', { text: letter.messageId }),
        h(
          'details',
          {},
          h('summary', { text: 'Detalhes' }),
          h(
            'div',
            { attrs: { role: 'group', 'aria-label': `Detalhes de ${letter.messageId}` } },
            h('h3', { className: 'h6 mt-2', text: 'Payload' }),
            h('pre', { className: 'small', text: letter.payload }),
            h('h3', { className: 'h6', text: 'Cabeçalhos' }),
            h('pre', { className: 'small', text: headers.join('\n') }),
          ),
        ),
      ),
      h('td', { text: letter.messageType ?? 'Desconhecido' }),
      h('td', { text: letter.originalDestination ?? 'Desconhecido' }),
      h(
        'td',
        {},
        h('div', { text: letter.exceptionType ?? 'Desconhecido' }),
        h('small', { className: 'text-body-secondary', text: letter.exceptionMessage ?? '' }),
      ),
      h('td', { text: letter.attempts === null ? 'Desconhecido' : String(letter.attempts) }),
      h('td', {}, replay),
    );
  }

  async function load(): Promise<void> {
    const mine = ++generation;
    failure = undefined;
    try {
      const found = await listDeadLetters(options.http);
      if (mine !== generation) {
        return;
      }
      letters = found;
    } catch (error) {
      if (mine !== generation) {
        return;
      }
      failure = errorMessage(error, 'Não foi possível ler a fila de mensagens mortas.');
    }
    loaded = true;
    render();
  }

  async function replayLetter(letter: DeadLetter): Promise<void> {
    const yes = await confirmAction({
      title: 'Reprocessar esta mensagem?',
      message: `A mensagem ${letter.messageId} volta pela sua exchange e sai da fila de mensagens mortas.`,
      confirmLabel: 'Reprocessar',
    });

    if (!yes) {
      return;
    }
    let problem: string | undefined;
    notice = '';
    try {
      const done = await replayDeadLetter(options.http, letter.messageId);
      notice = `${letter.messageId} reprocessada como ${done.newMessageId} pela exchange ${done.exchange} (${done.routingKey}).`;
    } catch (error) {
      problem = errorMessage(error, 'Não foi possível reprocessar a mensagem.');
    }
    await load();
    if (problem !== undefined) {
      failure = problem;
    }
    render();
  }

  render();
  void load();
}
