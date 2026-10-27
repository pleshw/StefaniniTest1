import {
  changeOrderStatus,
  inspectOrder,
  type AdminOrderView,
  type OrderStatusChange,
} from '../api/orders';
import { actionsFor, reasonRequired, type OrderAction } from '../domain/actions';
import { rowOf } from '../domain/feed';
import { PreconditionFailedError } from '../../shared/api/errors';
import type { HttpClient } from '../../shared/api/http';
import { formatDateTime } from '../../shared/domain/dates';
import { statusView } from '../../shared/domain/status';
import { timelineOf } from '../../shared/domain/orderTimeline';
import type { TimelineEntry } from '../../shared/domain/timeline';
import { askReason } from '../../shared/ui/dialog';
import { h } from '../../shared/ui/dom';
import { withIcon } from '../../shared/ui/icons';
import { errorMessage } from '../../shared/ui/errors';
import { renderOrderDetail } from '../../shared/ui/orderDetail';

export interface InspectorOptions {
  readonly http: HttpClient;
  /** Called after an action changed an order, so the page can refresh the OnHold queue. */
  readonly onChanged: () => void;
}

export interface Inspector {
  /** Loads the order with its history and events, and moves the focus to the inspector. */
  readonly open: (orderId: string) => void;
}

const maxReasonLength = 500;

/** Each action's colour and icon: confirming is the positive one, blocking and cancelling the destructive ones. */
const actionLooks: Readonly<Record<OrderAction, { className: string; icon: string }>> = {
  confirm: { className: 'btn-success', icon: 'bi-check-circle' },
  hold: { className: 'btn-outline-secondary', icon: 'bi-pause-circle' },
  block: { className: 'btn-outline-danger', icon: 'bi-slash-circle' },
  cancel: { className: 'btn-outline-danger', icon: 'bi-x-circle' },
};

const actionTexts: Readonly<
  Record<OrderAction, { button: string; title: (number: string) => string; confirm: string }>
> = {
  confirm: {
    button: 'Confirmar',
    title: (number) => `Confirmar pedido ${number}`,
    confirm: 'Confirmar pedido',
  },
  hold: {
    button: 'Colocar em espera',
    title: (number) => `Colocar o pedido ${number} em espera`,
    confirm: 'Colocar em espera',
  },
  block: {
    button: 'Bloquear',
    title: (number) => `Bloquear pedido ${number}`,
    confirm: 'Bloquear pedido',
  },
  cancel: {
    button: 'Cancelar',
    title: (number) => `Cancelar pedido ${number}`,
    confirm: 'Cancelar pedido',
  },
};

