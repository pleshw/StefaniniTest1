import {
  findByRole,
  findByText,
  fireEvent,
  getByRole,
  queryByRole,
  within,
} from '@testing-library/dom';
import { http, HttpResponse } from 'msw';
import { describe, expect, it, vi } from 'vitest';
import { server } from '../../mocks/server';
import type { OrderEvent } from '../../src/admin/api/events';
import { mountLiveEvents } from '../../src/admin/ui/liveEvents';
import { createHttpClient } from '../../src/shared/api/http';
import { FakeSource } from '../fakeEventSource';
import { expectAccessible } from '../helpers';

const orderId = '3f6c2a10-1111-4222-8333-444455556666';

function placed(id: number, number = `ORD-${String(id)}`): OrderEvent {
  return {
    id,
    orderId,
    type: 'OrderPlaced.v1',
    occurredAt: '2026-10-26T12:00:00Z',
    cursor: `c${String(id)}`,
    payload: { number, channel: 'shop', currency: 'BRL', total: 100, itemCount: 2 },
  };
}

function changed(id: number, from: string, to: string): OrderEvent {
  return {
    id,
    orderId,
    type: 'OrderStatusChanged.v1',
    occurredAt: '2026-10-26T12:05:00Z',
    cursor: `c${String(id)}`,
    payload: { from, to, reason: 'Rule', actor: 'system:worker' },
  };
}

function mount(overrides: { onInspect?: (orderId: string) => void; onEvent?: () => void } = {}) {
  const container = document.createElement('div');
  document.body.append(container);
  const sources: FakeSource[] = [];
  const onInspect = overrides.onInspect ?? vi.fn();

  mountLiveEvents(container, {
    http: createHttpClient({ sleep: () => Promise.resolve() }),
    onInspect,
    ...(overrides.onEvent === undefined ? {} : { onEvent: overrides.onEvent }),
    createSource: (url) => {
      const source = new FakeSource(url);
      sources.push(source);
      return source;
    },
  });
  const source = (): FakeSource => {
    const found = sources.at(-1);
    if (found === undefined) {
      throw new Error('No stream was opened.');
    }
    return found;
  };
  const send = (event: OrderEvent): void => {
    source().emit(event.type, JSON.stringify(event), event.cursor ?? '');
  };

  return { container, sources, source, send, onInspect };
}

const rows = (container: HTMLElement): HTMLElement[] =>
  within(container).queryAllByRole('row').slice(1);
const live = (container: HTMLElement): string =>
  getByRole(container, 'status', { name: 'Conexão' }).textContent;

