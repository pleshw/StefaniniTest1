import type { HttpClient } from '../../shared/api/http';
import type { OrderSummary } from '../../shared/api/orders';
import { formatDateTime } from '../../shared/domain/dates';
import { formatMoney } from '../../shared/domain/money';
import { timelineOf } from '../../shared/domain/orderTimeline';
import { h } from '../../shared/ui/dom';
import { errorMessage } from '../../shared/ui/errors';
import { statusBadge } from '../../shared/ui/status';
import { renderOrderDetail } from '../../shared/ui/orderDetail';
import { getOrder, listOrders } from '../api/orders';

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
    text: 'Meus pedidos',
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
    h('caption', { className: 'visually-hidden', text: 'Meus pedidos' }),
    h(
      'thead',
      {},
      h(
        'tr',
        {},
        ...['Pedido', 'Realizado em', 'Status', 'Itens', 'Total'].map((name) =>
          h('th', { text: name, attrs: { scope: 'col' } }),
        ),
      ),
    ),
    body,
  );
  const more = h('button', {
    className: 'btn btn-outline-primary',
    text: 'Carregar mais',
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
      ? 'Carregando pedidos…'
      : failure !== undefined
        ? ''
        : orders.length === 0
          ? 'Você ainda não fez nenhum pedido.'
          : `${String(orders.length)} ${orders.length === 1 ? 'pedido' : 'pedidos'}`;
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
      failure = errorMessage(error, 'Não foi possível carregar seus pedidos.');
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
      'Voltar para meus pedidos',
    );
    back.addEventListener('click', closeDetail);
    const state = h('p', { text: 'Carregando o pedido…', attrs: { role: 'status' } });
    detailPanel.replaceChildren(back, state);
    back.focus();

    try {
      const order = await getOrder(options.http, id);
      if (openedId !== id) {
        return;
      }
      detailPanel.replaceChildren(back, renderOrderDetail(order, timelineOf(order)));
    } catch (error) {
      if (openedId !== id) {
        return;
      }
      detailPanel.replaceChildren(
        back,
        h(
          'div',
          { className: 'alert alert-danger', attrs: { role: 'alert' } },
          errorMessage(error, 'Não foi possível carregar o pedido.'),
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
    text: 'Tentar novamente',
    attrs: { type: 'button' },
  });
  button.addEventListener('click', onClick);

  return button;
}
