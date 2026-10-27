import { http, HttpResponse } from 'msw';

/** A few products, as the BFF's shop view model returns them. */
const products = [
  {
    sku: 'DESK-1',
    name: 'Standing desk',
    description: 'Adjustable height.',
    category: 'Office',
    price: 1999.9,
  },
  {
    sku: 'LAMP-1',
    name: 'Desk lamp',
    description: 'Warm light.',
    category: 'Office',
    price: 149.0,
  },
  {
    sku: 'MUG-1',
    name: 'Coffee mug',
    description: 'Holds coffee.',
    category: 'Kitchen',
    price: 24.5,
  },
];

const stubClient = { id: 'a1d3e5f7-2b4c-4d6e-8f01-3c5e7a9b0d22', name: 'shopper' };

const stubOrders = [
  { number: 'ORD-2026-STUB00000003', status: 'OnHold', quantity: 2, price: 149.0 },
  { number: 'ORD-2026-STUB00000002', status: 'Confirmed', quantity: 1, price: 1999.9 },
  { number: 'ORD-2026-STUB00000001', status: 'Pending', quantity: 4, price: 24.5 },
].map(({ number, status, quantity, price }, index) => {
  const total = price * quantity;

  return {
    id: `b6c1f0a2-0000-4000-8000-00000000000${String(index + 1)}`,
    number,
    client: stubClient,
    status,
    channel: 'Shop',
    currency: 'BRL',
    itemCount: quantity,
    subtotal: total,
    discountTotal: 0,
    total,
    createdAt: `2026-10-2${String(6 - index)}T12:00:00Z`,
    rowVersion: 1,
    items: [
      {
        sku: 'STUB-1',
        productName: 'Stub product',
        unitPrice: price,
        catalogVersion: 1,
        quantity,
        lineDiscount: 0,
        lineTotal: total,
      },
    ],
  };
});

/** The list shows an order without its items. */
function summaryOf(order: (typeof stubOrders)[number]) {
  const {
    id,
    number,
    client,
    status,
    channel,
    currency,
    itemCount,
    subtotal,
    discountTotal,
    total,
    createdAt,
  } = order;

  return {
    id,
    number,
    client,
    status,
    channel,
    currency,
    itemCount,
    subtotal,
    discountTotal,
    total,
    createdAt,
  };
}

/**
 * BFF stubs, so pages and tests can run without the back end. Each front-end step adds the routes it
 * uses; the shapes follow the BFF's answers.
 */
export const handlers = [
  http.get('*/bff/user', () =>
    HttpResponse.json({
      sub: 'a1d3e5f7-2b4c-4d6e-8f01-3c5e7a9b0d22',
      name: 'admin',
      roles: ['Admin'],
    }),
  ),

  // Keyset paging: the cursor is the index of the first product of the next page.
  http.get('*/bff/shop/products', ({ request }) => {
    const query = new URL(request.url).searchParams;
    const currency = query.get('currency') ?? 'BRL';
    const q = (query.get('q') ?? '').toLowerCase();
    const category = query.get('category');
    const limit = Number(query.get('limit') ?? 12);
    const start = Number(query.get('after') ?? 0);
    const matching = products.filter(
      (product) =>
        product.name.toLowerCase().includes(q) &&
        (category === null || product.category === category),
    );
    const items = matching.slice(start, start + limit).map(({ price, ...product }) => ({
      ...product,
      price: { amount: price, currency },
    }));

    return HttpResponse.json({
      items,
      next: start + limit < matching.length ? String(start + limit) : null,
    });
  }),

  // The quote prices the lines from the stub products and takes 5% off a subtotal above 2000.
  http.post('*/bff/shop/cart/quote', async ({ request }) => {
    const body = (await request.json()) as {
      currency: string;
      items: { sku: string; quantity: number }[];
    };
    const priced = body.items.flatMap(({ sku, quantity }) => {
      const product = products.find((candidate) => candidate.sku === sku);
      return product === undefined ? [] : [{ product, quantity }];
    });
    const subtotal = priced.reduce(
      (sum, { product, quantity }) => sum + product.price * quantity,
      0,
    );
    const rate = subtotal > 2000 ? 0.05 : 0;

    return HttpResponse.json({
      currency: body.currency,
      itemCount: priced.reduce((sum, { quantity }) => sum + quantity, 0),
      subtotal,
      discountTotal: subtotal * rate,
      total: subtotal * (1 - rate),
      items: priced.map(({ product, quantity }) => ({
        sku: product.sku,
        productName: product.name,
        unitPrice: product.price,
        catalogVersion: 1,
        quantity,
        lineDiscount: product.price * quantity * rate,
        lineTotal: product.price * quantity * (1 - rate),
      })),
    });
  }),

  http.post('*/bff/shop/orders', () =>
    HttpResponse.json(
      {
        id: 'b6c1f0a2-0000-4000-8000-000000000001',
        number: 'ORD-2026-STUB00000001',
        status: 'Pending',
        currency: 'BRL',
      },
      { status: 201 },
    ),
  ),

  // The signed-in client's orders: the same three stub orders, newest first.
  http.get('*/bff/shop/orders', ({ request }) => {
    const query = new URL(request.url).searchParams;
    const limit = Number(query.get('limit') ?? 10);
    const start = Number(query.get('after') ?? 0);

    return HttpResponse.json({
      items: stubOrders.slice(start, start + limit).map(summaryOf),
      next: start + limit < stubOrders.length ? String(start + limit) : null,
    });
  }),

  http.get('*/bff/shop/orders/:id', ({ params }) => {
    const order = stubOrders.find((candidate) => candidate.id === params['id']);

    return order === undefined
      ? HttpResponse.json(
          { title: 'Not found', status: 404, detail: 'Order not found.' },
          { status: 404 },
        )
      : HttpResponse.json(order);
  }),

  // Admin: the OnHold queue (oldest first) and one page of orders, the inspector, actions and events.
  http.get('*/bff/admin/orders', ({ request }) => {
    const limit = Number(new URL(request.url).searchParams.get('limit') ?? 20);

    return HttpResponse.json({
      onHold: stubOrders.filter((order) => order.status === 'OnHold').map(summaryOf),
      orders: { items: stubOrders.slice(0, limit).map(summaryOf), next: null },
      partial: false,
      unavailable: [],
    });
  }),

  http.get('*/bff/admin/orders/:id', ({ params }) => {
    const order = stubOrders.find((candidate) => candidate.id === params['id']);

    return order === undefined
      ? HttpResponse.json(
          { title: 'Not found', status: 404, detail: 'Order not found.' },
          { status: 404 },
        )
      : HttpResponse.json({
          order,
          history: [
            {
              seq: 1,
              from: null,
              to: 'Pending',
              reason: null,
              actor: order.client.id,
              changedAt: order.createdAt,
            },
          ],
          events: [],
          partial: false,
          unavailable: [],
        });
  }),

  http.post('*/bff/admin/orders/:id/:action', ({ params }) => {
    const order = stubOrders.find((candidate) => candidate.id === params['id']);

    return order === undefined
      ? HttpResponse.json({ title: 'Not found', status: 404 }, { status: 404 })
      : HttpResponse.json({ ...order, rowVersion: order.rowVersion + 1 });
  }),

  http.get('*/bff/admin/events', () => HttpResponse.json({ items: [], next: null })),

  http.post('*/bff/admin/orders', () =>
    HttpResponse.json(
      {
        id: 'b6c1f0a2-0000-4000-8000-000000000009',
        number: 'ORD-2026-STUBADMIN001',
        status: 'Pending',
        currency: 'BRL',
      },
      { status: 201 },
    ),
  ),
];
