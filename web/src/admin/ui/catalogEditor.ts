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
import { withIcon } from '../../shared/ui/icons';
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
    attrs: { role: 'status', 'aria-label': 'Resultado da busca' },
  });
  const alertBox = h('div', {
    className: 'alert alert-danger d-flex align-items-center gap-3',
    attrs: { role: 'alert' },
  });
  const list = h('ul', { className: 'list-group mb-2' });
  const more = actionButton(
    'Carregar mais',
    'btn btn-outline-primary btn-sm',
    () => void load(true),
  );
  const editor = h('div');
  const form = h(
    'form',
    { className: 'row g-2 align-items-end mb-3', attrs: { role: 'search' } },
    h(
      'div',
      { className: 'col-12 col-md' },
      h('label', {
        className: 'form-label',
        text: 'Buscar produtos',
        attrs: { for: 'editor-search' },
      }),
      search,
    ),
    h(
      'div',
      { className: 'col-12 col-md-auto' },
      actionButton('Buscar', 'btn btn-primary', () => undefined, 'submit'),
    ),
  );

  container.append(
    h(
      'section',
      { attrs: { 'aria-labelledby': 'catalog-editor-heading' } },
      withIcon(
        h('h2', { className: 'h4', text: 'Catálogo', attrs: { id: 'catalog-editor-heading' } }),
        'bi-box-seam',
      ),
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
      ? 'Buscando…'
      : failure !== undefined
        ? ''
        : products.length === 0
          ? 'Nenhum produto encontrado.'
          : `${String(products.length)} ${products.length === 1 ? 'produto' : 'produtos'}`;
    alertBox.hidden = failure === undefined;
    alertBox.replaceChildren(
      ...(failure === undefined
        ? []
        : [
            h('span', { text: failure }),
            actionButton(
              'Tentar novamente',
              'btn btn-sm btn-outline-danger ms-auto',
              () => void load(false),
            ),
          ]),
    );
  }

  function item(product: ShopProduct): HTMLElement {
    const edit = actionButton(
      'Editar',
      'btn btn-outline-primary btn-sm',
      () => void open(product.sku),
    );
    edit.setAttribute('aria-label', `Editar ${product.name}`);

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
      failure = errorMessage(error, 'Não foi possível carregar os produtos.');
    }
    loading = false;
    render();
  }

  async function open(sku: string): Promise<void> {
    editor.replaceChildren(h('p', { text: 'Carregando produto…', attrs: { role: 'status' } }));

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
          text: readFailure(error, 'Não foi possível carregar o produto.'),
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
    attrs: { role: 'status', 'aria-label': 'Resultado do salvamento' },
  });
  const name = textInput('');
  const category = textInput('');
  const prices = new Map(
    catalogCurrencies.map((currency) => [currency, numberInput(undefined, { min: '0' })]),
  );

  function show(shown: ProductDetail): void {
    product = shown;
    heading.textContent = `${shown.name} (${shown.sku}), versão ${String(shown.version)}`;
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
      problems.push('O nome é obrigatório.');
    }
    if (category.value.trim() === '') {
      problems.push('A categoria é obrigatória.');
    }
    for (const [currency, input] of prices) {
      if (input.value.trim() === '') {
        continue;
      }
      if (!(input.valueAsNumber >= 0)) {
        problems.push(`O preço em ${currency} não pode ser negativo.`);
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
      result.textContent = `${stored.name} (${stored.sku}) salvo como versão ${String(stored.version)}: ${shown === '' ? 'sem preços' : shown}.`;
    } catch (error) {
      if (error instanceof PreconditionFailedError) {
        showStale();
      } else {
        summary.show([errorMessage(error, 'Não foi possível salvar o produto.')], 'Não salvo:');
      }
    }
    saving = false;
  }

  function showStale(): void {
    summary.show(
      ['Este produto foi alterado por outra pessoa. Recarregue-o para ver o estado atual.'],
      'Não salvo:',
    );
    const reload = actionButton(
      'Recarregar',
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
      summary.show(
        [readFailure(error, 'Não foi possível recarregar o produto.')],
        'Não recarregado:',
      );
    }
  }

  show(loaded);
  const form = h(
    'form',
    {
      className: 'border rounded p-3',
      attrs: { 'aria-label': 'Editor de produto', novalidate: '' },
    },
    heading,
    summary.element,
    h(
      'div',
      { className: 'row' },
      h('div', { className: 'col-md-6' }, labelled('Nome', name)),
      h('div', { className: 'col-md-6' }, labelled('Categoria', category)),
    ),
    h(
      'div',
      { className: 'row' },
      ...[...prices].map(([currency, input]) =>
        h(
          'div',
          { className: 'col-md-3' },
          labelled(`Preço em ${currency}`, input, 'Deixe vazio para não ter preço nesta moeda.'),
        ),
      ),
    ),
    result,
    h(
      'div',
      { className: 'd-flex gap-2' },
      actionButton('Salvar produto', 'btn btn-primary', () => void save(), 'submit'),
      actionButton('Fechar', 'btn btn-outline-secondary', close),
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
