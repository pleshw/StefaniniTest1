import {
  findAllByRole,
  findByRole,
  findByText,
  fireEvent,
  getByRole,
  queryByRole,
  within,
} from '@testing-library/dom';
import { http, HttpResponse } from 'msw';
import { describe, expect, it } from 'vitest';
import { server } from '../../mocks/server';
import { createHttpClient } from '../../src/shared/api/http';
import type { Order, OrderSummary } from '../../src/shared/api/orders';
import { orderStatuses } from '../../src/shared/domain/status';
import { mountOrders } from '../../src/shop/ui/orders';
import { expectAccessible } from '../helpers';

const client = { id: 'c1', name: 'Shopper' };

const summary = (number: string, status = 'Confirmed', total = 100): OrderSummary => ({
  id: `id-${number}`,
  number,
  client,
  status,
  channel: 'Loja',
  currency: 'BRL',
  itemCount: 3,
  subtotal: total + 10,
  discountTotal: 10,
  total,
  createdAt: '2026-10-26T12:00:00Z',
});

const detail = (status = 'Confirmed'): Order => ({
  ...summary('ORD-1', status, 1924.4),
  subtotal: 2024.4,
  discountTotal: 100,
  rowVersion: 2,
  items: [
    {
      sku: 'DESK-1',
      productName: 'Standing desk',
      unitPrice: 1999.9,
      catalogVersion: 1,
      quantity: 1,
      lineDiscount: 100,
      lineTotal: 1899.9,
    },
    {
      sku: 'MUG-1',
      productName: 'Coffee mug',
      unitPrice: 12.25,
      catalogVersion: 3,
      quantity: 2,
      lineDiscount: 0,
      lineTotal: 24.5,
    },
  ],
});

function mount(pageSize?: number) {
  const container = document.createElement('div');
  document.body.append(container);
  const view = mountOrders(container, {
    http: createHttpClient({ sleep: () => Promise.resolve() }),
    ...(pageSize === undefined ? {} : { pageSize }),
  });

  return { container, view };
}

function useList(items: OrderSummary[], next: string | null = null): void {
  server.use(http.get('*/bff/shop/orders', () => HttpResponse.json({ items, next })));
}

describe('my orders list', () => {
  it('loads nothing until it is opened, then shows each order with number, date, status, items and total', async () => {
    let calls = 0;
    server.use(
      http.get('*/bff/shop/orders', () => {
        calls++;
        return HttpResponse.json({
          items: [summary('ORD-1', 'OnHold', 1924.4), summary('ORD-2')],
          next: null,
        });
      }),
    );
    const { container, view } = mount();
    expect(calls).toBe(0);

    view.refresh();

    const rows = await findAllByRole(container, 'row');
    expect(rows).toHaveLength(3);
    const first = within(rows[1] as HTMLElement);
    expect(first.getByRole('button', { name: 'ORD-1' })).toBeTruthy();
    expect(rows[1]?.textContent).toContain('26 de out. de 2026');
    expect(rows[1]?.textContent).toContain('Em espera');
    expect(rows[1]?.textContent).toContain('3');
    expect(rows[1]?.textContent).toContain('R$\u00a01.924,40');
    expect(rows[1]?.querySelector('.badge')?.className).toContain('text-bg-warning');
    expect(getByRole(container, 'table', { name: 'Meus pedidos' })).toBeTruthy();
    expect(getByRole(container, 'status').textContent).toBe('2 pedidos');
  });

  it('says so when there are no orders', async () => {
    useList([]);
    const { container, view } = mount();

    view.refresh();

    await findByText(container, 'Você ainda não fez nenhum pedido.');
    expect(queryByRole(container, 'table')).toBeNull();
  });

  it('shows the error with a way to try again', async () => {
    let fail = true;
    server.use(
      http.get('*/bff/shop/orders', () =>
        fail
          ? HttpResponse.json({ title: 'Orders is unavailable' }, { status: 500 })
          : HttpResponse.json({ items: [summary('ORD-1')], next: null }),
      ),
    );
    const { container, view } = mount();

    view.refresh();
    const alert = await findByRole(container, 'alert');
    expect(alert.textContent).toContain('Orders is unavailable');
    fail = false;
    fireEvent.click(getByRole(alert, 'button', { name: 'Tentar novamente' }));

    expect(await findByRole(container, 'button', { name: 'ORD-1' })).toBeTruthy();
    expect(queryByRole(container, 'alert')).toBeNull();
  });

  it('loads more with the keyset cursor and appends', async () => {
    const afters: (string | null)[] = [];
    server.use(
      http.get('*/bff/shop/orders', ({ request }) => {
        const url = new URL(request.url);
        afters.push(url.searchParams.get('after'));
        return HttpResponse.json(
          url.searchParams.has('after')
            ? { items: [summary('ORD-2')], next: null }
            : { items: [summary('ORD-1')], next: 'cursor-1' },
        );
      }),
    );
    const { container, view } = mount(1);
    view.refresh();

    fireEvent.click(await findByRole(container, 'button', { name: 'Carregar mais' }));

    await findByRole(container, 'button', { name: 'ORD-2' });
    expect(getByRole(container, 'button', { name: 'ORD-1' })).toBeTruthy();
    expect(afters).toEqual([null, 'cursor-1']);
    expect(queryByRole(container, 'button', { name: 'Carregar mais' })).toBeNull();
  });

  it('starts again from the first page every time it is opened', async () => {
    let call = 0;
    server.use(
      http.get('*/bff/shop/orders', () =>
        HttpResponse.json({ items: [summary(`ORD-${String(++call)}`)], next: null }),
      ),
    );
    const { container, view } = mount();

    view.refresh();
    await findByRole(container, 'button', { name: 'ORD-1' });
    view.refresh();

    await findByRole(container, 'button', { name: 'ORD-2' });
    expect(queryByRole(container, 'button', { name: 'ORD-1' })).toBeNull();
  });
});

