import { listDeadLetters, replayDeadLetter, type DeadLetter } from '../api/deadLetters';
import type { HttpClient } from '../../shared/api/http';
import { confirmAction } from '../../shared/ui/dialog';
import { h } from '../../shared/ui/dom';
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
    h('caption', { className: 'visually-hidden', text: 'Dead letters' }),
    h(
      'thead',
      {},
      h(
        'tr',
        {},
        ...['Message', 'Type', 'Destination', 'Error', 'Attempts', 'Actions'].map((name) =>
          h('th', { text: name, attrs: { scope: 'col' } }),
        ),
      ),
    ),
    body,
  );
  const refresh = actionButton(
    'Refresh',
    'btn btn-outline-secondary btn-sm mb-2',
    () => void load(),
  );

  container.append(
    h(
      'section',
      { attrs: { 'aria-labelledby': 'dead-letters-heading' } },
      h('h2', { className: 'h4', text: 'Dead letters', attrs: { id: 'dead-letters-heading' } }),
      h('p', {
        className: 'text-body-secondary',
        text: 'Messages the worker could not process after every retry. A replay sends the message through its exchange again and removes it from this queue.',
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
      ? 'Loading the dead-letter queue…'
      : failure !== undefined
        ? ''
        : letters.length === 0
          ? 'The dead-letter queue is empty.'
          : `${String(letters.length)} ${letters.length === 1 ? 'message' : 'messages'} in the dead-letter queue`;
    alertBox.hidden = failure === undefined;
    alertBox.replaceChildren(
      ...(failure === undefined
        ? []
        : [
            h('span', { text: failure }),
            actionButton('Try again', 'btn btn-sm btn-outline-danger ms-auto', () => void load()),
          ]),
    );
  }

  function rowOf(letter: DeadLetter): HTMLElement {
    const replay = actionButton(
      'Replay',
      'btn btn-outline-primary btn-sm',
      () => void replayLetter(letter),
    );
    replay.setAttribute('aria-label', `Replay ${letter.messageId}`);
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
          h('summary', { text: 'Details' }),
          h(
            'div',
            { attrs: { role: 'group', 'aria-label': `Details of ${letter.messageId}` } },
            h('h3', { className: 'h6 mt-2', text: 'Payload' }),
            h('pre', { className: 'small', text: letter.payload }),
            h('h3', { className: 'h6', text: 'Headers' }),
            h('pre', { className: 'small', text: headers.join('\n') }),
          ),
        ),
      ),
      h('td', { text: letter.messageType ?? 'Unknown' }),
      h('td', { text: letter.originalDestination ?? 'Unknown' }),
      h(
        'td',
        {},
        h('div', { text: letter.exceptionType ?? 'Unknown' }),
        h('small', { className: 'text-body-secondary', text: letter.exceptionMessage ?? '' }),
      ),
      h('td', { text: letter.attempts === null ? 'Unknown' : String(letter.attempts) }),
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
      failure = errorMessage(error, 'The dead-letter queue could not be read.');
    }
    loaded = true;
    render();
  }

  async function replayLetter(letter: DeadLetter): Promise<void> {
    const yes = await confirmAction({
      title: 'Replay this message?',
      message: `Message ${letter.messageId} goes back through its exchange and leaves the dead-letter queue.`,
      confirmLabel: 'Replay',
    });

    if (!yes) {
      return;
    }
    let problem: string | undefined;
    notice = '';
    try {
      const done = await replayDeadLetter(options.http, letter.messageId);
      notice = `Replayed ${letter.messageId} as ${done.newMessageId} through ${done.exchange} (${done.routingKey}).`;
    } catch (error) {
      problem = errorMessage(error, 'The message could not be replayed.');
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
