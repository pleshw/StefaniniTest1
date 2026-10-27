/** The statuses an order goes through, as Orders names them. */
export const orderStatuses = [
  'Pending',
  'OnHold',
  'Confirmed',
  'Blocked',
  'Cancelled',
  'Failed',
] as const;

export type OrderStatus = (typeof orderStatuses)[number];

export interface StatusView {
  readonly label: string;
  /** The Bootstrap badge classes; the text colour is part of them, so the contrast is safe. */
  readonly badgeClass: string;
  /** A Bootstrap Icons class. Pages show it next to the label, never instead of it. */
  readonly icon: string;
}

const views: Readonly<Record<OrderStatus, StatusView>> = {
  Pending: { label: 'Pending', badgeClass: 'text-bg-secondary', icon: 'bi-hourglass-split' },
  OnHold: { label: 'On hold', badgeClass: 'text-bg-warning', icon: 'bi-pause-circle' },
  Confirmed: { label: 'Confirmed', badgeClass: 'text-bg-success', icon: 'bi-check-circle' },
  Blocked: { label: 'Blocked', badgeClass: 'text-bg-danger', icon: 'bi-slash-circle' },
  Cancelled: { label: 'Cancelled', badgeClass: 'text-bg-dark', icon: 'bi-x-circle' },
  Failed: { label: 'Failed', badgeClass: 'text-bg-danger', icon: 'bi-exclamation-triangle' },
};

export function isOrderStatus(value: string): value is OrderStatus {
  return (orderStatuses as readonly string[]).includes(value);
}

/** A status Orders adds later still renders, under its own name. */
export function statusView(status: string): StatusView {
  return isOrderStatus(status)
    ? views[status]
    : { label: status, badgeClass: 'text-bg-light', icon: 'bi-question-circle' };
}
