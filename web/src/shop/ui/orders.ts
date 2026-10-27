import type { HttpClient } from '../../shared/api/http';
import type { Order, OrderSummary } from '../../shared/api/orders';
import { formatDateTime } from '../../shared/domain/dates';
import { formatMoney } from '../../shared/domain/money';
import { h } from '../../shared/ui/dom';
import { errorMessage } from '../../shared/ui/errors';
import { statusBadge } from '../../shared/ui/status';
import { renderTimeline } from '../../shared/ui/timeline';
import { getOrder, listOrders } from '../api/orders';
import { timelineOf } from '../domain/orderTimeline';

export interface OrdersViewOptions {
  readonly http: HttpClient;
  readonly pageSize?: number;
}

export interface OrdersView {
  /** Shows the list again from its first page. The page calls it each time "My orders" is opened. */
  readonly refresh: () => void;
}

const defaultPageSize = 10;

/** The signed-in client's orders: a list with "load more", and the detail of one order. */
export function mountOrders(container: HTMLElement, options: OrdersViewOptions): OrdersView {
  const pageSize = options.pageSize ?? defaultPageSize;
  let orders: readonly OrderSummary[] = [];
  let next: string | null = null;
  let loading = false;
  let loadingMore = false;
  let failure: string | undefined;
  let request = new AbortController();
  let openedId: string | undefined;
  const buttons = new Map<string, HTMLButtonElement>();

  const heading = h('h2', {
    className: 'h4',
    text: 'My orders',
    attrs: { id: 'orders-heading', tabindex: '-1' },
  });
  const status = h('p', { className: 'text-body-secondary', attrs: { role: 'status' } });
  const alertBox = h('div', {
    className: 'alert alert-danger d-flex align-items-center gap-3',
    attrs: { role: 'alert' },
  });
  const body = h('tbody');
  const table = h(
    'table',
    { className: 'table align-middle' },
    h('caption', { className: 'visually-hidden', text: 'My orders' }),
    h(
      'thead',
      {},
      h(
        'tr',
        {},
        ...['Order', 'Placed', 'Status', 'Items', 'Total'].map((name) =>
          h('th', { text: name, attrs: { scope: 'col' } }),
        ),
      ),
    ),
    body,
  );
  const more = h('button', {
    className: 'btn btn-outline-primary',
    text: 'Load more',
    attrs: { type: 'button' },
  });
  const listPanel = h('div', {}, status, alertBox, table, more);
  const detailPanel = h('div');
  detailPanel.hidden = true;

  container.append(
    h(
      'section',
      { attrs: { 'aria-labelledby': 'orders-heading' } },
      heading,
      listPanel,
      detailPanel,
    ),
  );

  function render(): void {
    buttons.clear();
    body.replaceChildren(...orders.map(row));
    table.hidden = orders.length === 0;
    more.hidden = next === null || loading;
    status.textContent = loading
      ? 'Loading orders…'
      : failure !== undefined
        ? ''
        : orders.length === 0
          ? 'You have not placed any orders yet.'
          : `${String(orders.length)} ${orders.length === 1 ? 'order' : 'orders'}`;
    alertBox.hidden = failure === undefined;
    alertBox.replaceChildren(
      ...(failure === undefined
        ? []
        : [h('span', { text: failure }), retryButton(() => void load(loadingMore))]),
    );
  }

  function row(order: OrderSummary): HTMLElement {
    const open = h('button', {
      className: 'btn btn-link p-0',
      text: order.number,
      attrs: { type: 'button' },
    });
    open.addEventListener('click', () => void showDetail(order.id));
    buttons.set(order.id, open);

    return h(
      'tr',
      {},
      h('th', { attrs: { scope: 'row' } }, open),
      h('td', { text: formatDateTime(order.createdAt) }),
      h('td', {}, statusBadge(order.status)),
      h('td', { text: String(order.itemCount) }),
      h('td', { text: formatMoney(order.total, order.currency) }),
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
      orders = [];
      next = null;
    }
    render();

    try {
      const page = await listOrders(
        options.http,
        { limit: pageSize, ...(append && next !== null ? { after: next } : {}) },
        signal,
      );
      orders = [...orders, ...page.items];
      next = page.next;
    } catch (error) {
      if (signal.aborted) {
        return;
      }
      failure = errorMessage(error, 'Your orders could not be loaded.');
    }
    loading = false;
    render();
  }

  async function showDetail(id: string): Promise<void> {
    openedId = id;
    listPanel.hidden = true;
    detailPanel.hidden = false;
    const back = h('button', {
      className: 'btn btn-outline-secondary mb-3',
      attrs: { type: 'button' },
    });
    back.append(
      h('i', { className: 'bi bi-arrow-left me-1', attrs: { 'aria-hidden': 'true' } }),
      'Back to my orders',
    );
    back.addEventListener('click', closeDetail);
    const state = h('p', { text: 'Loading order…', attrs: { role: 'status' } });
    detailPanel.replaceChildren(back, state);
    back.focus();

    try {
      const order = await getOrder(options.http, id);
      if (openedId !== id) {
        return;
      }
      detailPanel.replaceChildren(back, renderDetail(order));
    } catch (error) {
      if (openedId !== id) {
        return;
      }
      detailPanel.replaceChildren(
        back,
        h(
          'div',
          { className: 'alert alert-danger', attrs: { role: 'alert' } },
          errorMessage(error, 'The order could not be loaded.'),
        ),
      );
    }
  }

  function closeDetail(): void {
    const id = openedId;
    openedId = undefined;
    detailPanel.hidden = true;
    detailPanel.replaceChildren();
    listPanel.hidden = false;
    (id === undefined ? heading : (buttons.get(id) ?? heading)).focus();
  }

  more.addEventListener('click', () => void load(true));

  return {
    refresh() {
      openedId = undefined;
      detailPanel.hidden = true;
      detailPanel.replaceChildren();
      listPanel.hidden = false;
      void load(false);
    },
  };
}

