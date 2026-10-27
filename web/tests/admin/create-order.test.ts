import {
  findByLabelText,
  findByRole,
  findByText,
  fireEvent,
  getByLabelText,
  getByRole,
} from '@testing-library/dom';
import { http, HttpResponse } from 'msw';
import { describe, expect, it, vi } from 'vitest';
import { server } from '../../mocks/server';
import { mountCreateOrder } from '../../src/admin/ui/createOrder';
import { createHttpClient } from '../../src/shared/api/http';
import { createIdempotencyKeys, type KeyStorage } from '../../src/shared/domain/idempotency';
import { createCart } from '../../src/shop/domain/cart';
import { expectAccessible } from '../helpers';

function memoryStorage(): KeyStorage {
  const data = new Map<string, string>();

  return {
    getItem: (name) => data.get(name) ?? null,
    setItem: (name, value) => {
      data.set(name, value);
    },
    removeItem: (name) => {
      data.delete(name);
    },
  };
}

const ana = { id: '11111111-1111-4111-8111-111111111111', name: 'Ana Souza' };
const bruno = { id: '22222222-2222-4222-8222-222222222222', name: 'Bruno Lima' };

const orderFor = (client: { id: string; name: string }) => ({
  id: 'o1',
  number: 'ORD-2026-ADMIN',
  client,
  status: 'Pending',
  channel: 'Admin',
  currency: 'BRL',
});

function summary(client: { id: string; name: string }, number: string) {
  return {
    id: `id-${number}`,
    number,
    client,
    status: 'Confirmed',
    channel: 'Shop',
    currency: 'BRL',
    itemCount: 1,
    subtotal: 1,
    discountTotal: 0,
    total: 1,
    createdAt: '2026-10-26T12:00:00Z',
  };
}

function useKnownClients(): void {
  server.use(
    http.get('*/bff/admin/orders', () =>
      HttpResponse.json({
        onHold: [],
        orders: {
          items: [summary(ana, 'A1'), summary(bruno, 'B1'), summary(ana, 'A2')],
          next: null,
        },
        partial: false,
        unavailable: [],
      }),
    ),
  );
}

function mount() {
  const container = document.createElement('div');
  document.body.append(container);
  const storage = memoryStorage();
  mountCreateOrder(container, {
    http: createHttpClient({ sleep: () => Promise.resolve() }),
    cart: createCart({ storage }),
    keys: createIdempotencyKeys({ storage }),
  });

  return container;
}

async function addDesk(container: HTMLElement): Promise<void> {
  fireEvent.click(
    await findByRole(container, 'button', { name: 'Adicionar Standing desk ao carrinho' }),
  );
  await findByRole(container, 'button', { name: 'Remover Standing desk' });
}

const placeOrder = (container: HTMLElement): HTMLElement =>
  getByRole(container, 'button', { name: 'Fazer pedido' });

