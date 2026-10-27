import {
  findByRole,
  findByText,
  fireEvent,
  getByLabelText,
  getByRole,
  queryByRole,
  within,
} from '@testing-library/dom';
import { http, HttpResponse } from 'msw';
import { describe, expect, it, vi } from 'vitest';
import { server } from '../../mocks/server';
import type { AdminOrderView } from '../../src/admin/api/orders';
import { mountInspector } from '../../src/admin/ui/inspector';
import { createHttpClient } from '../../src/shared/api/http';
import type { Order } from '../../src/shared/api/orders';
import { expectAccessible } from '../helpers';

const id = 'b6c1f0a2-0000-4000-8000-000000000001';

const order = (status: string, rowVersion = 3): Order => ({
  id,
  number: 'ORD-1',
  client: { id: 'c1', name: 'Ana Souza' },
  status,
  channel: 'Shop',
  currency: 'BRL',
  itemCount: 2,
  subtotal: 200,
  discountTotal: 20,
  total: 180,
  createdAt: '2026-10-26T12:00:00Z',
  rowVersion,
  items: [
    {
      sku: 'MUG-1',
      productName: 'Coffee mug',
      unitPrice: 100,
      catalogVersion: 1,
      quantity: 2,
      lineDiscount: 20,
      lineTotal: 180,
    },
  ],
});

const view = (status: string, extra: Partial<AdminOrderView> = {}): AdminOrderView => ({
  order: order(status),
  history: [
    {
      seq: 1,
      from: null,
      to: 'Pending',
      reason: null,
      actor: 'c1',
      changedAt: '2026-10-26T12:00:00Z',
    },
    {
      seq: 2,
      from: 'Pending',
      to: status,
      reason: 'Total above limit',
      actor: 'system:worker',
      changedAt: '2026-10-26T12:01:00Z',
    },
  ],
  events: [
    {
      id: 9,
      orderId: id,
      type: 'OrderStatusChanged.v1',
      occurredAt: '2026-10-26T12:01:00Z',
      payload: { from: 'Pending', to: status, reason: 'Total above limit', actor: 'system:worker' },
    },
  ],
  partial: false,
  unavailable: [],
  ...extra,
});

function mount(onChanged = vi.fn()) {
  const container = document.createElement('div');
  document.body.append(container);
  const inspector = mountInspector(container, {
    http: createHttpClient({ sleep: () => Promise.resolve() }),
    onChanged,
  });

  return { container, inspector, onChanged };
}

function useOrder(answer: AdminOrderView): void {
  server.use(http.get('*/bff/admin/orders/:id', () => HttpResponse.json(answer)));
}

async function openOrder(status: string, extra: Partial<AdminOrderView> = {}) {
  useOrder(view(status, extra));
  const mounted = mount();
  mounted.inspector.open(id);
  await findByRole(mounted.container, 'heading', { level: 3, name: 'Pedido ORD-1' });

  return mounted;
}

const dialog = (): HTMLElement => getByRole(document.body, 'dialog');

describe('order inspector', () => {
  it('says nothing is open before an order is chosen', () => {
    const { container } = mount();

    expect(container.textContent).toContain(
      'Escolha um pedido nos eventos ao vivo ou na fila em espera.',
    );
  });

  it('shows the order, its history with reasons and actors, and its events', async () => {
    const { container } = await openOrder('OnHold');

    expect(container.textContent).toContain('Ana Souza');
    expect(container.textContent).toContain('Coffee mug');
    expect(container.textContent).toContain('R$\u00a0180,00');
    const history = getByRole(container, 'list', { name: 'Linha do tempo do status' });
    expect(within(history).getAllByRole('listitem')).toHaveLength(2);
    expect(history.textContent).toContain('Pendente → Em espera');
    expect(history.textContent).toContain('system:worker');
    expect(history.textContent).toContain('Total above limit');
    const events = getByRole(container, 'table', { name: 'Eventos' });
    expect(events.textContent).toContain('OrderStatusChanged.v1');
    expect(events.textContent).toContain(
      'Pendente → Em espera por system:worker: Total above limit',
    );
  });

  it('says which parts could not be read, and still shows the order', async () => {
    const { container } = await openOrder('Pending', {
      history: null,
      events: null,
      partial: true,
      unavailable: ['history', 'events'],
    });

    expect(getByRole(container, 'alert').textContent).toContain(
      'Algumas partes não puderam ser carregadas: history, events.',
    );
    expect(container.textContent).toContain('Coffee mug');
  });

  it('shows the error when the order cannot be loaded', async () => {
    server.use(
      http.get('*/bff/admin/orders/:id', () =>
        HttpResponse.json({ title: 'Not found', detail: 'Order not found.' }, { status: 404 }),
      ),
    );
    const { container, inspector } = mount();

    inspector.open(id);

    expect((await findByRole(container, 'alert')).textContent).toContain('Order not found.');
  });

  it.each([
    ['Pending', ['Confirmar', 'Colocar em espera', 'Bloquear']],
    ['OnHold', ['Confirmar', 'Bloquear']],
    ['Confirmed', ['Cancelar']],
    ['Blocked', []],
    ['Cancelled', []],
    ['Failed', []],
  ])('offers the actions an order that is %s allows: %j', async (status, labels) => {
    const { container } = await openOrder(status);

    const group = getByRole(container, 'group', { name: 'Ações do pedido' });
    expect(
      within(group)
        .queryAllByRole('button')
        .map((button) => button.textContent),
    ).toEqual(labels);
  });
});

