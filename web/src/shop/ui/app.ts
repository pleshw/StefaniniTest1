import { createHttpClient, type HttpClient } from '../../shared/api/http';
import { renderShell } from '../../shared/ui/layout';
import { pageLinks } from '../../shared/ui/pages';
import { mountCatalog } from './catalog';

export interface ShopDependencies {
  readonly http: HttpClient;
}

/** The currency the Shop page asks prices in; the BFF's own default as well. */
export const shopCurrency = 'BRL';

/** The Shop page. Cart, checkout and orders arrive as views in the next front-end steps. */
export function mountShop(
  root: HTMLElement,
  dependencies: ShopDependencies = { http: createHttpClient() },
): void {
  const { content } = renderShell(root, 'Shop', pageLinks);

  mountCatalog(content, {
    http: dependencies.http,
    currency: shopCurrency,
    // The cart arrives with the next step.
    onAdd: () => undefined,
  });
}
