import { ApiError, NetworkError } from '../../shared/api/errors';
import type { HttpClient } from '../../shared/api/http';
import type { OrderRequest, Quote } from '../../shared/api/orders';
import { formatMoney } from '../../shared/domain/money';
import type { IdempotencyKeys } from '../../shared/domain/idempotency';
import { h } from '../../shared/ui/dom';
import { placeOrder, quoteCart } from '../api/orders';
import { maxQuantity, type Cart, type CartLine } from '../domain/cart';

export interface CartViewOptions {
  readonly http: HttpClient;
  readonly cart: Cart;
  readonly keys: IdempotencyKeys;
  readonly currency: string;
  /** How long the cart must stay unchanged before the quote is asked for again. */
  readonly debounceMilliseconds?: number;
}

const defaultDebounce = 300;
const keyScope = 'shop';

interface Row {
  readonly item: HTMLLIElement;
  readonly price: HTMLElement;
  readonly quantity: HTMLInputElement;
}

/** The cart: its lines, the quote Orders gives for them and the checkout that places the order. */
export function mountCart(container: HTMLElement, options: CartViewOptions): void {
  const { cart, currency } = options;
  const debounce = options.debounceMilliseconds ?? defaultDebounce;
  const rows = new Map<string, Row>();
  let quote: Quote | undefined;
  let quoteFailed = false;
  let quoteStale = false;
  let quoteTimer: ReturnType<typeof setTimeout> | undefined;
  let quoteRequest = new AbortController();
  let submitting = false;
  let orderErrors: readonly string[] = [];
  let placedNumber: string | undefined;

  const heading = h('h2', {
    className: 'h4',
    text: 'Cart',
    attrs: { id: 'cart-heading', tabindex: '-1' },
  });
  const empty = h('p', { text: 'Your cart is empty.' });
  const list = h('ul', { className: 'list-group mb-3' });
  const totals = h('dl', { className: 'row mb-0' });
  const totalsGroup = h(
    'div',
    { className: 'mb-3', attrs: { role: 'group', 'aria-label': 'Totals', 'aria-live': 'polite' } },
    totals,
  );
  const quoteAlert = h('div', { className: 'alert alert-warning', attrs: { role: 'alert' } });
  const orderAlert = h('div', {
    className: 'alert alert-danger',
    attrs: { role: 'alert', 'aria-label': 'Order error' },
  });
  const submit = h('button', { className: 'btn btn-success w-100', attrs: { type: 'button' } });
  const result = h('p', {
    className: 'mt-3 mb-0',
    attrs: { role: 'status', 'aria-label': 'Order result' },
  });

  container.append(
    h(
      'section',
      { attrs: { 'aria-labelledby': 'cart-heading' } },
      heading,
      empty,
      list,
      totalsGroup,
      quoteAlert,
      orderAlert,
      submit,
      result,
    ),
  );

  function render(): void {
    const lines = cart.lines();

    renderLines(lines);
    empty.hidden = lines.length > 0;
    list.hidden = lines.length === 0;
    renderTotals(lines);
    quoteAlert.hidden = !quoteFailed || lines.length === 0;
    quoteAlert.textContent = 'The prices could not be updated.';
    renderOrderErrors();
    submit.disabled = submitting || lines.length === 0;
    submit.replaceChildren(
      h('i', {
        className: `bi ${submitting ? 'bi-hourglass-split' : 'bi-bag-check'} me-1`,
        attrs: { 'aria-hidden': 'true' },
      }),
      submitting ? 'Placing order…' : 'Place order',
    );
    result.textContent = placedNumber === undefined ? '' : `Order ${placedNumber} was placed.`;
  }

  function renderLines(lines: readonly CartLine[]): void {
    // Rows are kept between renders, so a person changing a quantity with the keyboard keeps the focus.
    for (const [sku, row] of rows) {
      if (!lines.some((line) => line.sku === sku)) {
        row.item.remove();
        rows.delete(sku);
      }
    }
    for (const line of lines) {
      const row = rows.get(line.sku) ?? createRow(line);
      rows.set(line.sku, row);
      list.append(row.item);
      if (Number(row.quantity.value) !== line.quantity) {
        row.quantity.value = String(line.quantity);
      }
      row.price.textContent = priceText(line);
    }
  }

  function createRow(line: CartLine): Row {
    const quantityId = `cart-quantity-${line.sku}`;
    const quantity = h('input', {
      className: 'form-control form-control-sm',
      attrs: { id: quantityId, type: 'number', min: '1', max: String(maxQuantity), step: '1' },
    });
    quantity.addEventListener('change', () => {
      cart.setQuantity(line.sku, quantity.valueAsNumber);
      // A value the cart refused (empty, or the same) goes back to what the cart holds.
      quantity.value = String(
        cart.lines().find((candidate) => candidate.sku === line.sku)?.quantity ?? line.quantity,
      );
    });
    const remove = h('button', {
      className: 'btn btn-sm btn-outline-danger',
      attrs: { type: 'button', 'aria-label': `Remove ${line.name}` },
    });
    remove.append(h('i', { className: 'bi bi-trash', attrs: { 'aria-hidden': 'true' } }));
    remove.addEventListener('click', () => {
      cart.remove(line.sku);
      heading.focus();
    });
    const price = h('small', { className: 'text-body-secondary d-block' });
    const item = h(
      'li',
      { className: 'list-group-item d-flex flex-wrap align-items-center gap-2' },
      h('div', { className: 'flex-grow-1' }, h('strong', { text: line.name }), price),
      h('label', {
        className: 'visually-hidden',
        text: `Quantity of ${line.name}`,
        attrs: { for: quantityId },
      }),
      h('div', { className: 'cart-quantity' }, quantity),
      remove,
    );
    quantity.style.width = '5rem';

    return { item, price, quantity };
  }

  function priceText(line: CartLine): string {
    const priced = quote?.items.find((item) => item.sku === line.sku);

    if (priced === undefined) {
      return '';
    }
    const total = formatMoney(priced.lineTotal, quote?.currency ?? currency);

    return priced.lineDiscount > 0
      ? `${total} · Discount -${formatMoney(priced.lineDiscount, quote?.currency ?? currency)}`
      : total;
  }

  function renderTotals(lines: readonly CartLine[]): void {
    totalsGroup.hidden = lines.length === 0;
    totalsGroup.setAttribute('aria-busy', String(quoteStale));
    if (quote === undefined || lines.length === 0) {
      totals.replaceChildren(
        ...(lines.length === 0 || quoteFailed
          ? []
          : [h('dd', { className: 'col-12 mb-0', text: 'Calculating prices…' })]),
      );
      return;
    }
    const money = (amount: number): string => formatMoney(amount, quote?.currency ?? currency);
    const items = quote.itemCount === 1 ? '1 item' : `${String(quote.itemCount)} items`;

    totals.replaceChildren(
      h('dt', { className: 'col-6', text: 'Subtotal' }),
      h('dd', { className: 'col-6 text-end', text: money(quote.subtotal) }),
      h('dt', { className: 'col-6', text: 'Discounts' }),
      h('dd', {
        className: 'col-6 text-end',
        text: quote.discountTotal > 0 ? `-${money(quote.discountTotal)}` : money(0),
      }),
      h('dt', { className: 'col-6 fs-5', text: 'Total' }),
      h('dd', { className: 'col-6 text-end fs-5 fw-semibold', text: money(quote.total) }),
      h('dd', { className: 'col-12 text-body-secondary mb-0', text: items }),
    );
  }

  function renderOrderErrors(): void {
    orderAlert.hidden = orderErrors.length === 0;
    orderAlert.replaceChildren(
      orderErrors.length === 1
        ? h('span', { text: orderErrors[0] ?? '' })
        : h(
            'ul',
            { className: 'mb-0' },
            ...orderErrors.map((message) => h('li', { text: message })),
          ),
    );
  }

  function request(): OrderRequest {
    return {
      currency,
      items: cart.lines().map((line) => ({ sku: line.sku, quantity: line.quantity })),
    };
  }

  function scheduleQuote(): void {
    clearTimeout(quoteTimer);
    quoteRequest.abort();

    if (cart.lines().length === 0) {
      quote = undefined;
      quoteFailed = false;
      quoteStale = false;
      return;
    }
    quoteStale = true;
    quoteTimer = setTimeout(() => void refreshQuote(), debounce);
  }

  async function refreshQuote(): Promise<void> {
    quoteRequest = new AbortController();
    const { signal } = quoteRequest;

    try {
      quote = await quoteCart(options.http, request(), signal);
      quoteFailed = false;
    } catch {
      if (signal.aborted) {
        return;
      }
      quote = undefined;
      quoteFailed = true;
    }
    quoteStale = false;
    render();
  }

  async function checkout(): Promise<void> {
    if (submitting || cart.lines().length === 0) {
      return;
    }
    submitting = true;
    orderErrors = [];
    placedNumber = undefined;
    render();
    // The key follows the cart: a repeat of the same cart (a retry after a timeout) sends the same key.
    const key = options.keys.keyFor(keyScope, cart.fingerprint(currency));

    try {
      const order = await placeOrder(options.http, request(), key);
      options.keys.clear(keyScope);
      cart.clear();
      placedNumber = order.number;
    } catch (error) {
      orderErrors = messagesOf(error);
    }
    submitting = false;
    render();
  }

  cart.subscribe(() => {
    if (!submitting) {
      orderErrors = [];
      placedNumber = undefined;
    }
    scheduleQuote();
    render();
  });
  submit.addEventListener('click', () => void checkout());

  scheduleQuote();
  render();
}

function messagesOf(error: unknown): readonly string[] {
  if (error instanceof ApiError) {
    return error.messages;
  }
  if (error instanceof NetworkError) {
    return [error.message];
  }

  return ['The order could not be placed.'];
}
