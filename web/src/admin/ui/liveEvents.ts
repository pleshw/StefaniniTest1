import { listEvents, logEventTypes, streamEventNames, type OrderEvent } from '../api/events';
import { createLiveFeed, rowOf, type FeedRow } from '../domain/feed';
import type { HttpClient } from '../../shared/api/http';
import {
  openEventStream,
  type EventSourceLike,
  type StreamEvent,
  type StreamStatus,
} from '../../shared/api/sse';
import { formatDateTime } from '../../shared/domain/dates';
import { orderStatuses, statusView } from '../../shared/domain/status';
import { h } from '../../shared/ui/dom';
import { setIconText, withIcon } from '../../shared/ui/icons';
import { statusBadge } from '../../shared/ui/status';

export interface LiveEventsOptions {
  readonly http: HttpClient;
  /** Opens the inspector on an order. */
  readonly onInspect: (orderId: string) => void;
  /** Called once for each new event, so the page can refresh what depends on it (the OnHold queue). */
  readonly onEvent?: (row: FeedRow) => void;
  readonly createSource?: (url: string) => EventSourceLike;
}

const streamUrl = '/bff/admin/events/stream';
const backfillPageSize = 100;
const maxBackfillPages = 20;

const connectionText: Readonly<Record<StreamStatus, string>> = {
  connecting: 'Conectando…',
  open: 'Ao vivo',
  reconnecting: 'Reconectando…',
  closed: 'Desconectado',
};

