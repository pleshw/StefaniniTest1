import {
  findByRole,
  findByText,
  fireEvent,
  getByRole,
  getByText,
  queryByRole,
} from '@testing-library/dom';
import { http, HttpResponse } from 'msw';
import { describe, expect, it, vi } from 'vitest';
import { server } from '../../mocks/server';
import { createHttpClient } from '../../src/shared/api/http';
import { createIdempotencyKeys, type KeyStorage } from '../../src/shared/domain/idempotency';
import type { Quote } from '../../src/shared/api/orders';
import { createCart } from '../../src/shop/domain/cart';
import { mountCart } from '../../src/shop/ui/cart';
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

const desk = { sku: 'DESK-1', name: 'Standing desk' };
const mug = { sku: 'MUG-1', name: 'Coffee mug' };

const line = (
  sku: string,
  productName: string,
  unitPrice: number,
  quantity: number,
  lineDiscount = 0,
) => ({
  sku,
  productName,
  unitPrice,
  catalogVersion: 1,
  quantity,
  lineDiscount,
  lineTotal: unitPrice * quantity - lineDiscount,
});

const quote: Quote = {
  currency: 'BRL',
  itemCount: 3,
  subtotal: 2024.4,
  discountTotal: 100,
  total: 1924.4,
  items: [line('DESK-1', 'Standing desk', 1999.9, 1, 100), line('MUG-1', 'Coffee mug', 24.5, 1)],
};

const placed = { id: 'o1', number: 'ORD-2026-ABC', status: 'Pending', currency: 'BRL' };

function mount(options: { lines?: (typeof desk)[]; debounceMilliseconds?: number } = {}) {
  const container = document.createElement('div');
  document.body.append(container);
  const storage = memoryStorage();
  const cart = createCart({ storage });
  for (const product of options.lines ?? []) {
    cart.add(product);
  }
  const keys = createIdempotencyKeys({ storage, randomUUID: counter() });

  mountCart(container, {
    http: createHttpClient({ sleep: () => Promise.resolve() }),
    cart,
    keys,
    currency: 'BRL',
    debounceMilliseconds: options.debounceMilliseconds ?? 0,
  });

  return { container, cart, keys };
}

function counter() {
  let next = 0;
  return () => `key-${String(++next)}`;
}

function useQuote(answer: Quote = quote): void {
  server.use(http.post('*/bff/shop/cart/quote', () => HttpResponse.json(answer)));
}

const placeOrderButton = (container: HTMLElement) =>
  getByRole<HTMLButtonElement>(container, 'button', { name: /Place order|Placing order/ });