describe('live events', () => {
  it('connects to the stream with the log type names and says it is connecting, then live', () => {
    const { container, source } = mount();

    expect(source().url).toBe('/bff/admin/events/stream');
    expect(source().listening()).toEqual([
      'OrderPlaced.v1',
      'OrderStatusChanged.v1',
      'DeadLetterReplayed.v1',
      'heartbeat',
      'backfill-unavailable',
    ]);
    expect(live(container)).toBe('Conectando…');

    source().onopen?.(new Event('open'));

    expect(live(container)).toBe('Ao vivo');
  });

  it('shows a received event as a row: time, type, order, status and what happened', () => {
    const { container, send } = mount();

    send(placed(1));

    const [row] = rows(container);
    expect(row?.textContent).toContain('26 de out. de 2026');
    expect(row?.textContent).toContain('OrderPlaced.v1');
    expect(row?.textContent).toContain('ORD-1');
    expect(row?.textContent).toContain('Pendente');
    expect(row?.textContent).toContain('Pedido realizado via shop: R$\u00a0100,00, 2 itens');
  });

  it('puts the newest event on top and shows a status change with its badge', () => {
    const { container, send } = mount();

    send(placed(1));
    send(changed(2, 'Pending', 'OnHold'));

    const [first, second] = rows(container);
    expect(first?.textContent).toContain('Pendente → Em espera por system:worker: Rule');
    expect(first?.querySelector('.badge')?.className).toContain('text-bg-warning');
    expect(second?.textContent).toContain('OrderPlaced.v1');
  });

  it('shows an event once when it arrives twice', () => {
    const { container, send } = mount();

    send(placed(1));
    send(placed(1));

    expect(rows(container)).toHaveLength(1);
  });

  it('ignores a heartbeat and a message it cannot read', () => {
    const { container, source, send } = mount();

    source().emit('heartbeat', '{}', '');
    source().emit('OrderPlaced.v1', 'not json', 'c9');
    source().emit('OrderPlaced.v1', '{"id":"x"}', 'c9');
    send(placed(1));

    expect(rows(container)).toHaveLength(1);
  });

  it('tells the page about each new event, so it can refresh what depends on it', () => {
    const onEvent = vi.fn();
    const { send } = mount({ onEvent });

    send(placed(1));
    send(placed(1));
    send(changed(2, 'OnHold', 'Confirmed'));

    expect(onEvent).toHaveBeenCalledTimes(2);
    expect(onEvent.mock.calls[1]?.[0]).toMatchObject({ from: 'OnHold', status: 'Confirmed' });
  });

  it('opens the order of a row in the inspector', () => {
    const { container, send, onInspect } = mount();
    send(placed(1));

    fireEvent.click(getByRole(container, 'button', { name: 'Inspecionar ORD-1' }));

    expect(onInspect).toHaveBeenCalledExactlyOnceWith(orderId);
  });

  it('shows nothing but a waiting message before the first event', () => {
    const { container } = mount();

    expect(container.textContent).toContain('Aguardando eventos…');
    expect(queryByRole(container, 'table')).toBeNull();
  });

  it('keeps at most 500 rows', () => {
    const { container, send } = mount();

    for (let id = 1; id <= 505; id++) {
      send(placed(id));
    }

    expect(rows(container)).toHaveLength(500);
    expect(rows(container)[0]?.textContent).toContain('ORD-505');
  });
});

describe('pause and filters', () => {
  it('holds new events in a buffer while paused and shows them on resume', () => {
    const { container, send } = mount();
    send(placed(1));

    fireEvent.click(getByRole(container, 'button', { name: 'Pausar' }));
    send(placed(2));
    send(placed(3));

    expect(rows(container)).toHaveLength(1);
    expect(live(container)).toBe('Pausado · 2 novos eventos aguardando');

    fireEvent.click(getByRole(container, 'button', { name: 'Retomar' }));

    expect(rows(container)).toHaveLength(3);
    expect(rows(container)[0]?.textContent).toContain('ORD-3');
    expect(getByRole(container, 'button', { name: 'Pausar' })).toBeTruthy();
  });

  it('says "1 novo evento" in the singular and keeps saying paused when nothing is waiting', () => {
    const { container, send } = mount();

    fireEvent.click(getByRole(container, 'button', { name: 'Pausar' }));
    expect(live(container)).toBe('Pausado');
    send(placed(1));

    expect(live(container)).toBe('Pausado · 1 novo evento aguardando');
  });

  it('filters by event type and by status', () => {
    const { container, send } = mount();
    send(placed(1));
    send(changed(2, 'Pending', 'OnHold'));
    send(changed(3, 'OnHold', 'Confirmed'));

    fireEvent.change(getByRole(container, 'combobox', { name: 'Tipo de evento' }), {
      target: { value: 'OrderStatusChanged.v1' },
    });
    expect(rows(container)).toHaveLength(2);

    fireEvent.change(getByRole(container, 'combobox', { name: 'Status' }), {
      target: { value: 'OnHold' },
    });
    expect(rows(container)).toHaveLength(1);
    expect(rows(container)[0]?.textContent).toContain('Pendente → Em espera');

    fireEvent.change(getByRole(container, 'combobox', { name: 'Tipo de evento' }), {
      target: { value: '' },
    });
    fireEvent.change(getByRole(container, 'combobox', { name: 'Status' }), {
      target: { value: '' },
    });
    expect(rows(container)).toHaveLength(3);
  });

  it('says so when the filters match nothing', () => {
    const { container, send } = mount();
    send(placed(1));

    fireEvent.change(getByRole(container, 'combobox', { name: 'Status' }), {
      target: { value: 'Failed' },
    });

    expect(container.textContent).toContain('Nenhum evento corresponde aos filtros.');
  });
});

