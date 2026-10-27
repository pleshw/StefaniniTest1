import { listAdminOrders } from '../api/orders';
import type { HttpClient } from '../../shared/api/http';
import type { OrderSummary } from '../../shared/api/orders';
import { formatDateTime } from '../../shared/domain/dates';
import { formatMoney } from '../../shared/domain/money';
import { h } from '../../shared/ui/dom';
import { errorMessage } from '../../shared/ui/errors';

export interface OnHoldOptions {
  readonly http: HttpClient;
  readonly onInspect: (orderId: string) => void;
}

export interface OnHoldQueue {
  /** Reads the queue again. The list on screen stays until the new one arrives. */
  readonly refresh: () => void;
}

/** The orders waiting for an admin (status OnHold), the oldest first. */
export function mountOnHold(container: HTMLElement, options: OnHoldOptions): OnHoldQueue {
  let orders: readonly OrderSummary[] = [];
  let failure: string | undefined;
  let loaded = false;
  let generation = 0;

  const status = h('p', { className: 'text-body-secondary', attrs: { role: 'status' } });
  const alertBox = h('div', {
    className: 'alert alert-danger d-flex align-items-center gap-3',
    attrs: { role: 'alert' },
  });
  const list = h('ul', { className: 'list-group' });

  container.append(
    h(
      'section',
      { attrs: { 'aria-labelledby': 'onhold-heading' } },
      h('h2', { className: 'h4', text: 'OnHold queue', attrs: { id: 'onhold-heading' } }),
      status,
      alertBox,
      list,
    ),
  );

  function render(): void {
    list.replaceChildren(...orders.map(item));
    list.hidden = orders.length === 0;
    status.textContent = !loaded
      ? 'Loading the queue…'
      : failure !== undefined
        ? ''
        : orders.length === 0
          ? 'No orders are on hold.'
          : `${String(orders.length)} ${orders.length === 1 ? 'order' : 'orders'} waiting`;
    alertBox.hidden = failure === undefined;
    alertBox.replaceChildren(
      ...(failure === undefined ? [] : [h('span', { text: failure }), retry()]),
    );
  }

  function item(order: OrderSummary): HTMLElement {
    const inspect = h('button', {
      className: 'btn btn-link p-0',
      text: order.number,
      attrs: { type: 'button', 'aria-label': `Inspect ${order.number}` },
    });
    inspect.addEventListener('click', () => {
      options.onInspect(order.id);
    });

    return h(
      'li',
      { className: 'list-group-item d-flex flex-wrap justify-content-between gap-2' },
      h('span', {}, inspect, ` · ${order.client.name}`),
      h('span', {
        className: 'text-body-secondary',
        text: `${formatMoney(order.total, order.currency)} · ${formatDateTime(order.createdAt)}`,
      }),
    );
  }

  function retry(): HTMLButtonElement {
    const button = h('button', {
      className: 'btn btn-sm btn-outline-danger ms-auto',
      text: 'Try again',
      attrs: { type: 'button' },
    });
    button.addEventListener('click', refresh);

    return button;
  }

  function refresh(): void {
    const mine = ++generation;

    void listAdminOrders(options.http, { limit: 1 }).then(
      (answer) => {
        if (mine !== generation) {
          return;
        }
        loaded = true;
        if (answer.onHold === null) {
          failure = 'The OnHold queue could not be loaded.';
        } else {
          failure = undefined;
          orders = answer.onHold;
        }
        render();
      },
      (error: unknown) => {
        if (mine !== generation) {
          return;
        }
        loaded = true;
        failure = errorMessage(error, 'The OnHold queue could not be loaded.');
        render();
      },
    );
  }

  render();
  refresh();

  return { refresh };
}
