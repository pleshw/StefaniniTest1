import { describe, expect, it } from 'vitest';
import type { OrderEvent } from '../../src/admin/api/events';
import { actionsFor, reasonRequired } from '../../src/admin/domain/actions';
import { createLiveFeed, rowOf, type FeedRow } from '../../src/admin/domain/feed';
import { orderStatuses } from '../../src/shared/domain/status';

const orderId = '3f6c2a10-1111-4222-8333-444455556666';

const placed = (id: number, extra: Record<string, unknown> = {}): OrderEvent => ({
  id,
  orderId,
  type: 'OrderPlaced.v1',
  occurredAt: '2026-10-26T12:00:00Z',
  cursor: `c${String(id)}`,
  payload: {
    orderId,
    number: 'ORD-2026-AAA',
    clientId: 'c1',
    channel: 'shop',
    currency: 'BRL',
    total: 1924.4,
    itemCount: 3,
    occurredAt: '2026-10-26T12:00:00Z',
    ...extra,
  },
});

const changed = (
  id: number,
  from: string,
  to: string,
  extra: Record<string, unknown> = {},
): OrderEvent => ({
  id,
  orderId,
  type: 'OrderStatusChanged.v1',
  occurredAt: '2026-10-26T12:05:00Z',
  cursor: `c${String(id)}`,
  payload: { orderId, from, to, reason: 'Checked', actor: 'system:worker', ...extra },
});

describe('rowOf', () => {
  it('describes a placed order: its number, Pending status and amount', () => {
    expect(rowOf(placed(1))).toEqual({
      id: 1,
      cursor: 'c1',
      type: 'OrderPlaced.v1',
      orderId,
      orderLabel: 'ORD-2026-AAA',
      status: 'Pending',
      occurredAt: '2026-10-26T12:00:00Z',
      summary: 'Placed from shop: R$1,924.40, 3 items',
    });
  });

  it('describes a status change with who did it and why', () => {
    const row = rowOf(changed(2, 'Pending', 'OnHold'));

    expect(row.status).toBe('OnHold');
    expect(row.from).toBe('Pending');
    expect(row.orderLabel).toBe('3f6c2a10');
    expect(row.summary).toBe('Pending → OnHold by system:worker: Checked');
  });

  it('leaves out the reason when there is none', () => {
    expect(rowOf(changed(2, 'Pending', 'Confirmed', { reason: null })).summary).toBe(
      'Pending → Confirmed by system:worker',
    );
  });

  it('describes a replayed dead letter and any other event by its type', () => {
    const replay: OrderEvent = {
      id: 3,
      orderId,
      type: 'DeadLetterReplayed.v1',
      occurredAt: '2026-10-26T12:06:00Z',
      payload: { replayedBy: 'admin-1' },
    };
    const unknown: OrderEvent = { ...replay, id: 4, type: 'Something.v9', payload: 'text' };

    expect(rowOf(replay).summary).toBe('Dead letter replayed by admin-1');
    expect(rowOf(replay).cursor).toBeUndefined();
    expect(rowOf({ ...replay, payload: {} }).summary).toBe('Dead letter replayed');
    expect(rowOf(unknown)).toMatchObject({ summary: '', orderLabel: '3f6c2a10' });
    expect(rowOf(unknown).status).toBeUndefined();
  });

  it('copes with a payload that lacks the fields', () => {
    const odd = rowOf({ ...placed(5), payload: {} });
    expect(odd.orderLabel).toBe('3f6c2a10');
    expect(odd.summary).toBe('Placed');
    expect(rowOf({ ...changed(6, 'a', 'b'), payload: null }).summary).toBe('');
  });
});

const row = (id: number, type = 'OrderPlaced.v1', status?: string): FeedRow => ({
  id,
  cursor: undefined,
  type,
  orderId,
  orderLabel: `o${String(id)}`,
  ...(status === undefined ? {} : { status }),
  occurredAt: '2026-10-26T12:00:00Z',
  summary: '',
});