describe('cart view', () => {
  it('says the cart is empty, asks for nothing and cannot place an order', () => {
    let quoted = 0;
    server.use(
      http.post('*/bff/shop/cart/quote', () => {
        quoted++;
        return HttpResponse.json(quote);
      }),
    );

    const { container } = mount();

    expect(getByText(container, 'Your cart is empty.')).toBeTruthy();
    expect(placeOrderButton(container).disabled).toBe(true);
    expect(quoted).toBe(0);
  });

  it('lists the lines and shows the quote: subtotal, discount per line and in total, and total', async () => {
    useQuote();

    const { container } = mount({ lines: [desk, mug] });

    const totals = await findByRole(container, 'group', { name: 'Totals' });
    await vi.waitFor(() => {
      expect(totals.textContent).toContain('R$1,924.40');
    });
    expect(totals.textContent).toContain('Subtotal');
    expect(totals.textContent).toContain('R$2,024.40');
    expect(totals.textContent).toContain('Discounts');
    expect(totals.textContent).toContain('-R$100.00');
    expect(totals.textContent).toContain('3 items');
    const rows = container.querySelectorAll('li');
    expect(rows).toHaveLength(2);
    expect(rows[0]?.textContent).toContain('Standing desk');
    expect(rows[0]?.textContent).toContain('R$1,899.90');
    expect(rows[0]?.textContent).toContain('Discount -R$100.00');
    expect(rows[1]?.textContent).not.toContain('Discount');
  });

  it('sends the cart lines and the page currency to the quote', async () => {
    let body: unknown;
    server.use(
      http.post('*/bff/shop/cart/quote', async ({ request }) => {
        body = await request.json();
        return HttpResponse.json(quote);
      }),
    );

    mount({ lines: [desk, mug] });

    await vi.waitFor(() => {
      expect(body).toEqual({
        currency: 'BRL',
        items: [
          { sku: 'DESK-1', quantity: 1 },
          { sku: 'MUG-1', quantity: 1 },
        ],
      });
    });
  });

  it('changes a quantity, asks for a new quote and shows the new totals', async () => {
    const quantities: number[] = [];
    server.use(
      http.post('*/bff/shop/cart/quote', async ({ request }) => {
        const body = (await request.json()) as { items: { quantity: number }[] };
        quantities.push(body.items[0]?.quantity ?? 0);
        return HttpResponse.json(
          body.items[0]?.quantity === 2
            ? { ...quote, itemCount: 2, total: 3899.8, subtotal: 3999.8, discountTotal: 100 }
            : quote,
        );
      }),
    );
    const { container, cart } = mount({ lines: [desk] });
    await findByText(container, 'R$1,924.40');

    const quantity = getByRole<HTMLInputElement>(container, 'spinbutton', {
      name: 'Quantity of Standing desk',
    });
    fireEvent.change(quantity, { target: { value: '2' } });

    await findByText(container, 'R$3,899.80');
    expect(cart.lines()[0]?.quantity).toBe(2);
    expect(quantities).toEqual([1, 2]);
  });

  it('asks once for a burst of changes (debounce)', async () => {
    let quoted = 0;
    server.use(
      http.post('*/bff/shop/cart/quote', () => {
        quoted++;
        return HttpResponse.json(quote);
      }),
    );
    const { container } = mount({ lines: [desk], debounceMilliseconds: 40 });
    const quantity = getByRole<HTMLInputElement>(container, 'spinbutton', {
      name: 'Quantity of Standing desk',
    });

    for (const value of ['2', '3', '4']) {
      fireEvent.change(quantity, { target: { value } });
    }
    await vi.waitFor(() => {
      expect(quoted).toBe(1);
    });
    await new Promise((resolve) => setTimeout(resolve, 80));

    expect(quoted).toBe(1);
  });

  it('removes a line, and an empty cart goes back to the empty message', async () => {
    useQuote();
    const { container } = mount({ lines: [desk] });
    await findByText(container, 'R$1,924.40');

    fireEvent.click(getByRole(container, 'button', { name: 'Remove Standing desk' }));

    expect(getByText(container, 'Your cart is empty.')).toBeTruthy();
    expect(placeOrderButton(container).disabled).toBe(true);
    expect(queryByRole(container, 'group', { name: 'Totals' })).toBeNull();
  });

  it('updates when a product is added from the catalog', async () => {
    useQuote();
    const { container, cart } = mount();

    cart.add(mug);

    expect(await findByRole(container, 'button', { name: 'Remove Coffee mug' })).toBeTruthy();
    expect(placeOrderButton(container).disabled).toBe(false);
  });

  it('says the prices could not be updated when the quote fails, and still lets the order be placed', async () => {
    server.use(http.post('*/bff/shop/cart/quote', () => HttpResponse.json({}, { status: 500 })));

    const { container } = mount({ lines: [desk] });

    expect((await findByRole(container, 'alert')).textContent).toContain(
      'The prices could not be updated.',
    );
    expect(placeOrderButton(container).disabled).toBe(false);
  });

  it('shows a quote that has no lines for the cart as an unpriced cart', async () => {
    useQuote({ ...quote, items: [] });

    const { container } = mount({ lines: [desk] });

    await findByText(container, 'R$1,924.40');
    expect(container.querySelector('li')?.textContent).not.toContain('R$');
  });
});

