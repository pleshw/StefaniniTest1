import type { HttpClient } from '../../shared/api/http';
import type { Order, OrderRequest, Quote } from '../../shared/api/orders';

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
