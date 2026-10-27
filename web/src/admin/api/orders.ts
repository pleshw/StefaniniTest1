import type { HttpClient } from '../../shared/api/http';
import type { Order, OrderRequest, OrderSummary } from '../../shared/api/orders';
import type { OrderAction } from '../domain/actions';
import type { OrderEvent } from './events';

/** One entry of an order's status history. */
export interface OrderStatusChange {
  readonly seq: number;
  readonly from: string | null;
  readonly to: string;
  readonly reason: string | null;
  readonly actor: string;
  readonly changedAt: string;
}

/** The order inspector: a part that could not be read is null and named in `unavailable`. */
export interface AdminOrderView {
  readonly order: Order;
  readonly history: readonly OrderStatusChange[] | null;
  readonly events: readonly OrderEvent[] | null;
  readonly partial: boolean;
  readonly unavailable: readonly string[];
}

export interface AdminOrderList {
  /** Oldest first. */
  readonly onHold: readonly OrderSummary[] | null;
  readonly orders: { readonly items: readonly OrderSummary[]; readonly next: string | null } | null;
  readonly partial: boolean;
  readonly unavailable: readonly string[];
}

export function inspectOrder(
  http: HttpClient,
  id: string,
  signal?: AbortSignal,
): Promise<AdminOrderView> {
  return http.get<AdminOrderView>(`/bff/admin/orders/${encodeURIComponent(id)}`, undefined, signal);
}

export function listAdminOrders(
  http: HttpClient,
  query: { readonly limit?: number; readonly status?: string; readonly q?: string } = {},
  signal?: AbortSignal,
): Promise<AdminOrderList> {
  return http.get<AdminOrderList>('/bff/admin/orders', { ...query }, signal);
}

/** Changes the status of the order version the admin looked at; Orders answers 412 if it has changed since. */
export async function changeOrderStatus(
  http: HttpClient,
  id: string,
  action: OrderAction,
  change: { readonly reason: string; readonly rowVersion: number },
): Promise<Order> {
  const response = await http.send<Order>(`/bff/admin/orders/${encodeURIComponent(id)}/${action}`, {
    method: 'POST',
    body: change.reason === '' ? {} : { reason: change.reason },
    ifMatch: change.rowVersion,
  });

  return response.data;
}

export interface AdminOrderRequest extends OrderRequest {
  readonly clientId: string;
  readonly clientName: string;
}

/** Places an order for a client. The key is the client's: Orders scopes idempotency by the target client. */
export async function createOrderForClient(
  http: HttpClient,
  request: AdminOrderRequest,
  idempotencyKey: string,
): Promise<Order> {
  const response = await http.send<Order>('/bff/admin/orders', {
    method: 'POST',
    body: request,
    idempotencyKey,
  });

  return response.data;
}