/** The order inspector: the order, its status history and events, and the actions an admin can take on it. */
export function mountInspector(container: HTMLElement, options: InspectorOptions): Inspector {
  let orderId: string | undefined;
  let current: AdminOrderView | undefined;
  let loadError: string | undefined;
  let actionError: { message: string; stale: boolean } | undefined;
  let message: string | undefined;
  let busy = false;
  let generation = 0;

  const heading = withIcon(
    h('h2', {
      className: 'h4',
      text: 'Inspetor de pedidos',
      attrs: { id: 'inspector-heading', tabindex: '-1' },
    }),
    'bi-search',
  );
  const body = h('div');
  container.append(
    h('section', { attrs: { 'aria-labelledby': 'inspector-heading' } }, heading, body),
  );

  function render(): void {
    if (orderId === undefined) {
      body.replaceChildren(
        h('p', {
          className: 'text-body-secondary',
          text: 'Escolha um pedido nos eventos ao vivo ou na fila em espera.',
        }),
      );
      return;
    }
    if (loadError !== undefined) {
      body.replaceChildren(
        h('div', { className: 'alert alert-danger', text: loadError, attrs: { role: 'alert' } }),
      );
      return;
    }
    if (current === undefined) {
      body.replaceChildren(h('p', { text: 'Carregando pedido…', attrs: { role: 'status' } }));
      return;
    }
    const { order } = current;

    body.replaceChildren(
      h('p', { text: message ?? '', attrs: { role: 'status' } }),
      ...notices(current),
      renderOrderDetail(
        order,
        current.history === null ? timelineOf(order) : historyTimeline(current.history),
      ),
      h('p', {
        className: 'mb-2 text-body-secondary',
        text: `Cliente: ${order.client.name} · Canal: ${order.channel}`,
      }),
      actionButtons(order.status, order.number),
      eventsTable(current),
    );
  }

  function notices(view: AdminOrderView): HTMLElement[] {
    const found: HTMLElement[] = [];

    if (view.partial) {
      found.push(
        h('div', {
          className: 'alert alert-warning',
          text: `Algumas partes não puderam ser carregadas: ${view.unavailable.join(', ')}.`,
          attrs: { role: 'alert' },
        }),
      );
    }
    if (actionError !== undefined) {
      const alert = h('div', {
        className: 'alert alert-danger d-flex align-items-center gap-3',
        attrs: { role: 'alert' },
      });
      alert.append(h('span', { text: actionError.message }));
      if (actionError.stale) {
        const reload = withIcon(
          h('button', {
            className: 'btn btn-sm btn-outline-danger ms-auto',
            text: 'Recarregar',
            attrs: { type: 'button' },
          }),
          'bi-arrow-clockwise',
          'me-1',
        );
        reload.addEventListener('click', () => {
          actionError = undefined;
          if (orderId !== undefined) {
            void load(orderId, false);
          }
        });
        alert.append(reload);
      }
      found.push(alert);
    }

    return found;
  }

  function actionButtons(status: string, number: string): HTMLElement {
    const buttons = actionsFor(status).map((action) => {
      const button = withIcon(
        h('button', {
          className: `btn btn-sm ${actionLooks[action].className}`,
          text: actionTexts[action].button,
          attrs: { type: 'button' },
        }),
        actionLooks[action].icon,
        'me-1',
      );
      button.disabled = busy;
      button.addEventListener('click', () => void run(action, number));

      return button;
    });

    return h(
      'div',
      {
        className: 'd-flex flex-wrap gap-2 my-3',
        attrs: { role: 'group', 'aria-label': 'Ações do pedido' },
      },
      ...buttons,
    );
  }

  function eventsTable(view: AdminOrderView): HTMLElement {
    const events = [...(view.events ?? [])].reverse().map(rowOf);

    return h(
      'div',
      {},
      h('h4', { className: 'h6', text: 'Eventos' }),
      events.length === 0
        ? h('p', {
            className: 'text-body-secondary',
            text:
              view.events === null ? 'Os eventos não estão disponíveis.' : 'Ainda não há eventos.',
          })
        : h(
            'table',
            { className: 'table table-sm' },
            h('caption', { className: 'visually-hidden', text: 'Eventos' }),
            h(
              'thead',
              {},
              h(
                'tr',
                {},
                ...['Hora', 'Evento', 'Detalhes'].map((name) =>
                  h('th', { text: name, attrs: { scope: 'col' } }),
                ),
              ),
            ),
            h(
              'tbody',
              {},
              ...events.map((event) =>
                h(
                  'tr',
                  {},
                  h('td', { className: 'text-nowrap', text: formatDateTime(event.occurredAt) }),
                  h('td', { text: event.type }),
                  h('td', { text: event.summary }),
                ),
              ),
            ),
          ),
    );
  }

  async function load(id: string, clearMessage: boolean): Promise<void> {
    const mine = ++generation;
    orderId = id;
    loadError = undefined;
    if (clearMessage) {
      message = undefined;
      current = undefined;
    }
    render();

    try {
      const view = await inspectOrder(options.http, id);
      if (mine !== generation) {
        return;
      }
      current = view;
    } catch (error) {
      if (mine !== generation) {
        return;
      }
      loadError = errorMessage(error, 'Não foi possível carregar o pedido.');
    }
    render();
  }

  async function run(action: OrderAction, number: string): Promise<void> {
    const view = current;
    if (view === undefined || busy) {
      return;
    }
    const texts = actionTexts[action];
    const reason = await askReason({
      title: texts.title(number),
      label: 'Motivo',
      confirmLabel: texts.confirm,
      required: reasonRequired(action),
      maxLength: maxReasonLength,
    });

    if (reason === null) {
      return;
    }
    busy = true;
    actionError = undefined;
    render();

    try {
      const updated = await changeOrderStatus(options.http, view.order.id, action, {
        reason,
        rowVersion: view.order.rowVersion,
      });
      message = `O pedido ${updated.number} agora está ${statusView(updated.status).label}.`;
      busy = false;
      options.onChanged();
      await load(updated.id, false);
      return;
    } catch (error) {
      actionError = {
        message:
          error instanceof PreconditionFailedError
            ? 'Este pedido foi alterado por outra pessoa. Recarregue-o para ver o estado atual.'
            : errorMessage(error, 'A ação falhou.'),
        stale: error instanceof PreconditionFailedError,
      };
    }
    busy = false;
    render();
  }

  render();

  return {
    open(id) {
      void load(id, true).then(() => {
        heading.focus();
      });
    },
  };
}

function historyTimeline(history: readonly OrderStatusChange[]): readonly TimelineEntry[] {
  return history.map((change) => ({
    status: change.to,
    label:
      change.from === null
        ? 'Pedido realizado'
        : `${statusView(change.from).label} → ${statusView(change.to).label}`,
    at: change.changedAt,
    note:
      change.reason === null || change.reason === ''
        ? `por ${change.actor}`
        : `por ${change.actor}: ${change.reason}`,
  }));
}
