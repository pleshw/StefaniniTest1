import type { Order } from '../api/orders';
import { formatDateTime } from '../domain/dates';
import { formatMoney } from '../domain/money';
import type { TimelineEntry } from '../domain/timeline';
import { h } from './dom';
import { statusBadge } from './status';
import { renderTimeline } from './timeline';

/** An order with its items, totals and status timeline: what the shopper and the admin inspector both show. */
export function renderOrderDetail(order: Order, timeline: readonly TimelineEntry[]): HTMLElement {
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
    renderTimeline(timeline),
  );
}