function retryButton(onClick: () => void): HTMLButtonElement {
  const button = h('button', {
    className: 'btn btn-sm btn-outline-danger ms-auto',
    text: 'Try again',
    attrs: { type: 'button' },
  });
  button.addEventListener('click', onClick);

  return button;
}

function renderDetail(order: Order): HTMLElement {
  const money = (amount: number): string => formatMoney(amount, order.currency);
  const lines = order.items.map((item) =>
    h(
      'tr',
      {},
      h(
        'th',
        { attrs: { scope: 'row' } },
        h('div', { text: item.productName }),
        h('small', { className: 'text-body-secondary', text: item.sku }),
      ),
      h('td', { className: 'text-end', text: String(item.quantity) }),
      h('td', { className: 'text-end', text: money(item.unitPrice) }),
      h('td', {
        className: 'text-end',
        text: item.lineDiscount > 0 ? `-${money(item.lineDiscount)}` : '',
      }),
      h('td', { className: 'text-end', text: money(item.lineTotal) }),
    ),
  );
  const figure = (name: string, value: string): Node[] => [
    h('dt', { className: 'col-6 col-md-3', text: name }),
    h('dd', { className: 'col-6 col-md-9', text: value }),
  ];

  return h(
    'div',
    {},
    h('h3', { className: 'h5', text: `Order ${order.number}`, attrs: { tabindex: '-1' } }),
    h(
      'p',
      { className: 'order-status' },
      statusBadge(order.status),
      ` Placed ${formatDateTime(order.createdAt)}`,
    ),
    h(
      'table',
      { className: 'table align-middle' },
      h('caption', { className: 'visually-hidden', text: 'Items' }),
      h(
        'thead',
        {},
        h(
          'tr',
          {},
          ...['Product', 'Quantity', 'Unit price', 'Discount', 'Total'].map((name, index) =>
            h('th', {
              className: index === 0 ? '' : 'text-end',
              text: name,
              attrs: { scope: 'col' },
            }),
          ),
        ),
      ),
      h('tbody', {}, ...lines),
    ),
    h(
      'div',
      { attrs: { role: 'group', 'aria-label': 'Order totals' } },
      h(
        'dl',
        { className: 'row' },
        ...figure('Items', String(order.itemCount)),
        ...figure('Subtotal', money(order.subtotal)),
        ...figure(
          'Discounts',
          order.discountTotal > 0 ? `-${money(order.discountTotal)}` : money(0),
        ),
        ...figure('Total', money(order.total)),
        ...figure('Currency', order.currency),
      ),
    ),
    h('h4', { className: 'h6', text: 'Status' }),
    renderTimeline(timelineOf(order)),
  );
}
