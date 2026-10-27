import { createHttpClient, type HttpClient } from '../../shared/api/http';
import { createIdempotencyKeys } from '../../shared/domain/idempotency';
import { h } from '../../shared/ui/dom';
import { renderShell } from '../../shared/ui/layout';
import { pageLinks } from '../../shared/ui/pages';
import { createCart } from '../domain/cart';
import { mountCart } from './cart';
import { mountCatalog } from './catalog';
import { mountOrders } from './orders';

export interface ShopDependencies {
  readonly http: HttpClient;
}

/** The currency the Shop page asks prices in; the BFF's own default as well. */
export const shopCurrency = 'BRL';

/**
 * The Shop page: the catalog next to the cart and its checkout, and "My orders". The orders need a session, so they
 * are only asked for when that section is opened; browsing the catalog never sends anyone to the sign-in page.
 */
export function mountShop(
  root: HTMLElement,
  dependencies: ShopDependencies = { http: createHttpClient() },
): void {
  const { content } = renderShell(root, 'Shop', pageLinks);
  const cart = createCart();

  const catalog = h('div', { className: 'col-lg-8' });
  const side = h('div', { className: 'col-lg-4' });
  const shopping = h('div', { className: 'row g-4' }, catalog, side);
  const ordersSection = h('div');
  ordersSection.hidden = true;

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
  const orders = mountOrders(ordersSection, { http: dependencies.http });

  const shopButton = sectionButton('Shop');
  const ordersButton = sectionButton('My orders');
  const show = (section: 'shop' | 'orders'): void => {
    shopping.hidden = section !== 'shop';
    ordersSection.hidden = section !== 'orders';
    shopButton.classList.toggle('active', section === 'shop');
    ordersButton.classList.toggle('active', section === 'orders');
    shopButton.setAttribute('aria-current', String(section === 'shop'));
    ordersButton.setAttribute('aria-current', String(section === 'orders'));
    if (section === 'orders') {
      orders.refresh();
    }
  };
  shopButton.addEventListener('click', () => {
    show('shop');
  });
  ordersButton.addEventListener('click', () => {
    show('orders');
  });

  content.append(
    h(
      'div',
      { className: 'nav nav-pills mb-3', attrs: { role: 'group', 'aria-label': 'Shop sections' } },
      shopButton,
      ordersButton,
    ),
    shopping,
    ordersSection,
  );
  show('shop');
}

function sectionButton(label: string): HTMLButtonElement {
  return h('button', { className: 'nav-link', text: label, attrs: { type: 'button' } });
}
