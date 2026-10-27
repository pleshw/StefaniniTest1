import {
  findAllByRole,
  findByRole,
  findByText,
  fireEvent,
  getByRole,
  queryByRole,
  within,
} from '@testing-library/dom';
import { http, HttpResponse } from 'msw';
import { describe, expect, it, vi } from 'vitest';
import { server } from '../../mocks/server';
import type { DeadLetter } from '../../src/admin/api/deadLetters';
import { mountDeadLetters } from '../../src/admin/ui/deadLetters';
import { createHttpClient } from '../../src/shared/api/http';
import { expectAccessible } from '../helpers';

const letter = (extra: Partial<DeadLetter> = {}): DeadLetter => ({
  messageId: 'msg-1',
  messageType: 'Orders.Contracts.OrderPlacedV1',
  originalDestination: 'orders.worker.placed',
  exceptionType: 'System.InvalidOperationException',
  exceptionMessage: 'The order is already confirmed.',
  attempts: 6,
  payload: '{"orderId":"3f6c2a10"}',
  headers: { 'x-delivery-count': '6', traceparent: '00-abc' },
  ...extra,
});

function mount() {
  const container = document.createElement('div');
  document.body.append(container);
  mountDeadLetters(container, { http: createHttpClient({ sleep: () => Promise.resolve() }) });

  return container;
}

function useLetters(letters: DeadLetter[]): void {
  server.use(http.get('*/bff/admin/dead-letters', () => HttpResponse.json(letters)));
}

describe('dead letters', () => {
  it('lists each parked message with its type, destination, error and attempts', async () => {
    useLetters([
      letter(),
      letter({
        messageId: 'msg-2',
        attempts: null,
        exceptionType: null,
        exceptionMessage: null,
        messageType: null,
        originalDestination: null,
      }),
    ]);

    const container = mount();

    const rows = await findAllByRole(container, 'row');
    expect(rows).toHaveLength(3);
    expect(rows[1]?.textContent).toContain('msg-1');
    expect(rows[1]?.textContent).toContain('Orders.Contracts.OrderPlacedV1');
    expect(rows[1]?.textContent).toContain('orders.worker.placed');
    expect(rows[1]?.textContent).toContain('System.InvalidOperationException');
    expect(rows[1]?.textContent).toContain('The order is already confirmed.');
    expect(rows[1]?.textContent).toContain('6');
    expect(rows[2]?.textContent).toContain('msg-2');
    expect(rows[2]?.textContent).toContain('Unknown');
    expect(getByRole(container, 'status').textContent).toBe('2 messages in the dead-letter queue');
  });

  it('shows the payload and headers as plain text on request', async () => {
    useLetters([letter({ payload: '<img src=x onerror=alert(1)>' })]);
    const container = mount();

    const details = (await findByRole(container, 'group', { name: 'Details of msg-1' })).closest(
      'details',
    );
    expect(details?.textContent).toContain('<img src=x onerror=alert(1)>');
    expect(details?.querySelector('img')).toBeNull();
    expect(details?.textContent).toContain('x-delivery-count: 6');
    expect(details?.textContent).toContain('traceparent: 00-abc');
  });

  it('says the queue is empty', async () => {
    useLetters([]);

    const container = mount();

    await findByText(container, 'The dead-letter queue is empty.');
    expect(queryByRole(container, 'table')).toBeNull();
  });

  it('says when the list could not be read, and tries again', async () => {
    let fail = true;
    server.use(
      http.get('*/bff/admin/dead-letters', () =>
        fail
          ? HttpResponse.json(
              { title: 'Messaging unavailable', detail: 'The broker cannot be reached.' },
              { status: 503 },
            )
          : HttpResponse.json([letter()]),
      ),
    );
    const container = mount();

    const alert = await findByRole(container, 'alert');
    expect(alert.textContent).toContain('The broker cannot be reached.');
    fail = false;
    fireEvent.click(getByRole(alert, 'button', { name: 'Try again' }));

    expect(await findByRole(container, 'button', { name: 'Replay msg-1' })).toBeTruthy();
  });

  it('reads the queue again on Refresh', async () => {
    useLetters([letter()]);
    const container = mount();
    await findByRole(container, 'button', { name: 'Replay msg-1' });
    useLetters([letter({ messageId: 'msg-9' })]);

    fireEvent.click(getByRole(container, 'button', { name: 'Refresh' }));

    expect(await findByRole(container, 'button', { name: 'Replay msg-9' })).toBeTruthy();
  });
});

