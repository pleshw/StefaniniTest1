import { describe, expect, it, vi } from 'vitest';
import { UnauthorizedError } from '../../../src/shared/api/errors';
import { openEventStream, type StreamEvent, type StreamStatus } from '../../../src/shared/api/sse';
import { FakeSource } from '../../fakeEventSource';

function open(http?: { get: () => Promise<unknown> }) {
  const sources: FakeSource[] = [];
  const events: StreamEvent[] = [];
  const statuses: StreamStatus[] = [];
  const stream = openEventStream({
    url: '/bff/admin/events/stream',
    eventNames: ['OrderPlaced.v1', 'heartbeat'],
    onEvent: (event) => events.push(event),
    onStatus: (status) => statuses.push(status),
    createSource: (url) => {
      const source = new FakeSource(url);
      sources.push(source);
      return source;
    },
    ...(http === undefined ? {} : { http: http as never }),
  });

  const at = (index: number): FakeSource => {
    const found = sources[index];
    if (found === undefined) {
      throw new Error(`No source ${String(index)}.`);
    }
    return found;
  };

  return { stream, sources, events, statuses, at, source: () => at(sources.length - 1) };
}

describe('event stream', () => {
  it('listens for each named event and passes on its name, data and id', () => {
    const { source, events, stream } = open();

    expect(source().url).toBe('/bff/admin/events/stream');
    expect(source().listening()).toEqual(['OrderPlaced.v1', 'heartbeat']);

    source().emit('OrderPlaced.v1', '{"id":1}', 'cursor-1');

    expect(events).toEqual([{ name: 'OrderPlaced.v1', data: '{"id":1}', lastEventId: 'cursor-1' }]);
    expect(stream.lastEventId()).toBe('cursor-1');
  });

  it('keeps the last id, and a heartbeat without an id does not erase it', () => {
    const { source, stream } = open();

    source().emit('OrderPlaced.v1', '{}', 'cursor-1');
    source().emit('OrderPlaced.v1', '{}', 'cursor-2');
    source().emit('heartbeat', '{}', '');

    expect(stream.lastEventId()).toBe('cursor-2');
  });

  it('starts without a last id', () => {
    expect(open().stream.lastEventId()).toBeUndefined();
  });

  it('reports open, then reconnecting while the browser retries by itself, without closing the source', () => {
    const { source, statuses, stream } = open();

    source().onopen?.(new Event('open'));
    source().readyState = FakeSource.connecting;
    source().onerror?.(new Event('error'));

    expect(statuses).toEqual(['connecting', 'open', 'reconnecting']);
    expect(stream.status()).toBe('reconnecting');
    expect(source().closeCalls).toBe(0);
  });

  it('reports closed when the browser gives up and keeps the last id to backfill from', async () => {
    const get = vi.fn(() => Promise.resolve({}));
    const { source, statuses, stream } = open({ get });
    source().emit('OrderPlaced.v1', '{}', 'cursor-9');

    source().readyState = FakeSource.closed;
    source().onerror?.(new Event('error'));

    expect(statuses.at(-1)).toBe('closed');
    expect(stream.lastEventId()).toBe('cursor-9');
    await vi.waitFor(() => {
      expect(get).toHaveBeenCalledExactlyOnceWith('/bff/user');
    });
  });

  it('asks who is signed in once it closes, so an expired session reaches the sign-in page', async () => {
    const get = vi.fn(() => Promise.reject(new UnauthorizedError(401, {})));
    const { source } = open({ get });

    source().readyState = FakeSource.closed;
    source().onerror?.(new Event('error'));

    await vi.waitFor(() => {
      expect(get).toHaveBeenCalledOnce();
    });
  });

  it('survives a failing session probe', async () => {
    const get = vi.fn(() => Promise.reject(new Error('offline')));
    const { source, stream } = open({ get });

    source().readyState = FakeSource.closed;
    source().onerror?.(new Event('error'));

    await vi.waitFor(() => {
      expect(get).toHaveBeenCalledOnce();
    });
    expect(stream.status()).toBe('closed');
  });

  it('closes without probing when no client is given', () => {
    const { source, stream } = open();

    source().readyState = FakeSource.closed;
    source().onerror?.(new Event('error'));

    expect(stream.status()).toBe('closed');
  });

  it('ignores an error on a source that was replaced and one that is neither retrying nor closed', () => {
    const { at, statuses, stream } = open();
    const first = at(0);
    stream.reopen();

    first.readyState = FakeSource.closed;
    first.onerror?.(new Event('error'));
    at(1).readyState = 1;
    at(1).onerror?.(new Event('error'));

    expect(statuses).toEqual(['connecting', 'closed', 'connecting']);
  });

  it('reopens with a new connection and keeps the last id for the caller to backfill', () => {
    const { sources, at, stream } = open();
    at(0).emit('OrderPlaced.v1', '{}', 'cursor-3');

    stream.reopen();

    expect(sources).toHaveLength(2);
    expect(at(0).closeCalls).toBe(1);
    expect(stream.lastEventId()).toBe('cursor-3');
    expect(stream.status()).toBe('connecting');
  });

  it('closes for good, once', () => {
    const { source, statuses, stream } = open();

    stream.close();
    stream.close();

    expect(source().closeCalls).toBe(1);
    expect(statuses).toEqual(['connecting', 'closed']);
  });
});
