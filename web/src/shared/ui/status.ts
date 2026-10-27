import { statusView } from '../domain/status';
import { h } from './dom';

/** The status as a Bootstrap badge: its icon (hidden from screen readers) and its label. */
export function statusBadge(status: string): HTMLElement {
  const view = statusView(status);

  return h(
    'span',
    { className: `badge ${view.badgeClass}` },
    h('i', { className: `bi ${view.icon} me-1`, attrs: { 'aria-hidden': 'true' } }),
    view.label,
  );
}
