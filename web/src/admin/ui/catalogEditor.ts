import {
  catalogCurrencies,
  getProduct,
  ProductChangedWhileReadingError,
  saveProduct,
  type ProductDetail,
  type ProductPrice,
} from '../api/products';
import { PreconditionFailedError } from '../../shared/api/errors';
import type { HttpClient } from '../../shared/api/http';
import { formatMoney } from '../../shared/domain/money';
import { h } from '../../shared/ui/dom';
import { errorMessage } from '../../shared/ui/errors';
import { actionButton, errorSummary, labelled, numberInput, textInput } from '../../shared/ui/form';
import { listProducts, type ShopProduct } from '../../shop/api/catalog';

export interface CatalogEditorOptions {
  readonly http: HttpClient;
  readonly pageSize?: number;
}

const defaultPageSize = 10;
const searchCurrency = 'BRL';

/** The catalog editor: search for a product, open it with its version, change its name, category and prices, and save. */
export function mountCatalogEditor(container: HTMLElement, options: CatalogEditorOptions): void {
  const pageSize = options.pageSize ?? defaultPageSize;
  let products: readonly ShopProduct[] = [];
  let next: string | null = null;
  let loading = false;
  let failure: string | undefined;
  let generation = 0;

  const search = h('input', {
    className: 'form-control',
    attrs: { id: 'editor-search', type: 'search' },
  });
  const status = h('p', {
    className: 'text-body-secondary',
    attrs: { role: 'status', 'aria-label': 'Search result' },
  });
  const alertBox = h('div', {
    className: 'alert alert-danger d-flex align-items-center gap-3',
    attrs: { role: 'alert' },
  });
  const list = h('ul', { className: 'list-group mb-2' });
  const more = actionButton('Load more', 'btn btn-outline-primary btn-sm', () => void load(true));
  const editor = h('div');
  const form = h(
    'form',
    { className: 'row g-2 align-items-end mb-3', attrs: { role: 'search' } },
    h(
      'div',
      { className: 'col-12 col-md' },
      h('label', {
        className: 'form-label',
        text: 'Search products',
        attrs: { for: 'editor-search' },
      }),
      search,
    ),
    h(
      'div',
      { className: 'col-12 col-md-auto' },
      actionButton('Search', 'btn btn-primary', () => undefined, 'submit'),
    ),
  );

  container.append(
    h(
      'section',
      { attrs: { 'aria-labelledby': 'catalog-editor-heading' } },
      h('h2', { className: 'h4', text: 'Catalog', attrs: { id: 'catalog-editor-heading' } }),
      form,
      status,
      alertBox,
      list,
      more,
      editor,
    ),
  );

  function render(): void {
    list.replaceChildren(...products.map(item));
    list.hidden = products.length === 0;
    more.hidden = next === null || loading;
    status.textContent = loading
      ? 'Searching…'
      : failure !== undefined
        ? ''
        : products.length === 0
          ? 'No products found.'
          : `${String(products.length)} ${products.length === 1 ? 'product' : 'products'}`;
    alertBox.hidden = failure === undefined;
    alertBox.replaceChildren(
      ...(failure === undefined
        ? []
        : [
            h('span', { text: failure }),
            actionButton(
              'Try again',
              'btn btn-sm btn-outline-danger ms-auto',
              () => void load(false),
            ),
          ]),
    );
  }

  function item(product: ShopProduct): HTMLElement {
    const edit = actionButton(
      'Edit',
      'btn btn-outline-primary btn-sm',
      () => void open(product.sku),
    );
    edit.setAttribute('aria-label', `Edit ${product.name}`);

    return h(
      'li',
      {
        className:
          'list-group-item d-flex flex-wrap justify-content-between align-items-center gap-2',
      },
      h('span', {}, h('strong', { text: product.name }), ` · ${product.sku} · ${product.category}`),
      edit,
    );
  }

  async function load(append: boolean): Promise<void> {
    const mine = ++generation;
    loading = true;
    failure = undefined;
    if (!append) {
      products = [];
      next = null;
    }
    render();

    try {
      const page = await listProducts(options.http, {
        currency: searchCurrency,
        limit: pageSize,
        ...(search.value === '' ? {} : { q: search.value }),
        ...(append && next !== null ? { after: next } : {}),
      });
      if (mine !== generation) {
        return;
      }
      products = [...products, ...page.items];
      next = page.next;
    } catch (error) {
      if (mine !== generation) {
        return;
      }
      failure = errorMessage(error, 'The products could not be loaded.');
    }
    loading = false;
    render();
  }

  async function open(sku: string): Promise<void> {
    editor.replaceChildren(h('p', { text: 'Loading product…', attrs: { role: 'status' } }));

    try {
      const product = await getProduct(options.http, sku);
      editor.replaceChildren(
        buildForm(options.http, product, () => {
          editor.replaceChildren();
        }),
      );
      editor.querySelector<HTMLElement>('input')?.focus();
    } catch (error) {
      editor.replaceChildren(
        h('div', {
          className: 'alert alert-danger',
          text: readFailure(error, 'The product could not be loaded.'),
          attrs: { role: 'alert' },
        }),
      );
    }
  }

  form.addEventListener('submit', (event) => {
    event.preventDefault();
    void load(false);
  });

  render();
  void load(false);
}

