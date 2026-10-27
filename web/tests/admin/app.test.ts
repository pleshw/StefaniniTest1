import { findByRole, fireEvent, getByRole, queryByRole } from '@testing-library/dom';
import { http, HttpResponse } from 'msw';
import { describe, expect, it, vi } from 'vitest';
import { server } from '../../mocks/server';
import { mountAdmin } from '../../src/admin/ui/app';
import { createHttpClient } from '../../src/shared/api/http';
import { FakeSource } from '../fakeEventSource';
import { expectAccessible } from '../helpers';

const orderId = 'b6c1f0a2-0000-4000-8000-000000000001';

function mountPage() {
  const root = document.createElement('div');
  document.body.append(root);
  const sources: FakeSource[] = [];
  mountAdmin(root, {
    http: createHttpClient({ sleep: () => Promise.resolve() }),
    createSource: (url) => {
      const source = new FakeSource(url);
      sources.push(source);
      return source;
    },
  });

  return { root, source: (): FakeSource | undefined => sources[0] };
}

const onHoldEvent = {
  id: 7,
  orderId,
  type: 'OrderStatusChanged.v1',
  occurredAt: '2026-10-26T12:05:00Z',
  cursor: 'c7',
  payload: { from: 'Pending', to: 'OnHold', reason: 'Rule', actor: 'system:worker' },
};

describe('Admin page', () => {
  it('shows the live table, the OnHold queue and the inspector together', async () => {
    const { root } = mountPage();

    expect(getByRole(root, 'heading', { level: 2, name: 'Eventos ao vivo' })).toBeTruthy();
    expect(getByRole(root, 'heading', { level: 2, name: 'Fila em espera' })).toBeTruthy();
    expect(getByRole(root, 'heading', { level: 2, name: 'Inspetor de pedidos' })).toBeTruthy();
    expect(
      getByRole(root, 'button', { name: 'Eventos ao vivo' }).getAttribute('aria-current'),
    ).toBe('true');
    await findByRole(root, 'button', { name: 'Inspecionar ORD-2026-STUB00000003' });
  });

  it('opens the inspector from a live event and refreshes the OnHold queue when an order enters it', async () => {
    let queueCalls = 0;
    server.use(
      http.get('*/bff/admin/orders', () => {
        queueCalls++;
        return HttpResponse.json({
          onHold: [],
          orders: null,
          partial: true,
          unavailable: ['orders'],
        });
      }),
    );
    const { root, source } = mountPage();
    await vi.waitFor(() => {
      expect(queueCalls).toBe(1);
    });

    source()?.emit(onHoldEvent.type, JSON.stringify(onHoldEvent), 'c7');
    await vi.waitFor(() => {
      expect(queueCalls).toBe(2);
    });
    fireEvent.click(
      await findByRole(root, 'button', { name: `Inspecionar ${orderId.slice(0, 8)}` }),
    );

    expect(await findByRole(root, 'heading', { level: 3, name: /^Pedido ORD-/ })).toBeTruthy();
  });

  it('refreshes the queue after an action on an order', async () => {
    let queueCalls = 0;
    const waiting = {
      onHold: [
        {
          id: orderId,
          number: 'ORD-9',
          client: { id: 'c1', name: 'Ana Souza' },
          status: 'OnHold',
          channel: 'Shop',
          currency: 'BRL',
          itemCount: 1,
          subtotal: 10,
          discountTotal: 0,
          total: 10,
          createdAt: '2026-10-26T12:00:00Z',
        },
      ],
      orders: null,
      partial: true,
      unavailable: ['orders'],
    };
    server.use(
      http.get('*/bff/admin/orders', () => {
        queueCalls++;
        return HttpResponse.json(waiting);
      }),
    );
    const { root } = mountPage();

    fireEvent.click(await findByRole(root, 'button', { name: 'Inspecionar ORD-9' }));
    await findByRole(root, 'heading', { level: 3, name: /^Pedido ORD-/ });
    const before = queueCalls;
    fireEvent.click(getByRole(root, 'button', { name: 'Confirmar' }));
    fireEvent.click(getByRole(document.body, 'button', { name: 'Confirmar pedido' }));

    await vi.waitFor(() => {
      expect(queueCalls).toBeGreaterThan(before);
    });
  });

  it('opens "Create order" only when asked, and goes back to the live view', async () => {
    let clientCalls = 0;
    server.use(
      http.get('*/bff/admin/orders', ({ request }) => {
        if (new URL(request.url).searchParams.get('limit') === '50') {
          clientCalls++;
        }
        return HttpResponse.json({
          onHold: [],
          orders: { items: [], next: null },
          partial: false,
          unavailable: [],
        });
      }),
    );
    const { root } = mountPage();
    expect(clientCalls).toBe(0);
    expect(queryByRole(root, 'heading', { name: 'Criar pedido para um cliente' })).toBeNull();

    fireEvent.click(getByRole(root, 'button', { name: 'Criar pedido' }));

    expect(
      await findByRole(root, 'heading', { name: 'Criar pedido para um cliente' }),
    ).toBeTruthy();
    expect(queryByRole(root, 'heading', { name: 'Eventos ao vivo' })).toBeNull();
    await vi.waitFor(() => {
      expect(clientCalls).toBe(1);
    });
    fireEvent.click(getByRole(root, 'button', { name: 'Eventos ao vivo' }));
    fireEvent.click(getByRole(root, 'button', { name: 'Criar pedido' }));
    expect(clientCalls).toBe(1);
    await expectAccessible(root);
  });

  it.each([
    ['Regras de desconto', 'Regras de desconto', '*/bff/admin/discount-rules'],
    ['Regras de confirmação', 'Regras de confirmação', '*/bff/admin/confirmation-rules'],
    ['Mensagens mortas', 'Mensagens mortas', '*/bff/admin/dead-letters'],
  ])('opens "%s" only when asked and asks for its data once', async (label, heading, route) => {
    let calls = 0;
    server.use(
      http.get(route, () => {
        calls++;
        return HttpResponse.json([]);
      }),
    );
    const { root } = mountPage();
    expect(calls).toBe(0);
    expect(queryByRole(root, 'heading', { level: 2, name: heading })).toBeNull();

    fireEvent.click(getByRole(root, 'button', { name: label }));

    expect(await findByRole(root, 'heading', { level: 2, name: heading })).toBeTruthy();
    expect(getByRole(root, 'button', { name: label }).getAttribute('aria-current')).toBe('true');
    expect(queryByRole(root, 'heading', { name: 'Eventos ao vivo' })).toBeNull();
    await vi.waitFor(() => {
      expect(calls).toBe(1);
    });
    fireEvent.click(getByRole(root, 'button', { name: 'Eventos ao vivo' }));
    fireEvent.click(getByRole(root, 'button', { name: label }));
    expect(calls).toBe(1);
    await expectAccessible(root);
  });

  it('opens the catalog editor only when asked, and lists the products then', async () => {
    let calls = 0;
    server.use(
      http.get('*/bff/shop/products', () => {
        calls++;
        return HttpResponse.json({ items: [], next: null });
      }),
    );
    const { root } = mountPage();
    expect(calls).toBe(0);

    fireEvent.click(getByRole(root, 'button', { name: 'Catálogo' }));

    expect(await findByRole(root, 'heading', { level: 2, name: 'Catálogo' })).toBeTruthy();
    await vi.waitFor(() => {
      expect(calls).toBe(1);
    });
    await expectAccessible(root);
  });
});
