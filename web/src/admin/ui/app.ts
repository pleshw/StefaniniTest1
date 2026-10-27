import { createHttpClient, type HttpClient } from '../../shared/api/http';
import type { EventSourceLike } from '../../shared/api/sse';
import { createIdempotencyKeys } from '../../shared/domain/idempotency';
import { h } from '../../shared/ui/dom';
import { renderShell } from '../../shared/ui/layout';
import { pageLinks } from '../../shared/ui/pages';
import { createCart } from '../../shop/domain/cart';
import { mountCatalogEditor } from './catalogEditor';
import { mountConfirmationRules } from './confirmationRules';
import { mountCreateOrder } from './createOrder';
import { mountDeadLetters } from './deadLetters';
import { mountDiscountRules } from './discountRules';
import { mountInspector } from './inspector';
import { mountLiveEvents } from './liveEvents';
import { mountOnHold } from './onHold';

export interface AdminDependencies {
  readonly http: HttpClient;
  /** Opens the event stream; the browser's EventSource by default. */
  readonly createSource?: (url: string) => EventSourceLike;
}

type Section = 'live' | 'create' | 'discounts' | 'confirmation' | 'deadLetters' | 'catalog';

/**
 * The Admin page. Live events with the OnHold queue and the order inspector sit together, because an admin goes from
 * an event or a waiting order to the order and acts on it. Every other section is mounted the first time it is opened,
 * so a page that never leaves the live view never asks for rules, dead letters or the catalog.
 */
export function mountAdmin(
  root: HTMLElement,
  dependencies: AdminDependencies = { http: createHttpClient() },
): void {
  const { http } = dependencies;
  const { content } = renderShell(root, 'Administração', pageLinks);

  const live = h('div', { className: 'col-lg-7' });
  const side = h('div', { className: 'col-lg-5 d-grid gap-4 align-content-start' });
  const queue = h('div');
  const inspectorBox = h('div');
  side.append(queue, inspectorBox);
  const monitoring = h('div', { className: 'row g-4' }, live, side);
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

  /** The sections other than the live view: where each one lives, how it is built and the button that opens it. */
  const lazy: Readonly<
    Record<
      Exclude<Section, 'live'>,
      { label: string; box: HTMLElement; mount: (box: HTMLElement) => void }
    >
  > = {
    create: {
      label: 'Criar pedido',
      box: h('div'),
      mount: (box) => {
        mountCreateOrder(box, {
          http,
          cart: createCart({ storageKey: 'orders.admin.cart' }),
          keys: createIdempotencyKeys(),
        });
      },
    },
    discounts: {
      label: 'Regras de desconto',
      box: h('div'),
      mount: (box) => {
        mountDiscountRules(box, { http });
      },
    },
    confirmation: {
      label: 'Regras de confirmação',
      box: h('div'),
      mount: (box) => {
        mountConfirmationRules(box, { http });
      },
    },
    deadLetters: {
      label: 'Mensagens mortas',
      box: h('div'),
      mount: (box) => {
        mountDeadLetters(box, { http });
      },
    },
    catalog: {
      label: 'Catálogo',
      box: h('div'),
      mount: (box) => {
        mountCatalogEditor(box, { http });
      },
    },
  };
  const mounted = new Set<Section>();
  const buttons = new Map<Section, HTMLButtonElement>([['live', sectionButton('Eventos ao vivo')]]);
  for (const [section, { label }] of Object.entries(lazy)) {
    buttons.set(section as Section, sectionButton(label));
  }

  const show = (section: Section): void => {
    monitoring.hidden = section !== 'live';
    for (const [name, { box, mount }] of Object.entries(lazy)) {
      box.hidden = name !== section;
      if (name === section && !mounted.has(section)) {
        mounted.add(section);
        mount(box);
      }
    }
    for (const [name, button] of buttons) {
      button.classList.toggle('active', name === section);
      button.setAttribute('aria-current', String(name === section));
    }
  };
  for (const [section, button] of buttons) {
    button.addEventListener('click', () => {
      show(section);
    });
  }

  content.append(
    h(
      'div',
      {
        className: 'nav nav-pills mb-3',
        attrs: { role: 'group', 'aria-label': 'Seções da administração' },
      },
      ...buttons.values(),
    ),
    monitoring,
    ...Object.values(lazy).map(({ box }) => box),
  );
  show('live');
}

function sectionButton(label: string): HTMLButtonElement {
  return h('button', { className: 'nav-link', text: label, attrs: { type: 'button' } });
}
