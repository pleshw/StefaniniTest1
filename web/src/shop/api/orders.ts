import type { HttpClient } from '../../shared/api/http';
import type { Order, OrderRequest, OrderSummary, Quote } from '../../shared/api/orders';

export function quoteCart(
  http: HttpClient,
  request: OrderRequest,
  signal?: AbortSignal,
): Promise<Quote> {
  return http
    .send<Quote>('/bff/shop/cart/quote', {
      method: 'POST',
      body: request,
      ...(signal === undefined ? {} : { signal }),
    })
    .then((response) => response.data);
}

/** Places the order. The same key on every call for the same cart makes Orders answer a repeat with the first order. */
export async function placeOrder(
  http: HttpClient,
  request: OrderRequest,
  idempotencyKey: string,
): Promise<Order> {
  const response = await http.send<Order>('/bff/shop/orders', {
    method: 'POST',
    body: request,
    idempotencyKey,
  });

  return response.data;
}

export interface OrderPage {
  readonly items: readonly OrderSummary[];
  /** The keyset cursor for the next page; null on the last one. */
  readonly next: string | null;
}

/** The signed-in client's orders, newest first. */
export function listOrders(
  http: HttpClient,
  query: { readonly after?: string; readonly limit: number },
  signal?: AbortSignal,
): Promise<OrderPage> {
  return http.get<OrderPage>('/bff/shop/orders', { ...query }, signal);
}

export function getOrder(http: HttpClient, id: string, signal?: AbortSignal): Promise<Order> {
  return http.get<Order>(`/bff/shop/orders/${encodeURIComponent(id)}`, undefined, signal);
}
