import {
  findAllByRole,
  findByRole,
  findByText,
  fireEvent,
  getByLabelText,
  getByRole,
  queryByRole,
} from '@testing-library/dom';
import { http, HttpResponse } from 'msw';
import { describe, expect, it, vi } from 'vitest';
import { server } from '../../mocks/server';
import type { ConfirmationRule } from '../../src/admin/api/rules';
import { mountConfirmationRules } from '../../src/admin/ui/confirmationRules';
import { createHttpClient } from '../../src/shared/api/http';
import { expectAccessible } from '../helpers';

const rule = (extra: Partial<ConfirmationRule> = {}): ConfirmationRule => ({
  id: 'c1',
  version: 4,
  name: 'Big orders',
  action: 'Hold',
  conditions: [{ kind: 'TotalAbove', amount: 5000, currency: 'BRL' }],
  priority: 10,
  isActive: true,
  createdBy: 'admin',
  createdAt: '2026-10-01T12:00:00Z',
  ...extra,
});

function mount() {
  const container = document.createElement('div');
  document.body.append(container);
  mountConfirmationRules(container, { http: createHttpClient({ sleep: () => Promise.resolve() }) });

  return container;
}

function useRules(rules: ConfirmationRule[]): void {
  server.use(http.get('*/bff/admin/confirmation-rules', () => HttpResponse.json(rules)));
}

const fill = (form: HTMLElement, label: string, value: string): void => {
  fireEvent.input(getByLabelText(form, label), { target: { value } });
};
const save = (form: HTMLElement): void => {
  fireEvent.click(getByRole(form, 'button', { name: 'Save rule' }));
};

