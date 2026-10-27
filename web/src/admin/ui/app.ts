import { createHttpClient, type HttpClient } from '../../shared/api/http';
import type { EventSourceLike } from '../../shared/api/sse';
import { createIdempotencyKeys } from '../../shared/domain/idempotency';
import { h } from '../../shared/ui/dom';
import { renderShell } from '../../shared/ui/layout';
import { pageLinks } from '../../shared/ui/pages';
import { createCart } from '../../shop/domain/cart';
import { mountCreateOrder } from './createOrder';
import { mountInspector } from './inspector';
import { mountLiveEvents } from './liveEvents';
import { mountOnHold } from './onHold';

export interface AdminDependencies {
  readonly http: HttpClient;
  /** Opens the event stream; the browser's EventSource by default. */
  readonly createSource?: (url: string) => EventSourceLike;
}

type Section = 'live' | 'create';

/**
 * The Admin page. Live events with the OnHold queue and the order inspector sit together, because an admin goes from
 * an event or a waiting order to the order and acts on it. Rules, dead letters and the catalog arrive next.
 */
export function mountAdmin(
  root: HTMLElement,
  dependencies: AdminDependencies = { http: createHttpClient() },
): void {
  const { http } = dependencies;
  const { content } = renderShell(root, 'Admin', pageLinks);

  const live = h('div', { className: 'col-lg-7' });
  const side = h('div', { className: 'col-lg-5 d-grid gap-4 align-content-start' });
  const queue = h('div');
  const inspectorBox = h('div');
  side.append(queue, inspectorBox);
  const monitoring = h('div', { className: 'row g-4' }, live, side);
  const creating = h('div');
  let createMounted = false;

  const inspector = mountInspector(inspectorBox, {
    http,
    onChanged: () => {
      onHold.refresh();
    },
  });
  const onHold = mountOnHold(queue, { http, onInspect: inspector.open });
  mountLiveEvents(live, {
    http,
    onInspect: inspector.open,
    // A queue changes when an order enters or leaves OnHold.
    onEvent: (row) => {
      if (row.status === 'OnHold' || row.from === 'OnHold') {
        onHold.refresh();
      }
    },
    ...(dependencies.createSource === undefined ? {} : { createSource: dependencies.createSource }),
  });

  const buttons: Readonly<Record<Section, HTMLButtonElement>> = {
    live: sectionButton('Live events'),
    create: sectionButton('Create order'),
  };
  const show = (section: Section): void => {
    monitoring.hidden = section !== 'live';
    creating.hidden = section !== 'create';
    for (const [name, button] of Object.entries(buttons)) {
      button.classList.toggle('active', name === section);
      button.setAttribute('aria-current', String(name === section));
    }
    if (section === 'create' && !createMounted) {
      createMounted = true;
      mountCreateOrder(creating, {
        http,
        cart: createCart({ storageKey: 'orders.admin.cart' }),
        keys: createIdempotencyKeys(),
      });
    }
  };
  buttons.live.addEventListener('click', () => {
    show('live');
  });
  buttons.create.addEventListener('click', () => {
    show('create');
  });

  content.append(
    h(
      'div',
      { className: 'nav nav-pills mb-3', attrs: { role: 'group', 'aria-label': 'Admin sections' } },
      buttons.live,
      buttons.create,
    ),
    monitoring,
    creating,
  );
  show('live');
}

function sectionButton(label: string): HTMLButtonElement {
  return h('button', { className: 'nav-link', text: label, attrs: { type: 'button' } });
}