describe('connection', () => {
  it('shows reconnecting while the browser retries by itself', () => {
    const { container, source } = mount();

    source().onerror?.(new Event('error'));

    expect(live(container)).toBe('Reconectando…');
  });

  it('tells when the stream closed and reconnects: reopen first, then the missed events by cursor', async () => {
    const afters: (string | null)[] = [];
    server.use(
      http.get('*/bff/admin/events', ({ request }) => {
        const after = new URL(request.url).searchParams.get('after');
        afters.push(after);
        return HttpResponse.json(
          after === 'c2'
            ? { items: [placed(2), placed(3)], next: 'c3' }
            : { items: [placed(4)], next: null },
        );
      }),
    );
    const { container, sources, source, send } = mount();
    send(placed(1));
    send(placed(2));
    source().readyState = FakeSource.closed;
    source().onerror?.(new Event('error'));

    const alert = getByRole(container, 'alert');
    expect(alert.textContent).toContain('As atualizações ao vivo foram interrompidas.');
    expect(live(container)).toBe('Desconectado');
    fireEvent.click(getByRole(alert, 'button', { name: 'Reconectar' }));

    await vi.waitFor(() => {
      expect(rows(container)).toHaveLength(4);
    });
    expect(sources).toHaveLength(2);
    expect(afters).toEqual(['c2', 'c3']);
    expect(rows(container).map((row) => within(row).getByRole('button').textContent)).toEqual([
      'ORD-4',
      'ORD-3',
      'ORD-2',
      'ORD-1',
    ]);
    expect(queryByRole(container, 'alert')).toBeNull();
  });

  it('reconnects without a backfill when no event was ever received', async () => {
    let backfills = 0;
    server.use(
      http.get('*/bff/admin/events', () => {
        backfills++;
        return HttpResponse.json({ items: [], next: null });
      }),
    );
    const { container, sources, source } = mount();
    source().readyState = FakeSource.closed;
    source().onerror?.(new Event('error'));

    fireEvent.click(getByRole(container, 'button', { name: 'Reconectar' }));

    await vi.waitFor(() => {
      expect(sources).toHaveLength(2);
    });
    expect(backfills).toBe(0);
  });

  it('says the missed events could not be loaded when the backfill fails', async () => {
    server.use(http.get('*/bff/admin/events', () => HttpResponse.json({}, { status: 500 })));
    const { container, source, send } = mount();
    send(placed(1));
    source().readyState = FakeSource.closed;
    source().onerror?.(new Event('error'));

    fireEvent.click(getByRole(container, 'button', { name: 'Reconectar' }));

    expect((await findByRole(container, 'alert')).textContent).toContain(
      'Não foi possível carregar os eventos perdidos durante a desconexão.',
    );
  });

  it('warns when the server could not backfill after a reconnection', () => {
    const { container, source } = mount();

    source().emit('backfill-unavailable', '{}', '');

    expect(getByRole(container, 'alert').textContent).toContain(
      'Alguns eventos podem estar faltando',
    );
  });

  it('says so when the browser has no EventSource', () => {
    const container = document.createElement('div');
    document.body.append(container);
    vi.stubGlobal('EventSource', undefined);

    mountLiveEvents(container, { http: createHttpClient(), onInspect: vi.fn() });
    vi.unstubAllGlobals();

    expect(container.textContent).toContain(
      'As atualizações ao vivo exigem um navegador com suporte a server-sent events.',
    );
  });
});

describe('live events accessibility', () => {
  it('has no violations waiting, with rows, paused and disconnected', async () => {
    const { container, source, send } = mount();
    await expectAccessible(container);

    send(placed(1));
    send(changed(2, 'Pending', 'OnHold'));
    await expectAccessible(container);

    fireEvent.click(getByRole(container, 'button', { name: 'Pausar' }));
    await expectAccessible(container);

    source().readyState = FakeSource.closed;
    source().onerror?.(new Event('error'));
    await findByText(container, 'As atualizações ao vivo foram interrompidas.');
    await expectAccessible(container);
  });
});