describe('confirmation rules', () => {
  it('lists the rules with their action, priority, version and state', async () => {
    useRules([rule(), rule({ id: 'c2', name: 'Off', isActive: false })]);

    const container = mount();

    const rows = await findAllByRole(container, 'row');
    expect(rows).toHaveLength(3);
    expect(rows[1]?.textContent).toContain('Big orders');
    expect(rows[1]?.textContent).toContain('Hold');
    expect(rows[1]?.textContent).toContain('10');
    expect(rows[1]?.textContent).toContain('4');
    expect(rows[1]?.textContent).toContain('Active');
    expect(rows[2]?.textContent).toContain('Inactive');
    expect(queryByRole(rows[2] as HTMLElement, 'button', { name: 'Deactivate Off' })).toBeNull();
  });

  it('says there are none, and shows an error with a way to try again', async () => {
    useRules([]);
    const empty = mount();
    await findByText(empty, 'There are no confirmation rules.');
    empty.remove();

    let fail = true;
    server.use(
      http.get('*/bff/admin/confirmation-rules', () =>
        fail
          ? HttpResponse.json({ title: 'Orders is unavailable' }, { status: 503 })
          : HttpResponse.json([rule()]),
      ),
    );
    const failed = mount();
    const alert = await findByRole(failed, 'alert');
    fail = false;
    fireEvent.click(getByRole(alert, 'button', { name: 'Try again' }));
    expect(await findByRole(failed, 'button', { name: 'Edit Big orders' })).toBeTruthy();
  });

  it('creates a rule with a TotalAbove condition and reports version 1', async () => {
    useRules([]);
    let body: unknown;
    server.use(
      http.post('*/bff/admin/confirmation-rules', async ({ request }) => {
        body = await request.json();
        return HttpResponse.json(rule({ version: 1 }), { status: 201 });
      }),
    );
    const container = mount();
    fireEvent.click(await findByRole(container, 'button', { name: 'New rule' }));
    const form = getByRole(container, 'form', { name: 'Confirmation rule editor' });

    fill(form, 'Name', 'Big orders');
    fill(form, 'Priority', '10');
    fireEvent.change(getByLabelText(form, 'Kind of condition'), {
      target: { value: 'TotalAbove' },
    });
    fireEvent.click(getByRole(form, 'button', { name: 'Add condition' }));
    fill(form, 'Total above: amount', '5000');
    fireEvent.change(getByLabelText(form, 'Kind of condition'), {
      target: { value: 'FirstOrderOfClient' },
    });
    fireEvent.click(getByRole(form, 'button', { name: 'Add condition' }));
    save(form);

    await findByText(container, 'Saved "Big orders" as version 1.');
    expect(body).toEqual({
      name: 'Big orders',
      action: 'Hold',
      conditions: [
        { kind: 'TotalAbove', amount: 5000, currency: 'BRL' },
        { kind: 'FirstOrderOfClient' },
      ],
      priority: 10,
      isActive: true,
    });
  });

  it('only offers the conditions a confirmation rule understands', async () => {
    useRules([]);
    const container = mount();
    fireEvent.click(await findByRole(container, 'button', { name: 'New rule' }));
    const form = getByRole(container, 'form', { name: 'Confirmation rule editor' });

    const kinds = [...getByLabelText<HTMLSelectElement>(form, 'Kind of condition').options].map(
      (option) => option.text,
    );

    expect(kinds).toEqual(['Total above', 'First order of client', 'Channel is']);
  });

  it('does not send a rule without a name or a condition', async () => {
    useRules([]);
    let calls = 0;
    server.use(
      http.post('*/bff/admin/confirmation-rules', () => {
        calls++;
        return HttpResponse.json(rule(), { status: 201 });
      }),
    );
    const container = mount();
    fireEvent.click(await findByRole(container, 'button', { name: 'New rule' }));
    const form = getByRole(container, 'form', { name: 'Confirmation rule editor' });

    save(form);

    const summary = getByRole(form, 'alert');
    expect([...summary.querySelectorAll('li')].map((item) => item.textContent)).toEqual([
      'Name is required.',
      'Add at least one condition.',
    ]);
    expect(calls).toBe(0);
  });

  it('edits a rule into a new version with If-Match set to the version it opened', async () => {
    useRules([rule()]);
    let request: Request | undefined;
    server.use(
      http.put('*/bff/admin/confirmation-rules/:id', ({ request: incoming }) => {
        request = incoming.clone();
        return HttpResponse.json(rule({ version: 5, name: 'Bigger orders' }));
      }),
    );
    const container = mount();
    fireEvent.click(await findByRole(container, 'button', { name: 'Edit Big orders' }));
    const form = getByRole(container, 'form', { name: 'Confirmation rule editor' });
    expect(getByLabelText<HTMLInputElement>(form, 'Total above: amount').value).toBe('5000');

    fill(form, 'Name', 'Bigger orders');
    save(form);

    await findByText(container, 'Saved "Bigger orders" as version 5.');
    expect(request?.headers.get('if-match')).toBe('"4"');
  });

  it('on a 412 says the rule was changed by someone else', async () => {
    useRules([rule()]);
    server.use(
      http.put('*/bff/admin/confirmation-rules/:id', () =>
        HttpResponse.json({ title: 'Precondition failed' }, { status: 412 }),
      ),
    );
    const container = mount();
    fireEvent.click(await findByRole(container, 'button', { name: 'Edit Big orders' }));
    const form = getByRole(container, 'form', { name: 'Confirmation rule editor' });

    save(form);

    expect((await findByRole(form, 'alert')).textContent).toContain(
      'This rule was changed by someone else.',
    );
  });

  it('closes the editor on Cancel', async () => {
    useRules([]);
    const container = mount();
    fireEvent.click(await findByRole(container, 'button', { name: 'New rule' }));

    fireEvent.click(getByRole(container, 'button', { name: 'Cancel' }));

    expect(queryByRole(container, 'form')).toBeNull();
  });

  it('deactivates after asking, with the version shown as If-Match', async () => {
    useRules([rule()]);
    let request: Request | undefined;
    server.use(
      http.post('*/bff/admin/confirmation-rules/:id/deactivate', ({ request: incoming }) => {
        request = incoming.clone();
        return HttpResponse.json(rule({ isActive: false, version: 5 }));
      }),
    );
    const container = mount();
    fireEvent.click(await findByRole(container, 'button', { name: 'Deactivate Big orders' }));
    useRules([rule({ isActive: false, version: 5 })]);
    fireEvent.click(getByRole(document.body, 'button', { name: 'Deactivate' }));

    await findByText(container, 'Deactivated "Big orders" (version 5).');
    expect(request?.headers.get('if-match')).toBe('"4"');
  });

  it('keeps the rule when the question is cancelled, and reports a failed deactivation', async () => {
    useRules([rule()]);
    let calls = 0;
    server.use(
      http.post('*/bff/admin/confirmation-rules/:id/deactivate', () => {
        calls++;
        return HttpResponse.json({ title: 'Precondition failed' }, { status: 412 });
      }),
    );
    const container = mount();
    fireEvent.click(await findByRole(container, 'button', { name: 'Deactivate Big orders' }));
    fireEvent.click(getByRole(document.body, 'button', { name: 'Cancel' }));
    await new Promise((resolve) => setTimeout(resolve, 20));
    expect(calls).toBe(0);

    fireEvent.click(getByRole(container, 'button', { name: 'Deactivate Big orders' }));
    fireEvent.click(getByRole(document.body, 'button', { name: 'Deactivate' }));

    await vi.waitFor(() => {
      expect(getByRole(container, 'alert').textContent).toContain(
        'This rule was changed by someone else.',
      );
    });
  });

  it('has no accessibility violations: the list, the editor and its errors', async () => {
    useRules([rule()]);
    const container = mount();
    await findByRole(container, 'button', { name: 'Edit Big orders' });
    await expectAccessible(container);

    fireEvent.click(getByRole(container, 'button', { name: 'New rule' }));
    const form = getByRole(container, 'form', { name: 'Confirmation rule editor' });
    fireEvent.change(getByLabelText(form, 'Kind of condition'), { target: { value: 'ChannelIs' } });
    fireEvent.click(getByRole(form, 'button', { name: 'Add condition' }));
    await expectAccessible(form);
    save(form);
    await expectAccessible(form);
  });
});
