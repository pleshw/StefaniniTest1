import { renderShell } from '../../shared/ui/layout';
import { pageLinks } from '../../shared/ui/pages';

/** The Shop page. Catalog, cart, checkout and orders arrive as views in the next front-end steps. */
export function mountShop(root: HTMLElement): void {
  const { content } = renderShell(root, 'Shop', pageLinks);
  const intro = document.createElement('p');
  intro.textContent = 'Browse the catalog, build a cart and place your order.';
  content.append(intro);
}