describe('checkout', () => {
  it('places the order with the cart, its idempotency key and X-CSRF, then shows the order number and empties the cart', async () => {
    useQuote();
    let request: Request | undefined;
    let body: unknown;
    server.use(
      http.post('*/bff/shop/orders', async ({ request: incoming }) => {
        request = incoming.clone();
        body = await incoming.json();
        return HttpResponse.json(placed, { status: 201 });
      }),
    );
    const { container, cart } = mount({ lines: [desk, mug] });
    await findByText(container, 'R$1,924.40');

    fireEvent.click(placeOrderButton(container));

    await findByText(container, 'Order ORD-2026-ABC was placed.');
    expect(getByRole(container, 'status', { name: 'Order result' }).textContent).toBe(
      'Order ORD-2026-ABC was placed.',
    );
    expect(request?.headers.get('idempotency-key')).toBe('key-1');
    expect(request?.headers.get('x-csrf')).toBe('1');
    expect(body).toEqual({
      currency: 'BRL',
      items: [
        { sku: 'DESK-1', quantity: 1 },
        { sku: 'MUG-1', quantity: 1 },
      ],
    });
    expect(cart.lines()).toEqual([]);
    expect(getByText(container, 'Your cart is empty.')).toBeTruthy();
  });

  it('forgets the key after a 201, so the next order gets another one', async () => {
    useQuote();
    const keys: (string | null)[] = [];
    server.use(
      http.post('*/bff/shop/orders', ({ request }) => {
        keys.push(request.headers.get('idempotency-key'));
        return HttpResponse.json(placed, { status: 201 });
      }),
    );
    const { container, cart } = mount({ lines: [desk] });
    await findByText(container, 'R$1,924.40');
    fireEvent.click(placeOrderButton(container));
    await findByText(container, 'Order ORD-2026-ABC was placed.');

    cart.add(desk);
    await findByText(container, 'R$1,924.40');
    fireEvent.click(placeOrderButton(container));
    await vi.waitFor(() => {
      expect(keys).toHaveLength(2);
    });

    expect(keys).toEqual(['key-1', 'key-2']);
  });

  it('sends one request on a double click and disables the button while it is sent', async () => {
    useQuote();
    let calls = 0;
    let release: () => void = () => undefined;
    const gate = new Promise<void>((resolve) => {
      release = resolve;
    });
    server.use(
      http.post('*/bff/shop/orders', async () => {
        calls++;
        await gate;
        return HttpResponse.json(placed, { status: 201 });
      }),
    );
    const { container } = mount({ lines: [desk] });
    await findByText(container, 'R$1,924.40');

    fireEvent.click(placeOrderButton(container));
    fireEvent.click(placeOrderButton(container));

    const button = placeOrderButton(container);
    expect(button.disabled).toBe(true);
    expect(button.textContent).toContain('Placing order…');
    release();
    await findByText(container, 'Order ORD-2026-ABC was placed.');
    expect(calls).toBe(1);
  });

  it('retries after a timeout with the same key, also when the person presses the button again', async () => {
    useQuote();
    const keys: (string | null)[] = [];
    let failing = true;
    server.use(
      http.post('*/bff/shop/orders', ({ request }) => {
        keys.push(request.headers.get('idempotency-key'));
        return failing ? HttpResponse.error() : HttpResponse.json(placed, { status: 201 });
      }),
    );
    const { container } = mount({ lines: [desk] });
    await findByText(container, 'R$1,924.40');

    fireEvent.click(placeOrderButton(container));
    const alert = await findByRole(container, 'alert', { name: 'Order error' });
    expect(alert.textContent).toContain('The server could not be reached.');
    expect(placeOrderButton(container).disabled).toBe(false);

    failing = false;
    fireEvent.click(placeOrderButton(container));
    await findByText(container, 'Order ORD-2026-ABC was placed.');

    expect(new Set(keys)).toEqual(new Set(['key-1']));
    expect(keys).toHaveLength(4);
  });

  it('shows a 409 (discount already used) inline, keeps the cart and the key', async () => {
    useQuote();
    const keys: (string | null)[] = [];
    server.use(
      http.post('*/bff/shop/orders', ({ request }) => {
        keys.push(request.headers.get('idempotency-key'));
        return HttpResponse.json(
          { title: 'Conflict', status: 409, detail: 'The discount was already used.' },
          { status: 409 },
        );
      }),
    );
    const { container, cart } = mount({ lines: [desk] });
    await findByText(container, 'R$1,924.40');

    fireEvent.click(placeOrderButton(container));

    expect((await findByRole(container, 'alert', { name: 'Order error' })).textContent).toContain(
      'The discount was already used.',
    );
    expect(cart.lines()).toHaveLength(1);
    fireEvent.click(placeOrderButton(container));
    await vi.waitFor(() => {
      expect(keys).toHaveLength(2);
    });
    expect(keys).toEqual(['key-1', 'key-1']);
  });

  it('shows every message of a 422, and a changed cart gets a new key', async () => {
    useQuote();
    const keys: (string | null)[] = [];
    server.use(
      http.post('*/bff/shop/orders', ({ request }) => {
        keys.push(request.headers.get('idempotency-key'));
        return HttpResponse.json(
          {
            title: 'Unprocessable',
            status: 422,
            errors: { items: ['SKU DESK-1 is inactive.', 'SKU X is missing.'] },
          },
          { status: 422 },
        );
      }),
    );
    const { container, cart } = mount({ lines: [desk] });
    await findByText(container, 'R$1,924.40');

    fireEvent.click(placeOrderButton(container));
    const alert = await findByRole(container, 'alert', { name: 'Order error' });
    expect([...alert.querySelectorAll('li')].map((item) => item.textContent)).toEqual([
      'SKU DESK-1 is inactive.',
      'SKU X is missing.',
    ]);

    cart.add(mug);
    await vi.waitFor(() => {
      expect(queryByRole(container, 'alert', { name: 'Order error' })).toBeNull();
    });
    fireEvent.click(placeOrderButton(container));
    await vi.waitFor(() => {
      expect(keys).toHaveLength(2);
    });
    expect(keys).toEqual(['key-1', 'key-2']);
  });

  it('explains an error it does not know', async () => {
    useQuote();
    server.use(http.post('*/bff/shop/orders', () => HttpResponse.json({}, { status: 500 })));
    const { container } = mount({ lines: [desk] });
    await findByText(container, 'R$1,924.40');

    fireEvent.click(placeOrderButton(container));

    expect((await findByRole(container, 'alert', { name: 'Order error' })).textContent).toContain(
      'The request failed with status 500.',
    );
  });
});

describe('cart view accessibility', () => {
  it('has no violations when empty, with lines and totals, with an error and after an order', async () => {
    const empty = mount();
    await expectAccessible(empty.container);
    empty.container.remove();

    useQuote();
    server.use(
      http.post('*/bff/shop/orders', () =>
        HttpResponse.json({ title: 'Conflict', detail: 'Used.' }, { status: 409 }),
      ),
    );
    const full = mount({ lines: [desk, mug] });
    await findByText(full.container, 'R$1,924.40');
    await expectAccessible(full.container);

    fireEvent.click(placeOrderButton(full.container));
    await findByRole(full.container, 'alert', { name: 'Order error' });
    await expectAccessible(full.container);
    full.container.remove();

    server.use(http.post('*/bff/shop/orders', () => HttpResponse.json(placed, { status: 201 })));
    const done = mount({ lines: [desk] });
    await findByText(done.container, 'R$1,924.40');
    fireEvent.click(placeOrderButton(done.container));
    await findByText(done.container, 'Order ORD-2026-ABC was placed.');
    await expectAccessible(done.container);
  });
});
