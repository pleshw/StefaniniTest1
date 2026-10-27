import { UnauthorizedError } from './errors';
import type { HttpClient } from './http';

export type StreamStatus = 'connecting' | 'open' | 'reconnecting' | 'closed';

/** One server-sent event: the name the BFF gave it (the log type), its JSON data and its id (the log cursor). */
export interface StreamEvent {
  readonly name: string;
  readonly data: string;
  readonly lastEventId: string;
}

/** The part of a MessageEvent the wrapper reads. */
export interface MessageLike {
  readonly data: string;
  readonly lastEventId: string;
}

/** What the wrapper needs of an EventSource, so tests can stand in for the browser's. */
export interface EventSourceLike {
  readonly readyState: number;
  onopen: ((event: Event) => void) | null;
  onerror: ((event: Event) => void) | null;
  addEventListener(name: string, listener: (event: MessageLike) => void): void;
  close(): void;
}

export interface EventStreamOptions {
  readonly url: string;
  /** The event names to listen for; a named event never reaches onmessage. */
  readonly eventNames: readonly string[];
  readonly onEvent: (event: StreamEvent) => void;
  readonly onStatus?: (status: StreamStatus) => void;
  readonly createSource?: (url: string) => EventSourceLike;
  /** Asks the BFF who is signed in, to tell an expired session from an outage once the stream closes. */
  readonly http?: Pick<HttpClient, 'get'>;
}

export interface EventStream {
  /** The id of the last event received: the log cursor to backfill from. */
  readonly lastEventId: () => string | undefined;
  readonly status: () => StreamStatus;
  /**
   * Opens a new connection after the stream closed (or after the page paused it). The browser sends Last-Event-ID only
   * on its own automatic reconnect, never on a new EventSource, so the caller backfills from lastEventId first.
   */
  readonly reopen: () => void;
  readonly close: () => void;
}

const connecting = 0;
const closedState = 2;

/**
 * A server-sent event stream that keeps the last event id. While the connection drops and the browser reconnects by
 * itself (it sends Last-Event-ID on that retry) the stream only reports "reconnecting". When the browser gives up, the
 * stream is closed: an expired session sends the page to the sign-in page, anything else is reported as closed.
 */
export function openEventStream(options: EventStreamOptions): EventStream {
  const createSource: (url: string) => EventSourceLike =
    options.createSource ?? ((url) => new EventSource(url));
  let source: EventSourceLike | undefined;
  let status: StreamStatus = 'connecting';
  let lastEventId: string | undefined;

  const setStatus = (next: StreamStatus): void => {
    status = next;
    options.onStatus?.(next);
  };

  const probeSession = async (): Promise<void> => {
    try {
      await options.http?.get('/bff/user');
    } catch (error) {
      // The client has already sent the page to the sign-in page.
      if (!(error instanceof UnauthorizedError)) {
        return;
      }
    }
  };

  const connect = (): void => {
    const current = createSource(options.url);
    source = current;
    setStatus('connecting');

    current.onopen = () => {
      setStatus('open');
    };
    current.onerror = () => {
      if (source !== current) {
        return;
      }
      if (current.readyState === connecting) {
        setStatus('reconnecting');
        return;
      }
      if (current.readyState === closedState) {
        setStatus('closed');
        void probeSession();
      }
    };

    for (const name of options.eventNames) {
      current.addEventListener(name, (message) => {
        if (message.lastEventId !== '') {
          lastEventId = message.lastEventId;
        }
        options.onEvent({ name, data: message.data, lastEventId: message.lastEventId });
      });
    }
  };

  const close = (): void => {
    const current = source;
    source = undefined;
    current?.close();
    if (status !== 'closed') {
      setStatus('closed');
    }
  };

  connect();

  return {
    lastEventId: () => lastEventId,
    status: () => status,
    reopen: () => {
      close();
      connect();
    },
    close,
  };
}
