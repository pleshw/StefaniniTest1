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
];
