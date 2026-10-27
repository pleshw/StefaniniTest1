import type { HttpClient } from '../../shared/api/http';
import { formatMoney } from '../../shared/domain/money';
import { h } from '../../shared/ui/dom';
import { errorMessage } from '../../shared/ui/errors';
import { listProducts, type ShopProduct } from '../api/catalog';

export interface CatalogOptions {
  readonly http: HttpClient;
  /** The currency the page asks prices in. */
  readonly currency: string;
  readonly pageSize?: number;
  readonly onAdd: (product: ShopProduct) => void;
}

const defaultPageSize = 12;

/** The product catalog: search, category filter, cards with prices and keyset "load more". */
export function mountCatalog(container: HTMLElement, options: CatalogOptions): void {
  const pageSize = options.pageSize ?? defaultPageSize;
  const categories = new Set<string>();
  let products: readonly ShopProduct[] = [];
  let next: string | null = null;
  let loading = false;
  let failure: string | undefined;
  let loadingMore = false;
  let request = new AbortController();

  const search = h('input', {
    className: 'form-control',
    attrs: { id: 'catalog-search', type: 'search', name: 'q' },
  });
  const category = h('select', {
    className: 'form-select',
    attrs: { id: 'catalog-category', name: 'category' },
  });
  const form = h(
    'form',
    { className: 'row g-2 align-items-end mb-3', attrs: { role: 'search' } },
    h(
      'div',
      { className: 'col-12 col-md' },
      h('label', {
        className: 'form-label',
        text: 'Buscar produtos',
        attrs: { for: 'catalog-search' },
      }),
      search,
    ),
    h(
      'div',
      { className: 'col-12 col-md-4' },
      h('label', {
        className: 'form-label',
        text: 'Categoria',
        attrs: { for: 'catalog-category' },
      }),
      category,
    ),
    h(
      'div',
      { className: 'col-12 col-md-auto' },
      h('button', { className: 'btn btn-primary', text: 'Buscar', attrs: { type: 'submit' } }),
    ),
  );
  const status = h('p', { className: 'text-body-secondary', attrs: { role: 'status' } });
  const alertBox = h('div', {
    className: 'alert alert-danger d-flex align-items-center gap-3',
    attrs: { role: 'alert' },
  });
  alertBox.hidden = true;
  const list = h('ul', {
    className: 'row row-cols-1 row-cols-md-2 row-cols-lg-3 g-3 list-unstyled',
  });
  const more = h('button', {
    className: 'btn btn-outline-primary',
    text: 'Carregar mais',
    attrs: { type: 'button' },
  });

  container.append(
    h(
      'section',
      { attrs: { 'aria-labelledby': 'catalog-heading' } },
      h('h2', { className: 'h4', text: 'Catálogo', attrs: { id: 'catalog-heading' } }),
      form,
      status,
      alertBox,
      list,
      more,
    ),
  );

  function render(): void {
    renderCategories();
    list.replaceChildren(...products.map(card));
    list.hidden = products.length === 0;
    more.hidden = next === null || loading;
    status.textContent = statusText();
    alertBox.hidden = failure === undefined;
    alertBox.replaceChildren(
      ...(failure === undefined ? [] : [h('span', { text: failure }), retryButton()]),
    );
  }

  function statusText(): string {
    if (loading) {
      return 'Carregando produtos…';
    }
    if (failure !== undefined) {
      return '';
    }
    if (products.length === 0) {
      return 'Nenhum produto encontrado.';
    }

    return `${String(products.length)} ${products.length === 1 ? 'produto' : 'produtos'}`;
  }

  function retryButton(): HTMLButtonElement {
    const button = h('button', {
      className: 'btn btn-sm btn-outline-danger ms-auto',
      text: 'Tentar novamente',
      attrs: { type: 'button' },
    });
    button.addEventListener('click', () => void load(loadingMore));

    return button;
  }

  function renderCategories(): void {
    const selected = category.value;
    const names = [...categories].sort((a, b) => a.localeCompare(b));

    category.replaceChildren(
      h('option', { text: 'Todas as categorias', attrs: { value: '' } }),
      ...names.map((name) => h('option', { text: name, attrs: { value: name } })),
    );
    category.value = selected;
  }

  function card(product: ShopProduct): HTMLElement {
    const add = h('button', {
      className: 'btn btn-primary mt-auto',
      attrs: { type: 'button', 'aria-label': `Adicionar ${product.name} ao carrinho` },
    });
    add.append(
      h('i', { className: 'bi bi-cart-plus me-1', attrs: { 'aria-hidden': 'true' } }),
      'Adicionar ao carrinho',
    );
    add.disabled = product.price === null;
    add.addEventListener('click', () => {
      options.onAdd(product);
    });

    return h(
      'li',
      { className: 'col' },
      h(
        'div',
        { className: 'card h-100' },
        h(
          'div',
          { className: 'card-body d-flex flex-column gap-2' },
          h('h3', { className: 'h5 card-title mb-0', text: product.name }),
          h('span', {
            className: 'badge text-bg-secondary align-self-start',
            text: product.category,
          }),
          h('p', { className: 'card-text', text: product.description }),
          h('p', {
            className: 'fs-5 fw-semibold mb-0',
            text:
              product.price === null
                ? 'Preço indisponível'
                : formatMoney(product.price.amount, product.price.currency),
          }),
          add,
        ),
      ),
    );
  }

  async function load(append: boolean): Promise<void> {
    request.abort();
    request = new AbortController();
    const { signal } = request;
    loading = true;
    loadingMore = append;
    failure = undefined;
    if (!append) {
      products = [];
      next = null;
    }
    render();

    try {
      const page = await listProducts(
        options.http,
        {
          currency: options.currency,
          limit: pageSize,
          ...(search.value === '' ? {} : { q: search.value }),
          ...(category.value === '' ? {} : { category: category.value }),
          ...(append && next !== null ? { after: next } : {}),
        },
        signal,
      );

      products = [...products, ...page.items];
      next = page.next;
      for (const product of page.items) {
        categories.add(product.category);
      }
    } catch (error) {
      if (signal.aborted) {
        return;
      }
      failure = errorMessage(error, 'Não foi possível carregar os produtos.');
    }

    loading = false;
    render();
  }

  form.addEventListener('submit', (event) => {
    event.preventDefault();
    void load(false);
  });
  category.addEventListener('change', () => void load(false));
  more.addEventListener('click', () => void load(true));

  void load(false);
}
