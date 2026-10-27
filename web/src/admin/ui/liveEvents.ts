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
import { orderStatuses } from '../../shared/domain/status';
import { h } from '../../shared/ui/dom';
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
  connecting: 'Connecting…',
  open: 'Live',
  reconnecting: 'Reconnecting…',
  closed: 'Disconnected',
};

/** The live table of order events: server-sent, newest first, with pause, filters and a backfill after a reconnect. */
export function mountLiveEvents(container: HTMLElement, options: LiveEventsOptions): void {
  if (options.createSource === undefined && typeof EventSource === 'undefined') {
    container.append(
      h(
        'section',
        { attrs: { 'aria-labelledby': 'live-heading' } },
        h('h2', { className: 'h4', text: 'Live events', attrs: { id: 'live-heading' } }),
        h('p', {
          className: 'alert alert-warning',
          text: 'Live updates need a browser that supports server-sent events.',
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
    attrs: { role: 'status', 'aria-label': 'Connection' },
  });
  const pause = h('button', {
    className: 'btn btn-outline-secondary btn-sm',
    attrs: { type: 'button' },
  });
  const typeFilter = select('live-type', 'Event type', 'All events', logEventTypes);
  const statusFilter = select('live-status', 'Status', 'All statuses', orderStatuses);
  const closedAlert = h('div', {
    className: 'alert alert-warning d-flex align-items-center gap-3',
    attrs: { role: 'alert' },
  });
  const noticeAlert = h('div', { className: 'alert alert-warning', attrs: { role: 'alert' } });
  const empty = h('p', { className: 'text-body-secondary' });
  const latest = h('div', {
    className: 'visually-hidden',
    attrs: { role: 'log', 'aria-label': 'Latest event' },
  });
  const body = h('tbody');
  const table = h(
    'table',
    { className: 'table table-sm align-middle' },
    h('caption', { className: 'visually-hidden', text: 'Live events' }),
    h(
      'thead',
      {},
      h(
        'tr',
        {},
        ...['Time', 'Event', 'Order', 'Status', 'Details'].map((name) =>
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
      h('h2', { className: 'h4', text: 'Live events', attrs: { id: 'live-heading' } }),
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
    pause.textContent = feed.paused() ? 'Resume' : 'Pause';
    renderRows(rows);
    table.hidden = rows.length === 0;
    empty.hidden = rows.length > 0;
    empty.textContent =
      feed.rows().length === 0 && !feed.paused()
        ? 'Waiting for events…'
        : 'No events match the filters.';
    closedAlert.hidden = connection !== 'closed';
    closedAlert.replaceChildren(
      ...(connection === 'closed'
        ? [h('span', { text: 'Live updates stopped.' }), reconnectButton()]
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
      ? 'Paused'
      : `Paused · ${String(waiting)} new ${waiting === 1 ? 'event' : 'events'} waiting`;
  }

  function rowElement(row: FeedRow): HTMLElement {
    const inspect = h('button', {
      className: 'btn btn-link btn-sm p-0',
      text: row.orderLabel,
      attrs: { type: 'button', 'aria-label': `Inspect ${row.orderLabel}` },
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
    const button = h('button', {
      className: 'btn btn-sm btn-outline-secondary ms-auto',
      text: 'Reconnect',
      attrs: { type: 'button' },
    });
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
        'Some events may be missing: the server could not send what was missed. Reload the page to start again.';
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
      notice = 'The events missed while disconnected could not be loaded.';
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
): { group: HTMLElement; field: HTMLSelectElement } {
  const field = h(
    'select',
    { className: 'form-select form-select-sm', attrs: { id } },
    h('option', { text: all, attrs: { value: '' } }),
    ...values.map((value) => h('option', { text: value, attrs: { value } })),
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
