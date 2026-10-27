import {
  findAllByRole,
  findByRole,
  findByText,
  fireEvent,
  getByRole,
  queryByRole,
} from '@testing-library/dom';
import { http, HttpResponse } from 'msw';
import { describe, expect, it, vi } from 'vitest';
import { server } from '../../mocks/server';
import type { ShopProduct, ShopProductPage } from '../../src/shop/api/catalog';
import { mountCatalog } from '../../src/shop/ui/catalog';
import { createHttpClient } from '../../src/shared/api/http';
import { expectAccessible } from '../helpers';

const desk: ShopProduct = {
  sku: 'DESK-1',
  name: 'Standing desk',
  description: 'Adjustable height.',
  category: 'Office',
  price: { amount: 1999.9, currency: 'BRL' },
};
const mug: ShopProduct = {
  sku: 'MUG-1',
  name: 'Coffee mug',
  description: 'Holds coffee.',
  category: 'Kitchen',
  price: { amount: 24.5, currency: 'BRL' },
};
const unpriced: ShopProduct = {
  sku: 'OLD-1',
  name: 'Old lamp',
  description: 'No price in this currency.',
  category: 'Office',
  price: null,
};

const page = (items: ShopProduct[], next: string | null = null): ShopProductPage => ({
  items,
  next,
});

function mount(overrides: { onAdd?: (product: ShopProduct) => void; pageSize?: number } = {}) {
  const container = document.createElement('div');
  document.body.append(container);
  const onAdd = overrides.onAdd ?? vi.fn();

  mountCatalog(container, {
    http: createHttpClient({ sleep: () => Promise.resolve() }),
    currency: 'BRL',
    onAdd,
    ...(overrides.pageSize === undefined ? {} : { pageSize: overrides.pageSize }),
  });

  return { container, onAdd };
}