const statusLabels: Record<string, string> = {
  OnHold: 'Em espera',
  Confirmed: 'Confirmado',
  Blocked: 'Bloqueado',
  Cancelled: 'Cancelado',
  Failed: 'Falhou',
};

describe('order detail', () => {
  async function open(order: Order) {
    useList([summary(order.number, order.status)]);
    server.use(http.get('*/bff/shop/orders/:id', () => HttpResponse.json(order)));
    const { container, view } = mount();
    view.refresh();
    fireEvent.click(await findByRole(container, 'button', { name: order.number }));
    await findByRole(container, 'heading', { level: 3, name: `Pedido ${order.number}` });

    return container;
  }

  it('shows the items, quantity, subtotal, discount, total and currency', async () => {
    const container = await open(detail());

    const items = getByRole(container, 'table', { name: 'Itens' });
    expect(items.textContent).toContain('Standing desk');
    expect(items.textContent).toContain('DESK-1');
    expect(items.textContent).toContain('R$\u00a01.999,90');
    expect(items.textContent).toContain('-R$\u00a0100,00');
    expect(items.textContent).toContain('R$\u00a01.899,90');
    expect(items.textContent).toContain('Coffee mug');
    expect(items.textContent).toContain('R$\u00a012,25');
    const summaryList = getByRole(container, 'group', { name: 'Totais do pedido' });
    expect(summaryList.textContent).toContain('Itens3');
    expect(summaryList.textContent).toContain('SubtotalR$\u00a02.024,40');
    expect(summaryList.textContent).toContain('Descontos-R$\u00a0100,00');
    expect(summaryList.textContent).toContain('TotalR$\u00a01.924,40');
    expect(summaryList.textContent).toContain('MoedaBRL');
  });

  it.each([...orderStatuses, 'Shipped'])(
    'shows the status %s as a badge and in the timeline',
    async (status) => {
      const container = await open(detail(status));

      const timeline = getByRole(container, 'list', { name: 'Linha do tempo do status' });
      expect(timeline.textContent).toContain('Pedido realizado');
      expect(timeline.textContent).toContain('26 de out. de 2026');
      expect(container.querySelector('.order-status .badge')).not.toBeNull();
      if (status !== 'Pending') {
        expect(within(timeline).getAllByRole('listitem')).toHaveLength(2);
        expect(timeline.lastElementChild?.textContent).toContain(statusLabels[status] ?? status);
      } else {
        expect(within(timeline).getAllByRole('listitem')).toHaveLength(1);
      }
    },
  );

  it('goes back to the list with the focus on the order that was open', async () => {
    const container = await open(detail());

    fireEvent.click(getByRole(container, 'button', { name: 'Voltar para meus pedidos' }));

    const again = await findByRole(container, 'button', { name: 'ORD-1' });
    expect(queryByRole(container, 'heading', { name: 'Pedido ORD-1' })).toBeNull();
    expect(document.activeElement).toBe(again);
  });

  it('says when the order cannot be found, and the way back still works', async () => {
    useList([summary('ORD-9')]);
    server.use(
      http.get('*/bff/shop/orders/:id', () =>
        HttpResponse.json({ title: 'Not found', detail: 'Order not found.' }, { status: 404 }),
      ),
    );
    const { container, view } = mount();
    view.refresh();

    fireEvent.click(await findByRole(container, 'button', { name: 'ORD-9' }));

    expect((await findByRole(container, 'alert')).textContent).toContain('Order not found.');
    fireEvent.click(getByRole(container, 'button', { name: 'Voltar para meus pedidos' }));
    expect(await findByRole(container, 'button', { name: 'ORD-9' })).toBeTruthy();
  });

  it('shows that it is loading', async () => {
    useList([summary('ORD-9')]);
    server.use(http.get('*/bff/shop/orders/:id', () => new Promise<never>(() => undefined)));
    const { container, view } = mount();
    view.refresh();

    fireEvent.click(await findByRole(container, 'button', { name: 'ORD-9' }));

    expect(getByRole(container, 'status').textContent).toBe('Carregando o pedido…');
  });
});

describe('my orders accessibility', () => {
  it('has no violations for the list, the empty list, an error and an order', async () => {
    useList([summary('ORD-1', 'OnHold'), summary('ORD-2', 'Failed')], 'more');
    server.use(http.get('*/bff/shop/orders/:id', () => HttpResponse.json(detail('OnHold'))));
    const list = mount();
    list.view.refresh();
    await findByRole(list.container, 'button', { name: 'ORD-1' });
    await expectAccessible(list.container);

    fireEvent.click(getByRole(list.container, 'button', { name: 'ORD-1' }));
    await findByRole(list.container, 'heading', { name: 'Pedido ORD-1' });
    await expectAccessible(list.container);
    list.container.remove();

    useList([]);
    const empty = mount();
    empty.view.refresh();
    await findByText(empty.container, 'Você ainda não fez nenhum pedido.');
    await expectAccessible(empty.container);
    empty.container.remove();

    server.use(http.get('*/bff/shop/orders', () => HttpResponse.json({}, { status: 500 })));
    const failed = mount();
    failed.view.refresh();
    await findByRole(failed.container, 'alert');
    await expectAccessible(failed.container);
  });
});