describe('order actions', () => {
  it('confirms with the version it looked at as If-Match, then shows the new state and tells the page', async () => {
    let request: Request | undefined;
    let body: unknown;
    server.use(
      http.post('*/bff/admin/orders/:id/confirm', async ({ request: incoming }) => {
        request = incoming.clone();
        body = await incoming.json();
        return HttpResponse.json(order('Confirmed', 4));
      }),
    );
    const { container, onChanged } = await openOrder('OnHold');
    useOrder(view('Confirmed'));

    fireEvent.click(getByRole(container, 'button', { name: 'Confirmar' }));
    fireEvent.click(getByRole(dialog(), 'button', { name: 'Confirmar pedido' }));

    await findByText(container, 'O pedido ORD-1 agora está Confirmado.');
    expect(request?.headers.get('if-match')).toBe('"3"');
    expect(request?.headers.get('x-csrf')).toBe('1');
    expect(body).toEqual({});
    expect(onChanged).toHaveBeenCalledOnce();
    await vi.waitFor(() => {
      expect(
        within(getByRole(container, 'group', { name: 'Ações do pedido' }))
          .getAllByRole('button')
          .map((button) => button.textContent),
      ).toEqual(['Cancelar']);
    });
  });

  it('asks for a reason to put an order on hold and sends it', async () => {
    let body: unknown;
    server.use(
      http.post('*/bff/admin/orders/:id/hold', async ({ request }) => {
        body = await request.json();
        return HttpResponse.json(order('OnHold', 4));
      }),
    );
    const { container } = await openOrder('Pending');
    useOrder(view('OnHold'));

    fireEvent.click(getByRole(container, 'button', { name: 'Colocar em espera' }));
    fireEvent.input(getByLabelText(dialog(), 'Motivo'), { target: { value: 'Check the address' } });
    fireEvent.click(getByRole(dialog(), 'button', { name: 'Colocar em espera' }));

    await findByText(container, 'O pedido ORD-1 agora está Em espera.');
    expect(body).toEqual({ reason: 'Check the address' });
  });

  it.each([
    ['Pending', 'Colocar em espera', 'Colocar em espera'],
    ['Pending', 'Bloquear', 'Bloquear pedido'],
    ['Confirmed', 'Cancelar', 'Cancelar pedido'],
  ])('does not send %s %s without a reason', async (status, action, confirmLabel) => {
    let calls = 0;
    server.use(
      http.post('*/bff/admin/orders/:id/:action', () => {
        calls++;
        return HttpResponse.json(order('OnHold'));
      }),
    );
    const { container } = await openOrder(status);

    fireEvent.click(getByRole(container, 'button', { name: action }));
    fireEvent.click(getByRole(dialog(), 'button', { name: confirmLabel }));

    expect(getByRole(dialog(), 'alert').textContent).toBe('Motivo é obrigatório.');
    fireEvent.click(getByRole(dialog(), 'button', { name: 'Cancelar' }));
    await new Promise((resolve) => setTimeout(resolve, 20));
    expect(calls).toBe(0);
  });

  it('sends nothing when the dialog is cancelled', async () => {
    let calls = 0;
    server.use(
      http.post('*/bff/admin/orders/:id/confirm', () => {
        calls++;
        return HttpResponse.json(order('Confirmed'));
      }),
    );
    const { container } = await openOrder('Pending');

    fireEvent.click(getByRole(container, 'button', { name: 'Confirmar' }));
    fireEvent.click(getByRole(dialog(), 'button', { name: 'Cancelar' }));
    await new Promise((resolve) => setTimeout(resolve, 20));

    expect(calls).toBe(0);
    expect(queryByRole(document.body, 'dialog')).toBeNull();
  });

  it('on a 412 says the order was changed by someone else and reloads it on request', async () => {
    server.use(
      http.post('*/bff/admin/orders/:id/confirm', () =>
        HttpResponse.json(
          { title: 'Precondition failed', status: 412, detail: 'Stale.' },
          { status: 412 },
        ),
      ),
    );
    const { container, onChanged } = await openOrder('Pending');

    fireEvent.click(getByRole(container, 'button', { name: 'Confirmar' }));
    fireEvent.click(getByRole(dialog(), 'button', { name: 'Confirmar pedido' }));

    const alert = await findByRole(container, 'alert');
    expect(alert.textContent).toContain(
      'Este pedido foi alterado por outra pessoa. Recarregue-o para ver o estado atual.',
    );
    expect(onChanged).not.toHaveBeenCalled();

    useOrder(view('OnHold', { order: order('OnHold', 5) }));
    fireEvent.click(getByRole(alert, 'button', { name: 'Recarregar' }));

    await vi.waitFor(() => {
      expect(
        within(getByRole(container, 'group', { name: 'Ações do pedido' }))
          .getAllByRole('button')
          .map((button) => button.textContent),
      ).toEqual(['Confirmar', 'Bloquear']);
    });
    expect(queryByRole(container, 'alert')).toBeNull();
  });

  it('on a 409 shows why the transition is not allowed, in the services words', async () => {
    server.use(
      http.post('*/bff/admin/orders/:id/confirm', () =>
        HttpResponse.json(
          {
            title: 'Invalid transition',
            status: 409,
            detail: 'An order that is Cancelled cannot be confirmed.',
          },
          { status: 409 },
        ),
      ),
    );
    const { container } = await openOrder('Pending');

    fireEvent.click(getByRole(container, 'button', { name: 'Confirmar' }));
    fireEvent.click(getByRole(dialog(), 'button', { name: 'Confirmar pedido' }));

    expect((await findByRole(container, 'alert')).textContent).toContain(
      'An order that is Cancelled cannot be confirmed.',
    );
    expect(queryByRole(container, 'button', { name: 'Recarregar' })).toBeNull();
  });

  it('shows a network failure and lets the action be tried again', async () => {
    let fail = true;
    server.use(
      http.post('*/bff/admin/orders/:id/confirm', () =>
        fail ? HttpResponse.error() : HttpResponse.json(order('Confirmed', 4)),
      ),
    );
    const { container } = await openOrder('Pending');
    fireEvent.click(getByRole(container, 'button', { name: 'Confirmar' }));
    fireEvent.click(getByRole(dialog(), 'button', { name: 'Confirmar pedido' }));

    expect((await findByRole(container, 'alert')).textContent).toContain(
      'Não foi possível acessar o servidor.',
    );
    expect(getByRole(container, 'button', { name: 'Confirmar' }).hasAttribute('disabled')).toBe(
      false,
    );
    fail = false;
    useOrder(view('Confirmed'));
    fireEvent.click(getByRole(container, 'button', { name: 'Confirmar' }));
    fireEvent.click(getByRole(dialog(), 'button', { name: 'Confirmar pedido' }));

    await findByText(container, 'O pedido ORD-1 agora está Confirmado.');
  });

  it('disables the actions while one is being sent', async () => {
    let release: () => void = () => undefined;
    const gate = new Promise<void>((resolve) => {
      release = resolve;
    });
    server.use(
      http.post('*/bff/admin/orders/:id/confirm', async () => {
        await gate;
        return HttpResponse.json(order('Confirmed', 4));
      }),
    );
    const { container } = await openOrder('Pending');
    useOrder(view('Confirmed'));

    fireEvent.click(getByRole(container, 'button', { name: 'Confirmar' }));
    fireEvent.click(getByRole(dialog(), 'button', { name: 'Confirmar pedido' }));

    await vi.waitFor(() => {
      expect(
        within(getByRole(container, 'group', { name: 'Ações do pedido' }))
          .getAllByRole('button')
          .every((button) => button.hasAttribute('disabled')),
      ).toBe(true);
    });
    release();
    await findByText(container, 'O pedido ORD-1 agora está Confirmado.');
  });
});

describe('order inspector accessibility', () => {
  it('has no violations: empty, an order, partial, an error and the reason dialog', async () => {
    const empty = mount();
    await expectAccessible(empty.container);
    empty.container.remove();

    const full = await openOrder('Pending');
    await expectAccessible(full.container);
    fireEvent.click(getByRole(full.container, 'button', { name: 'Colocar em espera' }));
    await expectAccessible(dialog());
    fireEvent.click(getByRole(dialog(), 'button', { name: 'Cancelar' }));
    full.container.remove();

    const partial = await openOrder('OnHold', {
      history: null,
      partial: true,
      unavailable: ['history'],
    });
    await expectAccessible(partial.container);
    partial.container.remove();

    server.use(http.get('*/bff/admin/orders/:id', () => HttpResponse.json({}, { status: 500 })));
    const failed = mount();
    failed.inspector.open(id);
    await findByRole(failed.container, 'alert');
    await expectAccessible(failed.container);
  });
});
