import { createOrderForClient, listAdminOrders } from '../api/orders';
import type { HttpClient } from '../../shared/api/http';
import type { Client } from '../../shared/api/orders';
import type { IdempotencyKeys } from '../../shared/domain/idempotency';
import { h } from '../../shared/ui/dom';
import type { Cart } from '../../shop/domain/cart';
import { mountCart } from '../../shop/ui/cart';
import { mountCatalog } from '../../shop/ui/catalog';

export interface CreateOrderOptions {
  readonly http: HttpClient;
  /** The admin's own cart, kept apart from the Shop cart. */
  readonly cart: Cart;
  readonly keys: IdempotencyKeys;
}

/** The currency the admin asks prices in, as the Shop page does. */
export const adminCurrency = 'BRL';

const otherClient = 'other';
const clientsSeen = 50;
const guid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

/**
 * Creates an order for a client: a client picker, the Shop's catalog and cart, and the admin order route. Orders has no
 * list of clients, so the picker offers the clients seen in the latest orders, or an id and a name typed in.
 */
export function mountCreateOrder(container: HTMLElement, options: CreateOrderOptions): void {
  const status = h('p', { text: 'Loading clients…', attrs: { role: 'status' } });
  const section = h(
    'section',
    { attrs: { 'aria-labelledby': 'create-heading' } },
    h('h2', {
      className: 'h4',
      text: 'Create order for a client',
      attrs: { id: 'create-heading' },
    }),
    status,
  );
  container.append(section);

  void knownClients(options.http).then((clients) => {
    status.remove();
    build(section, clients, options);
  });
}

async function knownClients(http: HttpClient): Promise<readonly Client[]> {
  try {
    const answer = await listAdminOrders(http, { limit: clientsSeen });
    const seen = new Map<string, Client>();

    for (const order of [...(answer.orders?.items ?? []), ...(answer.onHold ?? [])]) {
      seen.set(order.client.id, order.client);
    }

    return [...seen.values()].sort((a, b) => a.name.localeCompare(b.name));
  } catch {
    // Without the list the admin can still type a client in.
    return [];
  }
}

function build(
  section: HTMLElement,
  clients: readonly Client[],
  options: CreateOrderOptions,
): void {
  const picker = h(
    'select',
    { className: 'form-select', attrs: { id: 'create-client', name: 'client' } },
    h('option', { text: 'Choose a client', attrs: { value: '' } }),
    ...clients.map((client) => h('option', { text: client.name, attrs: { value: client.id } })),
    h('option', { text: 'Another client…', attrs: { value: otherClient } }),
  );
  const id = h('input', {
    className: 'form-control',
    attrs: { id: 'create-client-id', type: 'text', autocomplete: 'off' },
  });
  const name = h('input', {
    className: 'form-control',
    attrs: { id: 'create-client-name', type: 'text', autocomplete: 'off' },
  });
  const other = h(
    'div',
    { className: 'row g-2 mt-1' },
    h(
      'div',
      { className: 'col-md-7' },
      h('label', {
        className: 'form-label',
        text: 'Client id',
        attrs: { for: 'create-client-id' },
      }),
      id,
    ),
    h(
      'div',
      { className: 'col-md-5' },
      h('label', {
        className: 'form-label',
        text: 'Client name',
        attrs: { for: 'create-client-name' },
      }),
      name,
    ),
  );
  other.hidden = true;
  const catalog = h('div', { className: 'col-lg-8' });
  const side = h('div', { className: 'col-lg-4' });

  /** The client the order is for, or what is wrong with the choice. */
  function chosen(): { client: Client } | { problem: string } {
    if (picker.value === '') {
      return { problem: 'Choose a client first.' };
    }
    if (picker.value !== otherClient) {
      const client = clients.find((candidate) => candidate.id === picker.value);

      return client === undefined ? { problem: 'Choose a client first.' } : { client };
    }
    if (!guid.test(id.value.trim())) {
      return { problem: 'Enter a valid client id (a GUID).' };
    }
    if (name.value.trim() === '') {
      return { problem: 'Enter the client name.' };
    }

    return { client: { id: id.value.trim().toLowerCase(), name: name.value.trim() } };
  }

  picker.addEventListener('change', () => {
    other.hidden = picker.value !== otherClient;
  });

  section.append(
    h(
      'div',
      { className: 'mb-3' },
      h('label', { className: 'form-label', text: 'Client', attrs: { for: 'create-client' } }),
      picker,
      other,
      h('p', {
        className: 'form-text',
        text: 'The quote is calculated for you; Orders prices the order for the client.',
      }),
    ),
    h('div', { className: 'row g-4' }, catalog, side),
  );

  mountCatalog(catalog, {
    http: options.http,
    currency: adminCurrency,
    onAdd: (product) => {
      options.cart.add(product);
    },
  });
  mountCart(side, {
    http: options.http,
    cart: options.cart,
    keys: options.keys,
    currency: adminCurrency,
    keyScope: () => {
      const choice = chosen();

      return 'client' in choice ? `admin:${choice.client.id}` : 'admin';
    },
    validate: () => {
      const choice = chosen();

      return 'problem' in choice ? choice.problem : undefined;
    },
    place: (request, key) => {
      const choice = chosen();

      if ('problem' in choice) {
        return Promise.reject(new Error(choice.problem));
      }

      return createOrderForClient(
        options.http,
        { ...request, clientId: choice.client.id, clientName: choice.client.name },
        key,
      );
    },
  });
}
