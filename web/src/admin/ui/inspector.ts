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
import { timelineOf } from '../../shared/domain/orderTimeline';
import type { TimelineEntry } from '../../shared/domain/timeline';
import { askReason } from '../../shared/ui/dialog';
import { h } from '../../shared/ui/dom';
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

const actionTexts: Readonly<
  Record<OrderAction, { button: string; title: (number: string) => string; confirm: string }>
> = {
  confirm: {
    button: 'Confirm',
    title: (number) => `Confirm order ${number}`,
    confirm: 'Confirm order',
  },
  hold: {
    button: 'Hold',
    title: (number) => `Put order ${number} on hold`,
    confirm: 'Put on hold',
  },
  block: { button: 'Block', title: (number) => `Block order ${number}`, confirm: 'Block order' },
  cancel: {
    button: 'Cancel',
    title: (number) => `Cancel order ${number}`,
    confirm: 'Cancel order',
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

  const heading = h('h2', {
    className: 'h4',
    text: 'Order inspector',
    attrs: { id: 'inspector-heading', tabindex: '-1' },
  });
  const body = h('div');
  container.append(
    h('section', { attrs: { 'aria-labelledby': 'inspector-heading' } }, heading, body),
  );

  function render(): void {
    if (orderId === undefined) {
      body.replaceChildren(
        h('p', {
          className: 'text-body-secondary',
          text: 'Choose an order from the live events or the OnHold queue.',
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
      body.replaceChildren(h('p', { text: 'Loading order…', attrs: { role: 'status' } }));
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
        text: `Client: ${order.client.name} · Channel: ${order.channel}`,
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
          text: `Some parts could not be loaded: ${view.unavailable.join(', ')}.`,
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
        const reload = h('button', {
          className: 'btn btn-sm btn-outline-danger ms-auto',
          text: 'Reload',
          attrs: { type: 'button' },
        });
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
      const button = h('button', {
        className: 'btn btn-outline-primary btn-sm',
        text: actionTexts[action].button,
        attrs: { type: 'button' },
      });
      button.disabled = busy;
      button.addEventListener('click', () => void run(action, number));

      return button;
    });

    return h(
      'div',
      {
        className: 'd-flex flex-wrap gap-2 my-3',
        attrs: { role: 'group', 'aria-label': 'Order actions' },
      },
      ...buttons,
    );
  }

  function eventsTable(view: AdminOrderView): HTMLElement {
    const events = [...(view.events ?? [])].reverse().map(rowOf);

    return h(
      'div',
      {},
      h('h4', { className: 'h6', text: 'Events' }),
      events.length === 0
        ? h('p', {
            className: 'text-body-secondary',
            text: view.events === null ? 'Events are not available.' : 'No events yet.',
          })
        : h(
            'table',
            { className: 'table table-sm' },
            h('caption', { className: 'visually-hidden', text: 'Events' }),
            h(
              'thead',
              {},
              h(
                'tr',
                {},
                ...['Time', 'Event', 'Details'].map((name) =>
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
      loadError = errorMessage(error, 'The order could not be loaded.');
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
      label: 'Reason',
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
      message = `Order ${updated.number} is now ${updated.status}.`;
      busy = false;
      options.onChanged();
      await load(updated.id, false);
      return;
    } catch (error) {
      actionError = {
        message:
          error instanceof PreconditionFailedError
            ? 'This order was changed by someone else. Reload it to see its current state.'
            : errorMessage(error, 'The action failed.'),
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
    label: change.from === null ? 'Placed' : `${change.from} → ${change.to}`,
    at: change.changedAt,
    note:
      change.reason === null || change.reason === ''
        ? `by ${change.actor}`
        : `by ${change.actor}: ${change.reason}`,
  }));
}
