import type { HttpClient } from '../../shared/api/http';

/** An entry of the order event log, as the stream and the backfill return it. */
export interface OrderEvent {
  readonly id: number;
  readonly orderId: string;
  /** A versioned name such as OrderPlaced.v1; it is also the name of the server-sent event. */
  readonly type: string;
  /** The published contract as JSON; its fields depend on the type. */
  readonly payload: unknown;
  readonly occurredAt: string;
  /** What `after=` accepts to continue right after this entry; it is the id of the server-sent event. */
  readonly cursor?: string | null;
}

export interface EventPage {
  readonly items: readonly OrderEvent[];
  readonly next: string | null;
}

export interface EventQuery {
  readonly after?: string;
  readonly limit?: number;
  readonly type?: string;
  readonly orderId?: string;
}

/** The event log, oldest first: the backfill for the live stream. */
export function listEvents(
  http: HttpClient,
  query: EventQuery,
  signal?: AbortSignal,
): Promise<EventPage> {
  return http.get<EventPage>('/bff/admin/events', { ...query }, signal);
}

/** The names the BFF gives server-sent events: the log types, plus two it sends for the connection itself. */
export const streamEventNames = [
  'OrderPlaced.v1',
  'OrderStatusChanged.v1',
  'DeadLetterReplayed.v1',
  'heartbeat',
  'backfill-unavailable',
] as const;

export const logEventTypes = streamEventNames.slice(0, 3);