function buildForm(http: HttpClient, loaded: ProductDetail, close: () => void): HTMLElement {
  let product = loaded;
  let saving = false;
  const summary = errorSummary();
  const heading = h('h3', { className: 'h5' });
  const result = h('p', {
    className: 'fw-semibold',
    attrs: { role: 'status', 'aria-label': 'Save result' },
  });
  const name = textInput('');
  const category = textInput('');
  const prices = new Map(
    catalogCurrencies.map((currency) => [currency, numberInput(undefined, { min: '0' })]),
  );

  function show(shown: ProductDetail): void {
    product = shown;
    heading.textContent = `${shown.name} (${shown.sku}), version ${String(shown.version)}`;
    name.value = shown.name;
    category.value = shown.category;
    for (const [currency, input] of prices) {
      const price = shown.prices.find((candidate) => candidate.currency === currency);
      input.value = price === undefined ? '' : String(price.amount);
    }
  }

  /** The prices typed in, a currency left empty meaning none; a problem is named in `problems`. */
  function read(): { name: string; category: string; prices: ProductPrice[]; problems: string[] } {
    const problems: string[] = [];
    const typed: ProductPrice[] = [];

    if (name.value.trim() === '') {
      problems.push('Name is required.');
    }
    if (category.value.trim() === '') {
      problems.push('Category is required.');
    }
    for (const [currency, input] of prices) {
      if (input.value.trim() === '') {
        continue;
      }
      if (!(input.valueAsNumber >= 0)) {
        problems.push(`The price in ${currency} must not be negative.`);
      } else {
        typed.push({ currency, amount: input.valueAsNumber });
      }
    }

    return { name: name.value.trim(), category: category.value.trim(), prices: typed, problems };
  }

  async function save(): Promise<void> {
    const change = read();

    if (change.problems.length > 0) {
      summary.show(change.problems);
      return;
    }
    if (saving) {
      return;
    }
    summary.clear();
    result.textContent = '';
    saving = true;
    try {
      const stored = await saveProduct(http, product, change);
      show(stored);
      const shown = stored.prices
        .map((price) => formatMoney(price.amount, price.currency))
        .join(', ');
      result.textContent = `Saved ${stored.name} (${stored.sku}) as version ${String(stored.version)}: ${shown === '' ? 'no prices' : shown}.`;
    } catch (error) {
      if (error instanceof PreconditionFailedError) {
        showStale();
      } else {
        summary.show([errorMessage(error, 'The product could not be saved.')], 'Not saved:');
      }
    }
    saving = false;
  }

  function showStale(): void {
    summary.show(
      ['This product was changed by someone else. Reload it to see its current state.'],
      'Not saved:',
    );
    const reload = actionButton(
      'Reload',
      'btn btn-sm btn-outline-danger',
      () => void reloadProduct(),
    );
    summary.element.append(reload);
  }

  async function reloadProduct(): Promise<void> {
    try {
      show(await getProduct(http, product.sku));
      summary.clear();
      result.textContent = '';
    } catch (error) {
      summary.show([readFailure(error, 'The product could not be reloaded.')], 'Not reloaded:');
    }
  }

  show(loaded);
  const form = h(
    'form',
    { className: 'border rounded p-3', attrs: { 'aria-label': 'Product editor', novalidate: '' } },
    heading,
    summary.element,
    h(
      'div',
      { className: 'row' },
      h('div', { className: 'col-md-6' }, labelled('Name', name)),
      h('div', { className: 'col-md-6' }, labelled('Category', category)),
    ),
    h(
      'div',
      { className: 'row' },
      ...[...prices].map(([currency, input]) =>
        h(
          'div',
          { className: 'col-md-3' },
          labelled(`Price in ${currency}`, input, 'Leave empty for no price in this currency.'),
        ),
      ),
    ),
    result,
    h(
      'div',
      { className: 'd-flex gap-2' },
      actionButton('Save product', 'btn btn-primary', () => void save(), 'submit'),
      actionButton('Close', 'btn btn-outline-secondary', close),
    ),
  );
  form.addEventListener('submit', (event) => {
    event.preventDefault();
    void save();
  });

  return form;
}

function readFailure(error: unknown, fallback: string): string {
  return error instanceof ProductChangedWhileReadingError
    ? error.message
    : errorMessage(error, fallback);
}
