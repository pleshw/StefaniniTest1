import {
  findAllByRole,
  findByRole,
  findByText,
  fireEvent,
  getByLabelText,
  getByRole,
  queryByRole,
} from '@testing-library/dom';
import { http, HttpResponse } from 'msw';
import { describe, expect, it, vi } from 'vitest';
import { server } from '../../mocks/server';
import { mountCatalogEditor } from '../../src/admin/ui/catalogEditor';
import { createHttpClient } from '../../src/shared/api/http';
import { expectAccessible } from '../helpers';

interface Stored {
  sku: string;
  name: string;
  description: string;
  category: string;
  attributes: Record<string, string>;
  prices: { currency: string; amount: number }[];
  isActive: boolean;
  version: number;
}

const product = (extra: Partial<Stored> = {}): Stored => ({
  sku: 'DESK-1',
  name: 'Standing desk',
  description: 'Adjustable height.',
  category: 'Office',
  attributes: { color: 'oak' },
  prices: [
    { currency: 'BRL', amount: 1999.9 },
    { currency: 'USD', amount: 399 },
  ],
  isActive: true,
  version: 3,
  ...extra,
});

/** What Catalog's GET /products/{sku} answers: one price, in the currency asked, and the version as the ETag. */
function useProduct(stored: Stored): void {
  server.use(
    http.get('*/bff/admin/products/:sku', ({ request }) => {
      const currency = new URL(request.url).searchParams.get('currency');
      const price = stored.prices.find((candidate) => candidate.currency === currency);
      const rest = { ...stored, prices: undefined };

      return HttpResponse.json(
        { ...rest, price: price ?? null },
        { headers: { ETag: `"${String(stored.version)}"` } },
      );
    }),
  );
}

function mount() {
  const container = document.createElement('div');
  document.body.append(container);
  mountCatalogEditor(container, { http: createHttpClient({ sleep: () => Promise.resolve() }) });

  return container;
}

async function openDesk(container: HTMLElement): Promise<HTMLElement> {
  fireEvent.click(await findByRole(container, 'button', { name: 'Edit Standing desk' }));

  return findByRole(container, 'form', { name: 'Product editor' });
}

const fill = (form: HTMLElement, label: string, value: string): void => {
  fireEvent.input(getByLabelText(form, label), { target: { value } });
};
const save = (form: HTMLElement): void => {
  fireEvent.click(getByRole(form, 'button', { name: 'Save product' }));
};

describe('catalog search', () => {
  it('searches the products and lists each with its SKU, category and a way to edit it', async () => {
    const asked: URL[] = [];
    server.use(
      http.get('*/bff/shop/products', ({ request }) => {
        asked.push(new URL(request.url));
        return HttpResponse.json({
          items: [
            {
              sku: 'DESK-1',
              name: 'Standing desk',
              description: 'x',
              category: 'Office',
              price: { amount: 1, currency: 'BRL' },
            },
            { sku: 'OLD-1', name: 'Old lamp', description: 'x', category: 'Office', price: null },
          ],
          next: null,
        });
      }),
    );
    const container = mount();

    fireEvent.input(getByRole(container, 'searchbox', { name: 'Search products' }), {
      target: { value: 'desk' },
    });
    fireEvent.submit(getByRole(container, 'search'));

    const items = await findAllByRole(container, 'listitem');
    expect(items).toHaveLength(2);
    expect(items[0]?.textContent).toContain('Standing desk');
    expect(items[0]?.textContent).toContain('DESK-1');
    expect(items[0]?.textContent).toContain('Office');
    expect(asked.at(-1)?.searchParams.get('q')).toBe('desk');
    expect(getByRole(container, 'status', { name: 'Search result' }).textContent).toBe(
      '2 products',
    );
  });

  it('loads more with the keyset cursor, says when nothing matches and when the search fails', async () => {
    const afters: (string | null)[] = [];
    server.use(
      http.get('*/bff/shop/products', ({ request }) => {
        const after = new URL(request.url).searchParams.get('after');
        afters.push(after);
        return HttpResponse.json(
          after === null
            ? {
                items: [{ sku: 'A-1', name: 'First', description: '', category: 'X', price: null }],
                next: 'c1',
              }
            : {
                items: [
                  { sku: 'B-1', name: 'Second', description: '', category: 'X', price: null },
                ],
                next: null,
              },
        );
      }),
    );
    const container = mount();

    fireEvent.click(await findByRole(container, 'button', { name: 'Load more' }));
    await findByRole(container, 'button', { name: 'Edit Second' });
    expect(afters).toEqual([null, 'c1']);
    expect(queryByRole(container, 'button', { name: 'Load more' })).toBeNull();

    server.use(http.get('*/bff/shop/products', () => HttpResponse.json({ items: [], next: null })));
    fireEvent.submit(getByRole(container, 'search'));
    await findByText(container, 'No products found.');

    server.use(
      http.get('*/bff/shop/products', () =>
        HttpResponse.json({ title: 'Catalog is unavailable' }, { status: 503 }),
      ),
    );
    fireEvent.submit(getByRole(container, 'search'));
    expect((await findByRole(container, 'alert')).textContent).toContain('Catalog is unavailable');
  });

  it('lists the products as soon as it opens', async () => {
    const container = mount();

    expect(await findByRole(container, 'button', { name: 'Edit Standing desk' })).toBeTruthy();
  });
});

