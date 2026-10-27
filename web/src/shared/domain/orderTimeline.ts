import type { Order } from '../api/orders';
import type { TimelineEntry } from './timeline';

/**
 * What the shopper can know of an order's life from the Shop routes. They do not return the status history, so the
 * timeline is the placement and, once the order has moved on, the current status.
 */
export function timelineOf(order: Pick<Order, 'status' | 'createdAt'>): readonly TimelineEntry[] {
  const placed: TimelineEntry = { status: 'Pending', label: 'Pedido realizado', at: order.createdAt };

  return order.status === 'Pending'
    ? [placed]
    : [placed, { status: order.status, label: 'Status atual' }];
}
