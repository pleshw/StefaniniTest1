import { findByRole, findByText, fireEvent, getByRole, queryByRole } from '@testing-library/dom';
import { http, HttpResponse } from 'msw';
import { describe, expect, it, vi } from 'vitest';
import { server } from '../../mocks/server';
import { mountOnHold } from '../../src/admin/ui/onHold';
import { createHttpClient } from '../../src/shared/api/http';
import type { OrderSummary } from '../../src/shared/api/orders';
import { expectAccessible } from '../helpers';

const summary = (number: string, total: number, createdAt: string): OrderSummary => ({
  id: `id-${number}`,
  number,
  client: { id: 'c1', name: 'Ana Souza' },
  status: 'OnHold',
  channel: 'Shop',
  currency: 'BRL',
  itemCount: 1,
  subtotal: total,
  discountTotal: 0,
  total,
  createdAt,
});

function mount() {
  const container = document.createElement('div');
  document.body.append(container);
  const onInspect = vi.fn();
  const queue = mountOnHold(container, {
    http: createHttpClient({ sleep: () => Promise.resolve() }),
    onInspect,
  });

  return { container, queue, onInspect };
}

function useQueue(answer: object): void {
  server.use(http.get('*/bff/admin/orders', () => HttpResponse.json(answer)));
}

describe('Fila em espera', () => {
  it('loads when it is mounted and lists the oldest order first, as the BFF returns them', async () => {
    useQueue({
      onHold: [
        summary('ORD-OLD', 5000, '2026-10-25T09:00:00Z'),
        summary('ORD-NEW', 7500.5, '2026-10-26T09:00:00Z'),
      ],
      orders: { items: [], next: null },
      partial: false,
      unavailable: [],
    });

    const { container } = mount();

    const items = await vi.waitFor(() => {
      const found = container.querySelectorAll('li');
      expect(found).toHaveLength(2);
      return found;
    });
    expect(items[0]?.textContent).toContain('ORD-OLD');
    expect(items[0]?.textContent).toContain('Ana Souza');
    expect(items[0]?.textContent).toContain('R$\u00a05.000,00');
    expect(items[0]?.textContent).toContain('25 de out. de 2026');
    expect(items[1]?.textContent).toContain('ORD-NEW');
    expect(getByRole(container, 'status').textContent).toBe('2 pedidos aguardando');
  });

  it('opens the inspector on an order', async () => {
    useQueue({
      onHold: [summary('ORD-OLD', 5000, '2026-10-25T09:00:00Z')],
      orders: null,
      partial: true,
      unavailable: ['orders'],
    });
    const { container, onInspect } = mount();

    fireEvent.click(await findByRole(container, 'button', { name: 'Inspecionar ORD-OLD' }));

    expect(onInspect).toHaveBeenCalledExactlyOnceWith('id-ORD-OLD');
  });

  it('says the queue is empty', async () => {
    useQueue({ onHold: [], orders: null, partial: false, unavailable: [] });

    const { container } = mount();

    await findByText(container, 'Nenhum pedido em espera.');
    expect(queryByRole(container, 'list')).toBeNull();
  });

  it('says when the queue could not be read, and tries again', async () => {
    useQueue({
      onHold: null,
      orders: { items: [], next: null },
      partial: true,
      unavailable: ['onHold'],
    });
    const { container, queue } = mount();

    const alert = await findByRole(container, 'alert');
    expect(alert.textContent).toContain('Não foi possível carregar a fila em espera.');

    useQueue({
      onHold: [summary('ORD-1', 10, '2026-10-26T09:00:00Z')],
      orders: null,
      partial: true,
      unavailable: [],
    });
    fireEvent.click(getByRole(alert, 'button', { name: 'Tentar novamente' }));
    expect(await findByRole(container, 'button', { name: 'Inspecionar ORD-1' })).toBeTruthy();
    expect(queryByRole(container, 'alert')).toBeNull();

    useQueue({ onHold: [], orders: null, partial: false, unavailable: [] });
    queue.refresh();
    await findByText(container, 'Nenhum pedido em espera.');
  });

  it('shows the error when the request fails', async () => {
    server.use(
      http.get('*/bff/admin/orders', () =>
        HttpResponse.json({ title: 'Orders is unavailable' }, { status: 503 }),
      ),
    );

    const { container } = mount();

    expect((await findByRole(container, 'alert')).textContent).toContain('Orders is unavailable');
  });

  it('keeps the list while it refreshes', async () => {
    useQueue({
      onHold: [summary('ORD-1', 10, '2026-10-26T09:00:00Z')],
      orders: null,
      partial: false,
      unavailable: [],
    });
    const { container, queue } = mount();
    await findByRole(container, 'button', { name: 'Inspecionar ORD-1' });
    server.use(http.get('*/bff/admin/orders', () => new Promise<never>(() => undefined)));

    queue.refresh();

    expect(getByRole(container, 'button', { name: 'Inspecionar ORD-1' })).toBeTruthy();
  });
});

describe('OnHold queue accessibility', () => {
  it('has no violations with orders, empty and with an error', async () => {
    useQueue({
      onHold: [summary('ORD-1', 10, '2026-10-26T09:00:00Z')],
      orders: null,
      partial: false,
      unavailable: [],
    });
    const full = mount();
    await findByRole(full.container, 'button', { name: 'Inspecionar ORD-1' });
    await expectAccessible(full.container);
    full.container.remove();

    useQueue({ onHold: [], orders: null, partial: false, unavailable: [] });
    const empty = mount();
    await findByText(empty.container, 'Nenhum pedido em espera.');
    await expectAccessible(empty.container);
    empty.container.remove();

    useQueue({ onHold: null, orders: null, partial: true, unavailable: ['onHold'] });
    const failed = mount();
    await findByRole(failed.container, 'alert');
    await expectAccessible(failed.container);
  });
});