describe('product editor', () => {
  it('loads the product with its version and one price per currency', async () => {
    useProduct(product());
    const container = mount();

    const form = await openDesk(container);

    await vi.waitFor(() => {
      expect(getByLabelText<HTMLInputElement>(form, 'Name').value).toBe('Standing desk');
    });
    expect(getByLabelText<HTMLInputElement>(form, 'Category').value).toBe('Office');
    expect(getByLabelText<HTMLInputElement>(form, 'Price in BRL').value).toBe('1999.9');
    expect(getByLabelText<HTMLInputElement>(form, 'Price in USD').value).toBe('399');
    expect(getByRole(form, 'heading', { name: 'Standing desk (DESK-1), version 3' })).toBeTruthy();
  });

  it('shows an empty price box for a currency the product has no price in', async () => {
    useProduct(product({ prices: [{ currency: 'BRL', amount: 10 }] }));
    const container = mount();

    const form = await openDesk(container);

    await vi.waitFor(() => {
      expect(getByLabelText<HTMLInputElement>(form, 'Price in BRL').value).toBe('10');
    });
    expect(getByLabelText<HTMLInputElement>(form, 'Price in USD').value).toBe('');
  });

  it('saves with the version it loaded as If-Match, sends the unchanged fields back, and shows the product returned', async () => {
    useProduct(product());
    let request: Request | undefined;
    let body: unknown;
    server.use(
      http.put('*/bff/admin/products/:sku', async ({ request: incoming }) => {
        request = incoming.clone();
        body = await incoming.json();
        return HttpResponse.json(
          product({
            name: 'Standing desk XL',
            version: 4,
            prices: [{ currency: 'BRL', amount: 2100 }],
          }),
          { headers: { ETag: '"4"' } },
        );
      }),
    );
    const container = mount();
    const form = await openDesk(container);
    await vi.waitFor(() => {
      expect(getByLabelText<HTMLInputElement>(form, 'Name').value).toBe('Standing desk');
    });

    fill(form, 'Name', '  Standing desk XL ');
    fill(form, 'Price in BRL', '2100');
    fill(form, 'Price in USD', '');
    save(form);

    const done = await findByRole(container, 'status', { name: 'Save result' });
    await vi.waitFor(() => {
      expect(done.textContent).toBe('Saved Standing desk XL (DESK-1) as version 4: R$2,100.00.');
    });
    expect(new URL(request?.url ?? '').pathname).toBe('/bff/admin/products/DESK-1');
    expect(request?.headers.get('if-match')).toBe('"3"');
    expect(request?.headers.get('x-csrf')).toBe('1');
    expect(body).toEqual({
      name: 'Standing desk XL',
      description: 'Adjustable height.',
      category: 'Office',
      attributes: { color: 'oak' },
      prices: [{ currency: 'BRL', amount: 2100 }],
    });
    expect(
      getByRole(form, 'heading', { name: 'Standing desk XL (DESK-1), version 4' }),
    ).toBeTruthy();
    expect(getByLabelText<HTMLInputElement>(form, 'Price in USD').value).toBe('');
  });

  it('the next save uses the new version', async () => {
    useProduct(product());
    const matches: (string | null)[] = [];
    server.use(
      http.put('*/bff/admin/products/:sku', ({ request }) => {
        matches.push(request.headers.get('if-match'));
        return HttpResponse.json(product({ version: 3 + matches.length }));
      }),
    );
    const container = mount();
    const form = await openDesk(container);
    await vi.waitFor(() => {
      expect(getByLabelText<HTMLInputElement>(form, 'Name').value).toBe('Standing desk');
    });

    save(form);
    await vi.waitFor(() => {
      expect(matches).toHaveLength(1);
    });
    await findByRole(form, 'heading', { name: 'Standing desk (DESK-1), version 4' });
    save(form);
    await vi.waitFor(() => {
      expect(matches).toHaveLength(2);
    });

    expect(matches).toEqual(['"3"', '"4"']);
  });

  it('on a 412 says the product was changed by someone else and reloads it on request', async () => {
    useProduct(product());
    server.use(
      http.put('*/bff/admin/products/:sku', () =>
        HttpResponse.json(
          { title: 'Precondition failed', detail: 'The product is at version 5.' },
          { status: 412 },
        ),
      ),
    );
    const container = mount();
    const form = await openDesk(container);
    await vi.waitFor(() => {
      expect(getByLabelText<HTMLInputElement>(form, 'Name').value).toBe('Standing desk');
    });
    fill(form, 'Name', 'My edit');

    save(form);

    const alert = await findByRole(form, 'alert');
    expect(alert.textContent).toContain('This product was changed by someone else.');
    useProduct(product({ name: 'Their edit', version: 5 }));
    fireEvent.click(getByRole(alert, 'button', { name: 'Reload' }));

    await vi.waitFor(() => {
      expect(getByLabelText<HTMLInputElement>(form, 'Name').value).toBe('Their edit');
    });
    expect(getByRole(form, 'heading', { name: 'Their edit (DESK-1), version 5' })).toBeTruthy();
    expect(queryByRole(form, 'alert')).toBeNull();
  });

  it('does not send a product without a name or a category, or with a price that is not valid', async () => {
    useProduct(product());
    let calls = 0;
    server.use(
      http.put('*/bff/admin/products/:sku', () => {
        calls++;
        return HttpResponse.json(product());
      }),
    );
    const container = mount();
    const form = await openDesk(container);
    await vi.waitFor(() => {
      expect(getByLabelText<HTMLInputElement>(form, 'Name').value).toBe('Standing desk');
    });

    fill(form, 'Name', ' ');
    fill(form, 'Category', '');
    fill(form, 'Price in BRL', '-1');
    save(form);

    const summary = getByRole(form, 'alert');
    expect([...summary.querySelectorAll('li')].map((item) => item.textContent)).toEqual([
      'Name is required.',
      'Category is required.',
      'The price in BRL must not be negative.',
    ]);
    expect(document.activeElement).toBe(summary);
    expect(calls).toBe(0);
  });

  it("shows Catalog's own messages when it refuses the product", async () => {
    useProduct(product());
    server.use(
      http.put('*/bff/admin/products/:sku', () =>
        HttpResponse.json(
          { title: 'Invalid request', detail: 'Currency JPY is not supported.' },
          { status: 400 },
        ),
      ),
    );
    const container = mount();
    const form = await openDesk(container);
    await vi.waitFor(() => {
      expect(getByLabelText<HTMLInputElement>(form, 'Name').value).toBe('Standing desk');
    });

    save(form);

    expect((await findByRole(form, 'alert')).textContent).toContain(
      'Currency JPY is not supported.',
    );
  });

  it('reads again when the product changed between the two price reads, and gives up if it keeps changing', async () => {
    let reads = 0;
    server.use(
      http.get('*/bff/admin/products/:sku', ({ request }) => {
        reads++;
        const currency = new URL(request.url).searchParams.get('currency');
        const version = reads <= 2 && currency === 'USD' ? 4 : 3;
        return HttpResponse.json(
          { ...product(), prices: undefined, price: { currency, amount: 1 }, version },
          { headers: { ETag: `"${String(version)}"` } },
        );
      }),
    );
    const container = mount();
    const form = await openDesk(container);
    await vi.waitFor(() => {
      expect(getByLabelText<HTMLInputElement>(form, 'Name').value).toBe('Standing desk');
    });
    expect(reads).toBe(4);

    server.use(
      http.get('*/bff/admin/products/:sku', ({ request }) => {
        const currency = new URL(request.url).searchParams.get('currency');
        return HttpResponse.json({
          ...product(),
          prices: undefined,
          price: null,
          version: currency === 'USD' ? 10 : 3,
        });
      }),
    );
    fireEvent.click(getByRole(container, 'button', { name: 'Edit Standing desk' }));
    await vi.waitFor(() => {
      expect(getByRole(container, 'alert').textContent).toContain(
        'The product changed while it was being read. Try again.',
      );
    });
  });

  it('shows the error when the product cannot be read', async () => {
    server.use(
      http.get('*/bff/admin/products/:sku', () =>
        HttpResponse.json(
          { title: 'Not found', detail: 'Product DESK-1 was not found.' },
          { status: 404 },
        ),
      ),
    );
    const container = mount();

    fireEvent.click(await findByRole(container, 'button', { name: 'Edit Standing desk' }));

    expect((await findByRole(container, 'alert')).textContent).toContain(
      'Product DESK-1 was not found.',
    );
    expect(queryByRole(container, 'form', { name: 'Product editor' })).toBeNull();
  });

  it('closes the editor on Close', async () => {
    useProduct(product());
    const container = mount();
    const form = await openDesk(container);

    fireEvent.click(getByRole(form, 'button', { name: 'Close' }));

    expect(queryByRole(container, 'form', { name: 'Product editor' })).toBeNull();
  });
});

describe('catalog editor accessibility', () => {
  it('has no violations: the search, the editor, its errors and the result', async () => {
    useProduct(product());
    server.use(
      http.put('*/bff/admin/products/:sku', () => HttpResponse.json(product({ version: 4 }))),
    );
    const container = mount();
    await findByRole(container, 'button', { name: 'Edit Standing desk' });
    await expectAccessible(container);

    const form = await openDesk(container);
    await vi.waitFor(() => {
      expect(getByLabelText<HTMLInputElement>(form, 'Name').value).toBe('Standing desk');
    });
    await expectAccessible(form);
    fill(form, 'Name', '');
    save(form);
    await expectAccessible(form);
    fill(form, 'Name', 'Desk');
    save(form);
    await findByRole(container, 'status', { name: 'Save result' });
    await vi.waitFor(() => {
      expect(getByRole(container, 'status', { name: 'Save result' }).textContent).toContain(
        'Saved',
      );
    });
    await expectAccessible(container);
  });
});