describe('replaying a dead letter', () => {
  it('asks first, then replays with X-CSRF, says where it went and reloads the queue', async () => {
    useLetters([letter({ messageId: 'msg/1 #a' })]);
    let request: Request | undefined;
    server.use(
      http.post('*/bff/admin/dead-letters/:id/replay', ({ request: incoming }) => {
        request = incoming.clone();
        return HttpResponse.json({
          letter: letter({ messageId: 'msg/1 #a' }),
          newMessageId: 'new-1',
          exchange: 'orders.events',
          routingKey: 'order.placed',
          replayedBy: 'admin-1',
        });
      }),
    );
    const container = mount();
    fireEvent.click(await findByRole(container, 'button', { name: 'Replay msg/1 #a' }));
    const dialog = getByRole(document.body, 'dialog', { name: 'Replay this message?' });
    expect(dialog.textContent).toContain('msg/1 #a');
    useLetters([]);

    fireEvent.click(within(dialog).getByRole('button', { name: 'Replay' }));

    await findByText(container, 'Replayed msg/1 #a as new-1 through orders.events (order.placed).');
    expect(new URL(request?.url ?? '').pathname).toBe(
      '/bff/admin/dead-letters/msg%2F1%20%23a/replay',
    );
    expect(request?.headers.get('x-csrf')).toBe('1');
    await findByText(container, 'The dead-letter queue is empty.');
  });

  it('sends nothing when the question is cancelled', async () => {
    useLetters([letter()]);
    let calls = 0;
    server.use(
      http.post('*/bff/admin/dead-letters/:id/replay', () => {
        calls++;
        return HttpResponse.json({});
      }),
    );
    const container = mount();
    fireEvent.click(await findByRole(container, 'button', { name: 'Replay msg-1' }));

    fireEvent.click(getByRole(document.body, 'button', { name: 'Cancel' }));
    await new Promise((resolve) => setTimeout(resolve, 20));

    expect(calls).toBe(0);
    expect(queryByRole(document.body, 'dialog')).toBeNull();
  });

  it.each([
    [404, 'No message has that id.'],
    [422, 'The message names no existing order.'],
    [503, 'The broker cannot be reached.'],
  ])('shows why a replay failed with %i, and reloads the queue', async (status, detail) => {
    let lists = 0;
    server.use(
      http.get('*/bff/admin/dead-letters', () => {
        lists++;
        return HttpResponse.json([letter()]);
      }),
      http.post('*/bff/admin/dead-letters/:id/replay', () =>
        HttpResponse.json({ title: 'Problem', detail }, { status }),
      ),
    );
    const container = mount();
    fireEvent.click(await findByRole(container, 'button', { name: 'Replay msg-1' }));

    fireEvent.click(getByRole(document.body, 'button', { name: 'Replay' }));

    await vi.waitFor(() => {
      expect(getByRole(container, 'alert').textContent).toContain(detail);
    });
    expect(lists).toBeGreaterThanOrEqual(2);
  });
});

describe('dead letters accessibility', () => {
  it('has no violations: the list, the open details, the empty queue and an error', async () => {
    useLetters([letter(), letter({ messageId: 'msg-2' })]);
    const full = mount();
    await findByRole(full, 'button', { name: 'Replay msg-1' });
    full.querySelector('details')?.setAttribute('open', '');
    await expectAccessible(full);
    full.remove();

    useLetters([]);
    const empty = mount();
    await findByText(empty, 'The dead-letter queue is empty.');
    await expectAccessible(empty);
    empty.remove();

    server.use(http.get('*/bff/admin/dead-letters', () => HttpResponse.json({}, { status: 503 })));
    const failed = mount();
    await findByRole(failed, 'alert');
    await expectAccessible(failed);
  });
});
