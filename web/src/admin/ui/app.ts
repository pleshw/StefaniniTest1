import { createHttpClient, loginUrl, type HttpClient } from '../../shared/api/http';
import type { EventSourceLike } from '../../shared/api/sse';
import { createIdempotencyKeys } from '../../shared/domain/idempotency';
import { h } from '../../shared/ui/dom';
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
import { renderAdminShell, type MenuGroup } from './shell';

export interface AdminDependencies {
  readonly http: HttpClient;
  /** Opens the event stream; the browser's EventSource by default. */
  readonly createSource?: (url: string) => EventSourceLike;
  /** Leaves the page after signing out; the browser's location by default. */
  readonly navigate?: (url: string) => void;
}

type Section = 'live' | 'create' | 'discounts' | 'confirmation' | 'deadLetters' | 'catalog';

/** The side menu: every section, grouped by what an admin does there, with what each one is for. */
const menu: readonly MenuGroup<Section>[] = [
  {
    title: 'Acompanhamento',
    items: [
      {
        section: 'live',
        label: 'Eventos ao vivo',
        icon: 'bi-broadcast',
        hint: 'Pedidos em tempo real, fila em espera e inspetor',
      },
    ],
  },
  {
    title: 'Pedidos',
    items: [
      {
        section: 'create',
        label: 'Criar pedido',
        icon: 'bi-cart-plus',
        hint: 'Fazer um pedido em nome de um cliente',
      },
    ],
  },
  {
    title: 'Regras',
    items: [
      {
        section: 'discounts',
        label: 'Regras de desconto',
        icon: 'bi-percent',
        hint: 'Descontos aplicados ao carrinho',
      },
      {
        section: 'confirmation',
        label: 'Regras de confirmação',
        icon: 'bi-shield-check',
        hint: 'Quando um pedido fica em espera',
      },
    ],
  },
  {
    title: 'Catálogo e mensagens',
    items: [
      {
        section: 'catalog',
        label: 'Catálogo',
        icon: 'bi-box-seam',
        hint: 'Nome, categoria e preços dos produtos',
      },
      {
        section: 'deadLetters',
        label: 'Mensagens mortas',
        icon: 'bi-envelope-exclamation',
        hint: 'Mensagens que falharam e o reprocessamento',
      },
    ],
  },
];

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
  const shell = renderAdminShell(root, 'Administração', menu, pageLinks);
  const { content } = shell;
  const navigate =
    dependencies.navigate ??
    ((url: string) => {
      window.location.assign(url);
    });
  void http.get<{ name: string }>('/bff/user').then(
    (user) => {
      shell.user.textContent = user.name;
    },
    () => {
      shell.user.textContent = '';
    },
  );
  shell.signOut.addEventListener('click', () => {
    shell.signOut.disabled = true;
    // Whatever the answer, the next page is the sign-in page: a session that is already gone is signed out as well.
    void http
      .send('/bff/logout', { method: 'POST' })
      .catch(() => undefined)
      .finally(() => {
        navigate(loginUrl('/admin.html'));
      });
  });

  const liveBox = h('div', { className: 'admin-panel' });
  const live = h('div', { className: 'col-xl-7' }, liveBox);
  const side = h('div', { className: 'col-xl-5 d-grid gap-4 align-content-start' });
  const queue = h('div', { className: 'admin-panel' });
  const inspectorBox = h('div', { className: 'admin-panel' });
  side.append(queue, inspectorBox);
  const monitoring = h('div', { className: 'row g-4' }, live, side);
  const inspector = mountInspector(inspectorBox, {
    http,
    onChanged: () => {
      onHold.refresh();
    },
  });
  const onHold = mountOnHold(queue, { http, onInspect: inspector.open });
  mountLiveEvents(liveBox, {
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

  /** The sections other than the live view: where each one lives and how it is built. */
  const lazy: Readonly<
    Record<Exclude<Section, 'live'>, { box: HTMLElement; mount: (box: HTMLElement) => void }>
  > = {
    create: {
      box: h('div', { className: 'admin-panel' }),
      mount: (box) => {
        mountCreateOrder(box, {
          http,
          cart: createCart({ storageKey: 'orders.admin.cart' }),
          keys: createIdempotencyKeys(),
        });
      },
    },
    discounts: {
      box: h('div', { className: 'admin-panel' }),
      mount: (box) => {
        mountDiscountRules(box, { http });
      },
    },
    confirmation: {
      box: h('div', { className: 'admin-panel' }),
      mount: (box) => {
        mountConfirmationRules(box, { http });
      },
    },
    deadLetters: {
      box: h('div', { className: 'admin-panel' }),
      mount: (box) => {
        mountDeadLetters(box, { http });
      },
    },
    catalog: {
      box: h('div', { className: 'admin-panel' }),
      mount: (box) => {
        mountCatalogEditor(box, { http });
      },
    },
  };
  const mounted = new Set<Section>();

  const show = (section: Section): void => {
    monitoring.hidden = section !== 'live';
    for (const [name, { box, mount }] of Object.entries(lazy)) {
      box.hidden = name !== section;
      if (name === section && !mounted.has(section)) {
        mounted.add(section);
        mount(box);
      }
    }
    shell.select(section);
  };
  for (const [section, button] of shell.buttons) {
    button.addEventListener('click', () => {
      show(section);
    });
  }

  content.append(monitoring, ...Object.values(lazy).map(({ box }) => box));
  show('live');
}
