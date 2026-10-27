import { createHttpClient, type HttpClient } from '../../shared/api/http';
import { createIdempotencyKeys } from '../../shared/domain/idempotency';
import { h } from '../../shared/ui/dom';
import { renderShell } from '../../shared/ui/layout';
import { pageLinks } from '../../shared/ui/pages';
import { createCart } from '../domain/cart';
import { mountCart } from './cart';
import { mountCatalog } from './catalog';

export interface ShopDependencies {
  readonly http: HttpClient;
}

/** The currency the Shop page asks prices in; the BFF's own default as well. */
export const shopCurrency = 'BRL';

/** The Shop page: the catalog next to the cart and its checkout. Orders arrive as a view in the next step. */
export function mountShop(
  root: HTMLElement,
  dependencies: ShopDependencies = { http: createHttpClient() },
): void {
  const { content } = renderShell(root, 'Shop', pageLinks);

  const cart = createCart();
  const catalog = h('div', { className: 'col-lg-8' });
  const side = h('div', { className: 'col-lg-4' });
  content.append(h('div', { className: 'row g-4' }, catalog, side));

  mountCatalog(catalog, {
    http: dependencies.http,
    currency: shopCurrency,
    onAdd: (product) => {
      cart.add(product);
    },
  });
  mountCart(side, {
    http: dependencies.http,
    cart,
    keys: createIdempotencyKeys(),
    currency: shopCurrency,
  });
}
