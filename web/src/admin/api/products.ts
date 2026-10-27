import type { HttpClient } from '../../shared/api/http';

/** The currencies Catalog accepts (its `AllowedCurrencies` setting); a product has at most one price in each. */
export const catalogCurrencies = ['BRL', 'USD'] as const;

export interface ProductPrice {
  readonly currency: string;
  readonly amount: number;
}

/** A product with every price and the version Catalog stores it at. */
export interface ProductDetail {
  readonly sku: string;
  readonly name: string;
  readonly description: string;
  readonly category: string;
  readonly attributes: Readonly<Record<string, string>>;
  readonly prices: readonly ProductPrice[];
  readonly isActive: boolean;
  readonly version: number;
}

/** What Catalog's read answers: the product with the price in the currency asked, or none. */
interface ProductRead {
  readonly sku: string;
  readonly name: string;
  readonly description: string;
  readonly category: string;
  readonly attributes: Readonly<Record<string, string>>;
  readonly price: ProductPrice | null;
  readonly isActive: boolean;
  readonly version: number;
}

/** What the editor changes; the rest of the product travels back as it was read. */
export interface ProductChange {
  readonly name: string;
  readonly category: string;
  readonly prices: readonly ProductPrice[];
}

/** The product changed between the reads of two currencies, and kept changing. */
export class ProductChangedWhileReadingError extends Error {
  constructor() {
    super('O produto mudou enquanto era lido. Tente novamente.');
    this.name = 'ProductChangedWhileReadingError';
  }
}

const attempts = 2;

/**
 * Reads the product through the BFF's `GET /bff/admin/products/{sku}`. Catalog answers with one price at a time, so
 * there is one read per currency; they must agree on the version, or the reads start again.
 */
export async function getProduct(
  http: HttpClient,
  sku: string,
  signal?: AbortSignal,
): Promise<ProductDetail> {
  const path = `/bff/admin/products/${encodeURIComponent(sku)}`;

  for (let attempt = 0; attempt < attempts; attempt++) {
    const reads = await Promise.all(
      catalogCurrencies.map((currency) => http.get<ProductRead>(path, { currency }, signal)),
    );
    const first = reads[0];

    if (first !== undefined && reads.every((read) => read.version === first.version)) {
      return {
        sku: first.sku,
        name: first.name,
        description: first.description,
        category: first.category,
        attributes: first.attributes,
        prices: reads.flatMap((read) => (read.price === null ? [] : [read.price])),
        isActive: first.isActive,
        version: first.version,
      };
    }
  }

  throw new ProductChangedWhileReadingError();
}

/** Replaces the product at the version that was read (If-Match). Catalog answers 412 if it has changed since. */
export async function saveProduct(
  http: HttpClient,
  product: ProductDetail,
  change: ProductChange,
): Promise<ProductDetail> {
  const response = await http.send<ProductDetail>(
    `/bff/admin/products/${encodeURIComponent(product.sku)}`,
    {
      method: 'PUT',
      body: {
        name: change.name,
        description: product.description,
        category: change.category,
        attributes: product.attributes,
        prices: change.prices,
      },
      ifMatch: product.version,
    },
  );

  return response.data;
}
