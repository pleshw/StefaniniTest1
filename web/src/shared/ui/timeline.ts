import { formatDateTime } from '../domain/dates';
import type { TimelineEntry } from '../domain/timeline';
import { h } from './dom';
import { statusBadge } from './status';

/** The status changes of an order, oldest first. */
export function renderTimeline(entries: readonly TimelineEntry[]): HTMLElement {
  return h(
    'ol',
    { className: 'list-unstyled d-grid gap-2', attrs: { 'aria-label': 'Status timeline' } },
    ...entries.map((entry) =>
      h(
        'li',
        { className: 'd-flex flex-wrap align-items-center gap-2' },
        statusBadge(entry.status),
        ...(entry.label === undefined ? [] : [h('span', { text: entry.label })]),
        ...(entry.at === undefined
          ? []
          : [h('span', { className: 'text-body-secondary', text: formatDateTime(entry.at) })]),
        ...(entry.note === undefined ? [] : [h('span', { text: entry.note })]),
      ),
    ),
  );
}
