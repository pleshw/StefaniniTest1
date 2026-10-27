import type { OrderEvent } from '../api/events';
import { formatMoney } from '../../shared/domain/money';

/** One line of the live table. */
export interface FeedRow {
  readonly id: number;
  readonly cursor: string | undefined;
  readonly type: string;
  readonly orderId: string;
  /** The order number when the event carries it, else the start of the order id. */
  readonly orderLabel: string;
  /** The status the order has after the event, when the event says so. */
  readonly status?: string;
  /** The status the order had before, for a status change. */
  readonly from?: string;
  readonly occurredAt: string;
  readonly summary: string;
}

export function rowOf(event: OrderEvent): FeedRow {
  const payload = fieldsOf(event.payload);
  const orderLabel = text(payload['number']) ?? event.orderId.slice(0, 8);
  const common = {
    id: event.id,
    cursor: event.cursor ?? undefined,
    type: event.type,
    orderId: event.orderId,
    orderLabel,
    occurredAt: event.occurredAt,
  };

  switch (event.type) {
    case 'OrderPlaced.v1':
      return { ...common, status: 'Pending', summary: placedSummary(payload) };
    case 'OrderStatusChanged.v1': {
      const to = text(payload['to']);
      const from = text(payload['from']);

      return {
        ...common,
        ...(to === undefined ? {} : { status: to }),
        ...(from === undefined ? {} : { from }),
        summary: changedSummary(payload),
      };
    }
    case 'DeadLetterReplayed.v1': {
      const by = text(payload['replayedBy']);

      return {
        ...common,
        summary: by === undefined ? 'Dead letter replayed' : `Dead letter replayed by ${by}`,
      };
    }
    default:
      return { ...common, summary: '' };
  }
}

function placedSummary(payload: Readonly<Record<string, unknown>>): string {
  const channel = text(payload['channel']);
  const currency = text(payload['currency']);
  const total = payload['total'];
  const items = payload['itemCount'];
  const parts = [
    typeof total === 'number' && currency !== undefined ? formatMoney(total, currency) : undefined,
    typeof items === 'number' ? `${String(items)} ${items === 1 ? 'item' : 'items'}` : undefined,
  ].filter((part) => part !== undefined);
  const from = channel === undefined ? 'Placed' : `Placed from ${channel}`;

  return parts.length === 0 ? from : `${from}: ${parts.join(', ')}`;
}

function changedSummary(payload: Readonly<Record<string, unknown>>): string {
  const from = text(payload['from']);
  const to = text(payload['to']);
  const actor = text(payload['actor']);
  const reason = text(payload['reason']);

  if (from === undefined || to === undefined) {
    return '';
  }
  const change = `${from} → ${to}${actor === undefined ? '' : ` by ${actor}`}`;

  return reason === undefined ? change : `${change}: ${reason}`;
}

function fieldsOf(payload: unknown): Readonly<Record<string, unknown>> {
  return typeof payload === 'object' && payload !== null && !Array.isArray(payload)
    ? (payload as Record<string, unknown>)
    : {};
}

function text(value: unknown): string | undefined {
  return typeof value === 'string' && value !== '' ? value : undefined;
}

export interface FeedFilter {
  readonly type?: string;
  readonly status?: string;
}

export interface LiveFeed {
  /** False when the event was already seen (the stream and the backfill can both deliver it). */
  push(row: FeedRow): boolean;
  /** Newest first. */
  rows(filter?: FeedFilter): readonly FeedRow[];
  pause(): void;
  /** Adds what arrived while paused. */
  resume(): void;
  paused(): boolean;
  pending(): number;
}

export interface LiveFeedOptions {
  /** The most rows kept; the oldest go first. */
  readonly limit?: number;
}

export const defaultFeedLimit = 500;

export function createLiveFeed(options: LiveFeedOptions = {}): LiveFeed {
  const limit = options.limit ?? defaultFeedLimit;
  // Ids are remembered a while after their rows are gone, so a late repeat is still ignored.
  const rememberedIds = limit * 4;
  const seen = new Set<number>();
  const order: number[] = [];
  let rows: FeedRow[] = [];
  let buffered: FeedRow[] = [];
  let isPaused = false;

  function remember(id: number): boolean {
    if (seen.has(id)) {
      return false;
    }
    seen.add(id);
    order.push(id);
    if (order.length > rememberedIds) {
      const forgotten = order.shift();
      if (forgotten !== undefined) {
        seen.delete(forgotten);
      }
    }

    return true;
  }

  function add(row: FeedRow): void {
    rows = [row, ...rows].slice(0, limit);
  }

  return {
    push(row) {
      if (!remember(row.id)) {
        return false;
      }
      if (isPaused) {
        buffered = [row, ...buffered].slice(0, limit);
      } else {
        add(row);
      }

      return true;
    },
    rows(filter = {}) {
      return rows.filter(
        (row) =>
          (filter.type === undefined || row.type === filter.type) &&
          (filter.status === undefined || row.status === filter.status),
      );
    },
    pause() {
      isPaused = true;
    },
    resume() {
      isPaused = false;
      rows = [...buffered, ...rows].slice(0, limit);
      buffered = [];
    },
    paused: () => isPaused,
    pending: () => buffered.length,
  };
}
