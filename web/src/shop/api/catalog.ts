import type { HttpClient } from '../../shared/api/http';

export interface ShopPrice {
  readonly amount: number;
  readonly currency: string;
}

/** A product as the BFF's shop view model gives it: no version, no attributes. */
export interface ShopProduct {
  readonly sku: string;
  readonly name: string;
  readonly description: string;
  readonly category: string;
  /** Null when the product has no price in the requested currency. */
  readonly price: ShopPrice | null;
}

export interface ShopProductPage {
  readonly items: readonly ShopProduct[];
  /** The keyset cursor for the next page; null on the last one. */
  readonly next: string | null;
}

export interface ProductQuery {
  readonly q?: string;
  readonly category?: string;
  readonly currency: string;
  readonly after?: string;
  readonly limit: number;
}

export function listProducts(
  http: Pick<HttpClient, 'get'>,
  query: ProductQuery,
  signal?: AbortSignal,
): Promise<ShopProductPage> {
  return http.get<ShopProductPage>('/bff/shop/products', { ...query }, signal);
}