/** The live table of order events: server-sent, newest first, with pause, filters and a backfill after a reconnect. */
export function mountLiveEvents(container: HTMLElement, options: LiveEventsOptions): void {
  if (options.createSource === undefined && typeof EventSource === 'undefined') {
    container.append(
      h(
        'section',
        { attrs: { 'aria-labelledby': 'live-heading' } },
        withIcon(
          h('h2', { className: 'h4', text: 'Eventos ao vivo', attrs: { id: 'live-heading' } }),
          'bi-broadcast',
        ),
        h('p', {
          className: 'alert alert-warning',
          text: 'As atualizações ao vivo exigem um navegador com suporte a server-sent events.',
        }),
      ),
    );
    return;
  }

  const feed = createLiveFeed();
  let connection: StreamStatus = 'connecting';
  let notice: string | undefined;
  const elements = new Map<number, HTMLElement>();
  let shown: readonly number[] = [];

  const status = h('p', {
    className: 'mb-0',
    attrs: { role: 'status', 'aria-label': 'Conexão' },
  });
  const pause = h('button', {
    className: 'btn btn-outline-secondary btn-sm',
    attrs: { type: 'button' },
  });
  const typeFilter = select('live-type', 'Tipo de evento', 'Todos os eventos', logEventTypes);
  const statusFilter = select(
    'live-status',
    'Status',
    'Todos os status',
    orderStatuses,
    (value) => statusView(value).label,
  );
  const closedAlert = h('div', {
    className: 'alert alert-warning d-flex align-items-center gap-3',
    attrs: { role: 'alert' },
  });
  const noticeAlert = h('div', { className: 'alert alert-warning', attrs: { role: 'alert' } });
  const empty = h('p', { className: 'text-body-secondary' });
  const latest = h('div', {
    className: 'visually-hidden',
    attrs: { role: 'log', 'aria-label': 'Último evento' },
  });
  const body = h('tbody');
  const table = h(
    'table',
    { className: 'table table-sm align-middle' },
    h('caption', { className: 'visually-hidden', text: 'Eventos ao vivo' }),
    h(
      'thead',
      {},
      h(
        'tr',
        {},
        ...['Hora', 'Evento', 'Pedido', 'Status', 'Detalhes'].map((name) =>
          h('th', { text: name, attrs: { scope: 'col' } }),
        ),
      ),
    ),
    body,
  );

  container.append(
    h(
      'section',
      { attrs: { 'aria-labelledby': 'live-heading' } },
      withIcon(
        h('h2', { className: 'h4', text: 'Eventos ao vivo', attrs: { id: 'live-heading' } }),
        'bi-broadcast',
      ),
      h(
        'div',
        { className: 'd-flex flex-wrap align-items-end gap-3 mb-3' },
        h('div', { className: 'me-auto' }, status),
        typeFilter.group,
        statusFilter.group,
        pause,
      ),
      closedAlert,
      noticeAlert,
      empty,
      table,
      latest,
    ),
  );

  function render(): void {
    const filter = {
      ...(typeFilter.field.value === '' ? {} : { type: typeFilter.field.value }),
      ...(statusFilter.field.value === '' ? {} : { status: statusFilter.field.value }),
    };
    const rows = feed.rows(filter);

    status.textContent = feed.paused() ? pausedText() : connectionText[connection];
    if (feed.paused()) {
      setIconText(pause, 'bi-play-fill', 'Retomar');
    } else {
      setIconText(pause, 'bi-pause-fill', 'Pausar');
    }
    renderRows(rows);
    table.hidden = rows.length === 0;
    empty.hidden = rows.length > 0;
    empty.textContent =
      feed.rows().length === 0 && !feed.paused()
        ? 'Aguardando eventos…'
        : 'Nenhum evento corresponde aos filtros.';
    closedAlert.hidden = connection !== 'closed';
    closedAlert.replaceChildren(
      ...(connection === 'closed'
        ? [h('span', { text: 'As atualizações ao vivo foram interrompidas.' }), reconnectButton()]
        : []),
    );
    noticeAlert.hidden = notice === undefined;
    noticeAlert.textContent = notice ?? '';
  }

  /** Rows arrive on top, so most renders only add a few elements there and drop the oldest below. */
  function renderRows(rows: readonly FeedRow[]): void {
    const ids = rows.map((row) => row.id);
    const unchanged = ids.length === shown.length && ids.every((id, index) => id === shown[index]);

    if (!unchanged) {
      const newest = shown[0];
      const added = newest === undefined ? -1 : ids.indexOf(newest);
      const kept = ids.slice(added);

      if (added > 0 && kept.every((id, index) => id === shown[index])) {
        body.prepend(...rows.slice(0, added).map(elementOf));
        while (body.children.length > rows.length) {
          body.lastElementChild?.remove();
        }
      } else {
        body.replaceChildren(...rows.map(elementOf));
      }
      for (const id of shown) {
        if (!ids.includes(id)) {
          elements.delete(id);
        }
      }
    }
    shown = ids;
  }

  function elementOf(row: FeedRow): HTMLElement {
    let element = elements.get(row.id);

    if (element === undefined) {
      element = rowElement(row);
      elements.set(row.id, element);
    }

    return element;
  }

  function pausedText(): string {
    const waiting = feed.pending();

    return waiting === 0
      ? 'Pausado'
      : `Pausado · ${String(waiting)} ${waiting === 1 ? 'novo evento aguardando' : 'novos eventos aguardando'}`;
  }

  function rowElement(row: FeedRow): HTMLElement {
    const inspect = h('button', {
      className: 'btn btn-link btn-sm p-0',
      text: row.orderLabel,
      attrs: { type: 'button', 'aria-label': `Inspecionar ${row.orderLabel}` },
    });
    inspect.addEventListener('click', () => {
      options.onInspect(row.orderId);
    });

    return h(
      'tr',
      {},
      h('td', { className: 'text-nowrap', text: formatDateTime(row.occurredAt) }),
      h('td', { text: row.type }),
      h('td', {}, inspect),
      h('td', {}, ...(row.status === undefined ? [] : [statusBadge(row.status)])),
      h('td', { text: row.summary }),
    );
  }

  function reconnectButton(): HTMLButtonElement {
    const button = withIcon(
      h('button', {
        className: 'btn btn-sm btn-outline-secondary ms-auto',
        text: 'Reconectar',
        attrs: { type: 'button' },
      }),
      'bi-plug',
      'me-1',
    );
    button.addEventListener('click', () => void reconnect());

    return button;
  }

  function accept(event: OrderEvent): void {
    const row = rowOf(event);

    if (feed.push(row)) {
      latest.textContent = `${row.type} ${row.orderLabel}`;
      options.onEvent?.(row);
      render();
    }
  }

  function onMessage(message: StreamEvent): void {
    if (message.name === 'heartbeat') {
      return;
    }
    if (message.name === 'backfill-unavailable') {
      notice =
        'Alguns eventos podem estar faltando: o servidor não conseguiu enviar o que foi perdido. Recarregue a página para recomeçar.';
      render();
      return;
    }
    const event = parseEvent(message.data);

    if (event !== undefined) {
      accept(event);
    }
  }

  /** Opens the stream again first, then reads what was missed, so nothing falls in the gap; repeats are dropped. */
  async function reconnect(): Promise<void> {
    const last = stream.lastEventId();
    notice = undefined;
    stream.reopen();

    if (last === undefined) {
      return;
    }
    try {
      let after: string | null = last;
      for (let page = 0; after !== null && page < maxBackfillPages; page++) {
        const events = await listEvents(options.http, { after, limit: backfillPageSize });
        events.items.forEach(accept);
        after = events.next;
      }
    } catch {
      notice = 'Não foi possível carregar os eventos perdidos durante a desconexão.';
    }
    render();
  }

  pause.addEventListener('click', () => {
    if (feed.paused()) {
      feed.resume();
    } else {
      feed.pause();
    }
    render();
  });
  typeFilter.field.addEventListener('change', render);
  statusFilter.field.addEventListener('change', render);

  const stream = openEventStream({
    url: streamUrl,
    eventNames: streamEventNames,
    onEvent: onMessage,
    onStatus: (next) => {
      connection = next;
      render();
    },
    http: options.http,
    ...(options.createSource === undefined ? {} : { createSource: options.createSource }),
  });
  render();
}

function select(
  id: string,
  label: string,
  all: string,
  values: readonly string[],
  labelOf: (value: string) => string = (value) => value,
): { group: HTMLElement; field: HTMLSelectElement } {
  const field = h(
    'select',
    { className: 'form-select form-select-sm', attrs: { id } },
    h('option', { text: all, attrs: { value: '' } }),
    ...values.map((value) => h('option', { text: labelOf(value), attrs: { value } })),
  );

  return {
    group: h(
      'div',
      {},
      h('label', { className: 'form-label mb-0 small', text: label, attrs: { for: id } }),
      field,
    ),
    field,
  };
}

function parseEvent(data: string): OrderEvent | undefined {
  try {
    const value: unknown = JSON.parse(data);

    if (
      typeof value === 'object' &&
      value !== null &&
      'id' in value &&
      'orderId' in value &&
      'type' in value &&
      'occurredAt' in value &&
      typeof value.id === 'number' &&
      typeof value.orderId === 'string' &&
      typeof value.type === 'string' &&
      typeof value.occurredAt === 'string'
    ) {
      return value as OrderEvent;
    }
  } catch {
    // A message that is not JSON is not an event.
  }

  return undefined;
}