describe('live feed', () => {
  it('shows the newest row first', () => {
    const feed = createLiveFeed();

    feed.push(row(1));
    feed.push(row(2));

    expect(feed.rows().map((r) => r.id)).toEqual([2, 1]);
  });

  it('shows each event once, however many times it arrives (stream and backfill)', () => {
    const feed = createLiveFeed();

    expect(feed.push(row(1))).toBe(true);
    expect(feed.push(row(1))).toBe(false);

    expect(feed.rows()).toHaveLength(1);
  });

  it('keeps at most the limit, dropping the oldest', () => {
    const feed = createLiveFeed({ limit: 3 });

    for (let id = 1; id <= 5; id++) {
      feed.push(row(id));
    }

    expect(feed.rows().map((r) => r.id)).toEqual([5, 4, 3]);
  });

  it('keeps 500 rows by default', () => {
    const feed = createLiveFeed();

    for (let id = 1; id <= 520; id++) {
      feed.push(row(id));
    }

    expect(feed.rows()).toHaveLength(500);
    expect(feed.rows()[0]?.id).toBe(520);
  });

  it('buffers while paused and adds the buffered rows on resume', () => {
    const feed = createLiveFeed();
    feed.push(row(1));

    feed.pause();
    feed.push(row(2));
    feed.push(row(3));

    expect(feed.paused()).toBe(true);
    expect(feed.pending()).toBe(2);
    expect(feed.rows().map((r) => r.id)).toEqual([1]);

    feed.resume();

    expect(feed.paused()).toBe(false);
    expect(feed.pending()).toBe(0);
    expect(feed.rows().map((r) => r.id)).toEqual([3, 2, 1]);
  });

  it('does not buffer a row twice, and keeps only the newest of the buffer past the limit', () => {
    const feed = createLiveFeed({ limit: 2 });
    feed.pause();

    feed.push(row(1));
    expect(feed.push(row(1))).toBe(false);
    feed.push(row(2));
    feed.push(row(3));

    expect(feed.pending()).toBe(2);
    feed.resume();
    expect(feed.rows().map((r) => r.id)).toEqual([3, 2]);
  });

  it('filters by type and by status without losing the rest', () => {
    const feed = createLiveFeed();
    feed.push(row(1, 'OrderPlaced.v1', 'Pending'));
    feed.push(row(2, 'OrderStatusChanged.v1', 'OnHold'));
    feed.push(row(3, 'OrderStatusChanged.v1', 'Confirmed'));
    feed.push(row(4, 'DeadLetterReplayed.v1'));

    expect(feed.rows({ type: 'OrderStatusChanged.v1' }).map((r) => r.id)).toEqual([3, 2]);
    expect(feed.rows({ status: 'OnHold' }).map((r) => r.id)).toEqual([2]);
    expect(
      feed.rows({ type: 'OrderStatusChanged.v1', status: 'Confirmed' }).map((r) => r.id),
    ).toEqual([3]);
    expect(feed.rows({}).map((r) => r.id)).toEqual([4, 3, 2, 1]);
  });

  it('forgets old ids, so memory stays bounded, but still ignores a recent repeat', () => {
    const feed = createLiveFeed({ limit: 2 });

    for (let id = 1; id <= 50; id++) {
      feed.push(row(id));
    }

    expect(feed.push(row(50))).toBe(false);
  });
});

describe('order actions', () => {
  it.each([
    ['Pending', ['confirm', 'hold', 'block']],
    ['OnHold', ['confirm', 'block']],
    ['Confirmed', ['cancel']],
    ['Blocked', []],
    ['Cancelled', []],
    ['Failed', []],
    ['Unheard', []],
  ])('lets an order that is %s %j', (status, actions) => {
    expect(actionsFor(status)).toEqual(actions);
  });

  it('knows every status', () => {
    for (const status of orderStatuses) {
      expect(Array.isArray(actionsFor(status))).toBe(true);
    }
  });

  it('asks for a reason to hold, block and cancel, not to confirm', () => {
    expect(reasonRequired('hold')).toBe(true);
    expect(reasonRequired('block')).toBe(true);
    expect(reasonRequired('cancel')).toBe(true);
    expect(reasonRequired('confirm')).toBe(false);
  });
});