describe('create order for a client', () => {
  it('offers the clients seen in the orders, once each, and a way to enter another', async () => {
    useKnownClients();
    const container = mount();

    const select = await waitForClients(container);

    expect([...select.options].map((option) => option.text)).toEqual([
      'Escolha um cliente',
      'Ana Souza',
      'Bruno Lima',
      'Outro cliente…',
    ]);
  });

  it('places the order for the chosen client with the cart, one key and X-CSRF', async () => {
    useKnownClients();
    let request: Request | undefined;
    let body: unknown;
    server.use(
      http.post('*/bff/admin/orders', async ({ request: incoming }) => {
        request = incoming.clone();
        body = await incoming.json();
        return HttpResponse.json(orderFor(ana), { status: 201 });
      }),
    );
    const container = mount();
    const select = await waitForClients(container);
    fireEvent.change(select, { target: { value: ana.id } });
    await addDesk(container);

    fireEvent.click(placeOrder(container));

    await findByText(container, 'O pedido ORD-2026-ADMIN foi feito.');
    expect(body).toEqual({
      currency: 'BRL',
      items: [{ sku: 'DESK-1', quantity: 1 }],
      clientId: ana.id,
      clientName: 'Ana Souza',
    });
    expect(request?.headers.get('idempotency-key')).toMatch(/^[0-9a-f-]{36}$/);
    expect(request?.headers.get('x-csrf')).toBe('1');
  });

  it('asks to choose a client first and sends nothing', async () => {
    useKnownClients();
    let calls = 0;
    server.use(
      http.post('*/bff/admin/orders', () => {
        calls++;
        return HttpResponse.json(orderFor(ana), { status: 201 });
      }),
    );
    const container = mount();
    await waitForClients(container);
    await addDesk(container);

    fireEvent.click(placeOrder(container));

    expect((await findByRole(container, 'alert', { name: 'Erro no pedido' })).textContent).toBe(
      'Escolha um cliente primeiro.',
    );
    expect(calls).toBe(0);
  });

  it('takes another client by id and name, and checks both before sending', async () => {
    useKnownClients();
    let body: { clientId?: string; clientName?: string } = {};
    server.use(
      http.post('*/bff/admin/orders', async ({ request }) => {
        body = (await request.json()) as typeof body;
        return HttpResponse.json(orderFor(ana), { status: 201 });
      }),
    );
    const container = mount();
    const select = await waitForClients(container);
    expect(getByLabelText(container, 'ID do cliente').closest('.row')?.hasAttribute('hidden')).toBe(
      true,
    );
    fireEvent.change(select, { target: { value: 'other' } });
    await addDesk(container);

    fireEvent.input(getByLabelText(container, 'ID do cliente'), {
      target: { value: 'not-a-guid' },
    });
    fireEvent.input(getByLabelText(container, 'Nome do cliente'), {
      target: { value: 'Carla Dias' },
    });
    fireEvent.click(placeOrder(container));
    expect((await findByRole(container, 'alert', { name: 'Erro no pedido' })).textContent).toBe(
      'Informe um ID de cliente válido (um GUID).',
    );

    fireEvent.input(getByLabelText(container, 'ID do cliente'), {
      target: { value: '33333333-3333-4333-8333-333333333333' },
    });
    fireEvent.input(getByLabelText(container, 'Nome do cliente'), { target: { value: '  ' } });
    fireEvent.click(placeOrder(container));
    await findByText(container, 'Informe o nome do cliente.');

    fireEvent.input(getByLabelText(container, 'Nome do cliente'), {
      target: { value: 'Carla Dias' },
    });
    fireEvent.click(placeOrder(container));
    await findByText(container, 'O pedido ORD-2026-ADMIN foi feito.');
    expect(body).toMatchObject({
      clientId: '33333333-3333-4333-8333-333333333333',
      clientName: 'Carla Dias',
    });
  });

  it('uses one idempotency key per client: the same cart for another client is another order', async () => {
    useKnownClients();
    const keys: (string | null)[] = [];
    server.use(
      http.post('*/bff/admin/orders', ({ request }) => {
        keys.push(request.headers.get('idempotency-key'));
        return HttpResponse.json({ title: 'Conflict', detail: 'Used.' }, { status: 409 });
      }),
    );
    const container = mount();
    const select = await waitForClients(container);
    await addDesk(container);

    fireEvent.change(select, { target: { value: ana.id } });
    fireEvent.click(placeOrder(container));
    await findByRole(container, 'alert', { name: 'Erro no pedido' });
    fireEvent.click(placeOrder(container));
    await vi.waitFor(() => {
      expect(keys).toHaveLength(2);
    });
    fireEvent.change(select, { target: { value: bruno.id } });
    fireEvent.click(placeOrder(container));
    await vi.waitFor(() => {
      expect(keys).toHaveLength(3);
    });

    expect(keys[0]).toBe(keys[1]);
    expect(keys[2]).not.toBe(keys[0]);
  });

  it('still lets another client be entered when the orders cannot be read', async () => {
    server.use(http.get('*/bff/admin/orders', () => HttpResponse.json({}, { status: 500 })));

    const container = mount();

    const select = await waitForClients(container);
    expect([...select.options].map((option) => option.text)).toEqual([
      'Escolha um cliente',
      'Outro cliente…',
    ]);
  });

  it('says the prices are estimates for the signed-in admin', async () => {
    useKnownClients();

    const container = mount();
    await waitForClients(container);

    expect(container.textContent).toContain(
      'A cotação é calculada para você; o Orders precifica o pedido para o cliente.',
    );
  });

  it('has no accessibility violations', async () => {
    useKnownClients();
    const container = mount();
    const select = await waitForClients(container);
    await expectAccessible(container);
    fireEvent.change(select, { target: { value: 'other' } });
    await expectAccessible(container);
  });
});

function waitForClients(container: HTMLElement): Promise<HTMLSelectElement> {
  return findByLabelText<HTMLSelectElement>(container, 'Cliente');
}