describe('catalog view', () => {
  it('shows each product with its name, category, price and an add button named after it', async () => {
    server.use(http.get('*/bff/shop/products', () => HttpResponse.json(page([desk, mug]))));

    const { container } = mount();

    const items = await findAllByRole(container, 'listitem');
    expect(items).toHaveLength(2);
    expect(getByRole(container, 'heading', { level: 3, name: 'Standing desk' })).toBeTruthy();
    expect(items[0]?.textContent).toContain('Office');
    expect(items[0]?.textContent).toContain('R$1,999.90');
    expect(items[0]?.textContent).toContain('Adjustable height.');
    expect(getByRole(container, 'button', { name: 'Add Standing desk to cart' })).toBeTruthy();
    expect(getByRole(container, 'status').textContent).toBe('2 products');
  });

  it('asks for the products in the page currency and the first page size', async () => {
    let seen: URL | undefined;
    server.use(
      http.get('*/bff/shop/products', ({ request }) => {
        seen = new URL(request.url);
        return HttpResponse.json(page([desk]));
      }),
    );

    const { container } = mount({ pageSize: 6 });
    await findAllByRole(container, 'listitem');

    expect(seen?.searchParams.get('currency')).toBe('BRL');
    expect(seen?.searchParams.get('limit')).toBe('6');
    expect(seen?.searchParams.has('q')).toBe(false);
    expect(seen?.searchParams.has('after')).toBe(false);
  });

  it('hands the product to the cart when its button is pressed', async () => {
    server.use(http.get('*/bff/shop/products', () => HttpResponse.json(page([desk, mug]))));
    const onAdd = vi.fn();
    const { container } = mount({ onAdd });

    fireEvent.click(await findByRole(container, 'button', { name: 'Add Coffee mug to cart' }));

    expect(onAdd).toHaveBeenCalledExactlyOnceWith(mug);
  });

  it('cannot add a product that has no price in the currency', async () => {
    server.use(http.get('*/bff/shop/products', () => HttpResponse.json(page([unpriced]))));

    const { container } = mount();

    const button = await findByRole(container, 'button', { name: 'Add Old lamp to cart' });
    expect(button.hasAttribute('disabled')).toBe(true);
    expect(container.textContent).toContain('Price unavailable');
  });

  it('says so when nothing matches', async () => {
    server.use(http.get('*/bff/shop/products', () => HttpResponse.json(page([]))));

    const { container } = mount();

    await findByText(container, 'No products found.');
    expect(queryByRole(container, 'list')).toBeNull();
    expect(queryByRole(container, 'button', { name: 'Load more' })).toBeNull();
  });

  it('shows the error with a way to try again, and the retry loads the products', async () => {
    let fail = true;
    server.use(
      http.get('*/bff/shop/products', () =>
        fail
          ? HttpResponse.json({ title: 'Catalog is unavailable', status: 500 }, { status: 500 })
          : HttpResponse.json(page([desk])),
      ),
    );
    const { container } = mount();

    const alert = await findByRole(container, 'alert');
    expect(alert.textContent).toContain('Catalog is unavailable');

    fail = false;
    fireEvent.click(getByRole(alert, 'button', { name: 'Try again' }));

    expect(await findByRole(container, 'heading', { name: 'Standing desk' })).toBeTruthy();
    expect(queryByRole(container, 'alert')).toBeNull();
  });

  it('shows a network failure in plain words', async () => {
    server.use(http.get('*/bff/shop/products', () => HttpResponse.error()));

    const { container } = mount();

    expect((await findByRole(container, 'alert')).textContent).toContain(
      'The server could not be reached.',
    );
  });

  it('loads the next page with the keyset cursor and adds to the list', async () => {
    const afters: (string | null)[] = [];
    server.use(
      http.get('*/bff/shop/products', ({ request }) => {
        const after = new URL(request.url).searchParams.get('after');
        afters.push(after);
        return HttpResponse.json(after === null ? page([desk], 'cursor-1') : page([mug]));
      }),
    );
    const { container } = mount();

    fireEvent.click(await findByRole(container, 'button', { name: 'Load more' }));

    await vi.waitFor(() => {
      expect(getByRole(container, 'status').textContent).toBe('2 products');
    });
    expect(afters).toEqual([null, 'cursor-1']);
    expect(getByRole(container, 'heading', { name: 'Standing desk' })).toBeTruthy();
    expect(getByRole(container, 'heading', { name: 'Coffee mug' })).toBeTruthy();
    expect(queryByRole(container, 'button', { name: 'Load more' })).toBeNull();
  });

  it('keeps the products when loading more fails and offers the retry', async () => {
    let fail = true;
    server.use(
      http.get('*/bff/shop/products', ({ request }) => {
        const after = new URL(request.url).searchParams.get('after');
        if (after === null) {
          return HttpResponse.json(page([desk], 'cursor-1'));
        }
        return fail
          ? HttpResponse.json({ title: 'Down' }, { status: 500 })
          : HttpResponse.json(page([mug]));
      }),
    );
    const { container } = mount();

    fireEvent.click(await findByRole(container, 'button', { name: 'Load more' }));
    const alert = await findByRole(container, 'alert');

    expect(getByRole(container, 'heading', { name: 'Standing desk' })).toBeTruthy();
    fail = false;
    fireEvent.click(getByRole(alert, 'button', { name: 'Try again' }));
    expect(await findByRole(container, 'heading', { name: 'Coffee mug' })).toBeTruthy();
  });

  it('searches by text and starts again from the first page', async () => {
    const seen: (URL | undefined)[] = [];
    server.use(
      http.get('*/bff/shop/products', ({ request }) => {
        const url = new URL(request.url);
        seen.push(url);
        return HttpResponse.json(
          url.searchParams.get('q') === 'mug' ? page([mug]) : page([desk], 'cursor-1'),
        );
      }),
    );
    const { container } = mount();
    await findByRole(container, 'heading', { name: 'Standing desk' });

    const search = getByRole(container, 'searchbox', { name: 'Search products' });
    fireEvent.input(search, { target: { value: 'mug' } });
    fireEvent.submit(getByRole(container, 'search'));

    expect(await findByRole(container, 'heading', { name: 'Coffee mug' })).toBeTruthy();
    expect(queryByRole(container, 'heading', { name: 'Standing desk' })).toBeNull();
    expect(seen.at(-1)?.searchParams.get('q')).toBe('mug');
    expect(seen.at(-1)?.searchParams.has('after')).toBe(false);
  });

  it('filters by a category seen in the products, and can go back to all of them', async () => {
    const categories: (string | null)[] = [];
    server.use(
      http.get('*/bff/shop/products', ({ request }) => {
        const category = new URL(request.url).searchParams.get('category');
        categories.push(category);
        return HttpResponse.json(
          category === 'Kitchen' ? page([mug]) : page([desk, mug, unpriced]),
        );
      }),
    );
    const { container } = mount();
    await findAllByRole(container, 'listitem');

    const select = getByRole<HTMLSelectElement>(container, 'combobox', { name: 'Category' });
    expect([...select.options].map((option) => option.text)).toEqual([
      'All categories',
      'Kitchen',
      'Office',
    ]);

    fireEvent.change(select, { target: { value: 'Kitchen' } });
    await vi.waitFor(() => {
      expect(getByRole(container, 'status').textContent).toBe('1 product');
    });
    // The categories already seen stay in the list, so the filter can be changed back.
    expect(select.options).toHaveLength(3);

    fireEvent.change(select, { target: { value: '' } });
    await vi.waitFor(() => {
      expect(getByRole(container, 'status').textContent).toBe('3 products');
    });
    expect(categories).toEqual([null, 'Kitchen', null]);
  });

  it('shows the newest answer when searches overlap', async () => {
    server.use(
      http.get('*/bff/shop/products', async ({ request }) => {
        const q = new URL(request.url).searchParams.get('q');
        if (q === 'slow') {
          await new Promise((resolve) => setTimeout(resolve, 60));
          return HttpResponse.json(page([desk]));
        }
        return HttpResponse.json(page([mug]));
      }),
    );
    const { container } = mount();
    await findByRole(container, 'heading', { name: 'Coffee mug' });
    const search = getByRole(container, 'searchbox', { name: 'Search products' });
    const form = getByRole(container, 'search');

    fireEvent.input(search, { target: { value: 'slow' } });
    fireEvent.submit(form);
    fireEvent.input(search, { target: { value: 'fast' } });
    fireEvent.submit(form);
    await new Promise((resolve) => setTimeout(resolve, 120));

    expect(queryByRole(container, 'heading', { name: 'Standing desk' })).toBeNull();
    expect(getByRole(container, 'heading', { name: 'Coffee mug' })).toBeTruthy();
  });

  it('shows that it is loading', () => {
    server.use(http.get('*/bff/shop/products', () => new Promise<never>(() => undefined)));

    const { container } = mount();

    expect(getByRole(container, 'status').textContent).toBe('Loading products…');
  });
});

describe('catalog view accessibility', () => {
  it('has no violations with products, an empty result or an error', async () => {
    server.use(
      http.get('*/bff/shop/products', () => HttpResponse.json(page([desk, mug, unpriced], 'next'))),
    );
    const withProducts = mount();
    await findAllByRole(withProducts.container, 'listitem');
    await expectAccessible(withProducts.container);
    withProducts.container.remove();

    server.use(http.get('*/bff/shop/products', () => HttpResponse.json(page([]))));
    const empty = mount();
    await vi.waitFor(() => {
      expect(empty.container.textContent).toContain('No products found.');
    });
    await expectAccessible(empty.container);
    empty.container.remove();

    server.use(http.get('*/bff/shop/products', () => HttpResponse.json({}, { status: 500 })));
    const failed = mount();
    await findByRole(failed.container, 'alert');
    await expectAccessible(failed.container);
  });
});
