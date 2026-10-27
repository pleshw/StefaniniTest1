import {
  findAllByRole,
  findByRole,
  findByText,
  fireEvent,
  getAllByLabelText,
  getByLabelText,
  getByRole,
  queryByRole,
  within,
} from '@testing-library/dom';
import { http, HttpResponse } from 'msw';
import { describe, expect, it, vi } from 'vitest';
import { server } from '../../mocks/server';
import type { DiscountRule } from '../../src/admin/api/rules';
import { mountDiscountRules } from '../../src/admin/ui/discountRules';
import { createHttpClient } from '../../src/shared/api/http';
import { expectAccessible } from '../helpers';

const rule = (extra: Partial<DiscountRule> = {}): DiscountRule => ({
  id: 'r1',
  version: 2,
  name: 'Ten off',
  type: 'Percentage',
  parameters: { percent: 10, appliesTo: 'order' },
  conditions: [{ kind: 'MinSubtotal', amount: 100, currency: 'BRL' }],
  priority: 5,
  stackable: false,
  oncePerClient: true,
  validFrom: '2026-10-01T12:00:00Z',
  validTo: null,
  isActive: true,
  createdBy: 'admin',
  createdAt: '2026-10-01T12:00:00Z',
  ...extra,
});

function mount() {
  const container = document.createElement('div');
  document.body.append(container);
  mountDiscountRules(container, { http: createHttpClient({ sleep: () => Promise.resolve() }) });

  return container;
}

function useRules(rules: DiscountRule[]): void {
  server.use(http.get('*/bff/admin/discount-rules', () => HttpResponse.json(rules)));
}

async function openNew(container: HTMLElement): Promise<HTMLElement> {
  fireEvent.click(await findByRole(container, 'button', { name: 'New rule' }));

  return getByRole(container, 'form', { name: 'Rule editor' });
}

const type = (form: HTMLElement, value: string): void => {
  fireEvent.change(getByLabelText(form, 'Type'), { target: { value } });
};
const fill = (form: HTMLElement, label: string, value: string): void => {
  fireEvent.input(getByLabelText(form, label), { target: { value } });
};
const save = (form: HTMLElement): void => {
  fireEvent.click(getByRole(form, 'button', { name: 'Save rule' }));
};

describe('discount rules list', () => {
  it('lists each rule with its type, priority, validity, version and state', async () => {
    useRules([
      rule(),
      rule({ id: 'r2', name: 'Old', isActive: false, version: 1, validTo: '2026-12-01T12:00:00Z' }),
    ]);

    const container = mount();

    const rows = await findAllByRole(container, 'row');
    expect(rows).toHaveLength(3);
    expect(rows[1]?.textContent).toContain('Ten off');
    expect(rows[1]?.textContent).toContain('Percentage');
    expect(rows[1]?.textContent).toContain('5');
    expect(rows[1]?.textContent).toContain('Oct 1, 2026');
    expect(rows[1]?.textContent).toContain('Active');
    expect(rows[1]?.textContent).toContain('2');
    expect(rows[2]?.textContent).toContain('Inactive');
    expect(rows[2]?.textContent).toContain('Dec 1, 2026');
    expect(
      within(rows[2] as HTMLElement).queryByRole('button', { name: 'Deactivate Old' }),
    ).toBeNull();
  });

  it('asks only for the active rules when the box is ticked', async () => {
    const asked: (string | null)[] = [];
    server.use(
      http.get('*/bff/admin/discount-rules', ({ request }) => {
        asked.push(new URL(request.url).searchParams.get('active'));
        return HttpResponse.json([]);
      }),
    );
    const container = mount();
    await findByText(container, 'There are no discount rules.');

    fireEvent.click(getByLabelText(container, 'Active only'));

    await vi.waitFor(() => {
      expect(asked).toEqual([null, 'true']);
    });
  });

  it('says there are none, and shows an error with a way to try again', async () => {
    useRules([]);
    const empty = mount();
    await findByText(empty, 'There are no discount rules.');
    empty.remove();

    let fail = true;
    server.use(
      http.get('*/bff/admin/discount-rules', () =>
        fail
          ? HttpResponse.json({ title: 'Orders is unavailable' }, { status: 500 })
          : HttpResponse.json([rule()]),
      ),
    );
    const failed = mount();
    const alert = await findByRole(failed, 'alert');
    expect(alert.textContent).toContain('Orders is unavailable');
    fail = false;
    fireEvent.click(getByRole(alert, 'button', { name: 'Try again' }));
    expect(await findByRole(failed, 'button', { name: 'Edit Ten off' })).toBeTruthy();
  });
});

describe('discount rule editor', () => {
  it('creates a percentage rule: sends the definition and reports version 1', async () => {
    useRules([]);
    let body: unknown;
    server.use(
      http.post('*/bff/admin/discount-rules', async ({ request }) => {
        body = await request.json();
        return HttpResponse.json(rule({ version: 1 }), { status: 201 });
      }),
    );
    const container = mount();
    const form = await openNew(container);

    fill(form, 'Name', '  Ten off ');
    fill(form, 'Percent', '10');
    fill(form, 'Priority', '5');
    fireEvent.click(getByLabelText(form, 'Once per client'));
    useRules([rule({ version: 1 })]);
    save(form);

    await findByText(container, 'Saved "Ten off" as version 1.');
    expect(body).toEqual({
      name: 'Ten off',
      type: 'Percentage',
      parameters: { percent: 10, appliesTo: 'order' },
      conditions: [],
      priority: 5,
      stackable: false,
      oncePerClient: true,
      isActive: true,
    });
    expect(queryByRole(container, 'form', { name: 'Rule editor' })).toBeNull();
    expect(await findByRole(container, 'button', { name: 'Edit Ten off' })).toBeTruthy();
  });

  it.each([
    ['FixedAmount', ['Every amount must be greater than 0.']],
    [
      'BuyXGetY',
      ['Buy must be a whole number of at least 1.', 'Get must be a whole number of at least 1.'],
    ],
    [
      'Tiered',
      ['Currency must be a three-letter code.', 'Percent must be greater than 0 and at most 100.'],
    ],
    ['Percentage', ['Percent must be greater than 0 and at most 100.']],
  ])('does not send a %s rule that is not valid, and lists what to fix', async (kind, messages) => {
    useRules([]);
    let calls = 0;
    server.use(
      http.post('*/bff/admin/discount-rules', () => {
        calls++;
        return HttpResponse.json(rule(), { status: 201 });
      }),
    );
    const container = mount();
    const form = await openNew(container);
    fill(form, 'Name', 'Rule');
    fill(form, 'Percent', '0');
    type(form, kind);
    if (kind === 'Tiered') {
      fill(form, 'Currency', '');
    }
    if (kind === 'BuyXGetY') {
      fill(form, 'Buy', '0');
      fill(form, 'Get', '');
    }

    save(form);

    const summary = getByRole(form, 'alert');
    expect([...summary.querySelectorAll('li')].map((item) => item.textContent)).toEqual(messages);
    expect(document.activeElement).toBe(summary);
    expect(calls).toBe(0);
  });

  it('shows the fields of the type chosen, and none of the others', async () => {
    useRules([]);
    const container = mount();
    const form = await openNew(container);

    expect(getByLabelText(form, 'Percent')).toBeTruthy();
    expect(getByLabelText(form, 'Applies to')).toBeTruthy();

    type(form, 'BuyXGetY');
    expect(getByLabelText(form, 'Buy')).toBeTruthy();
    expect(getByLabelText(form, 'Only these SKUs (optional)')).toBeTruthy();
    expect(within(form).queryByLabelText('Percent')).toBeNull();

    type(form, 'FixedAmount');
    expect(getAllByLabelText(form, 'Amount')).toHaveLength(1);
    fireEvent.click(getByRole(form, 'button', { name: 'Add amount' }));
    expect(getAllByLabelText(form, 'Amount')).toHaveLength(2);
    fireEvent.click(getAllByLabelText(form, 'Amount')[1] as HTMLElement);
    fireEvent.click(getByRole(form, 'button', { name: 'Remove amount 2' }));
    expect(getAllByLabelText(form, 'Amount')).toHaveLength(1);
  });

  it('sends a fixed-amount rule, a buy-x-get-y rule and a tiered rule as Orders expects them', async () => {
    useRules([]);
    const bodies: { type: string; parameters: unknown }[] = [];
    server.use(
      http.post('*/bff/admin/discount-rules', async ({ request }) => {
        bodies.push((await request.json()) as (typeof bodies)[number]);
        return HttpResponse.json(rule(), { status: 201 });
      }),
    );
    const container = mount();

    let form = await openNew(container);
    fill(form, 'Name', 'Fixed');
    type(form, 'FixedAmount');
    fill(form, 'Currency', 'brl');
    fill(form, 'Amount', '15.5');
    fireEvent.click(getByRole(form, 'button', { name: 'Add amount' }));
    fireEvent.input(getAllByLabelText(form, 'Currency')[1] as HTMLElement, {
      target: { value: 'USD' },
    });
    fireEvent.input(getAllByLabelText(form, 'Amount')[1] as HTMLElement, {
      target: { value: '3' },
    });
    save(form);
    await findByText(container, 'Saved "Ten off" as version 2.');

    form = await openNew(container);
    fill(form, 'Name', 'Bogo');
    type(form, 'BuyXGetY');
    fill(form, 'Buy', '2');
    fill(form, 'Get', '1');
    fill(form, 'Only these SKUs (optional)', 'A-1, B-2');
    save(form);
    await vi.waitFor(() => {
      expect(bodies).toHaveLength(2);
    });

    form = await openNew(container);
    fill(form, 'Name', 'Tiers');
    type(form, 'Tiered');
    fill(form, 'Currency', 'brl');
    fill(form, 'Min subtotal', '0');
    fill(form, 'Percent', '5');
    fireEvent.click(getByRole(form, 'button', { name: 'Add band' }));
    fireEvent.input(getAllByLabelText(form, 'Min subtotal')[1] as HTMLElement, {
      target: { value: '500' },
    });
    fireEvent.input(getAllByLabelText(form, 'Percent')[1] as HTMLElement, {
      target: { value: '10' },
    });
    save(form);
    await vi.waitFor(() => {
      expect(bodies).toHaveLength(3);
    });

    expect(bodies.map((body) => body.parameters)).toEqual([
      {
        amounts: [
          { currency: 'BRL', amount: 15.5 },
          { currency: 'USD', amount: 3 },
        ],
      },
      { buy: 2, get: 1, skus: ['A-1', 'B-2'] },
      {
        currency: 'BRL',
        tiers: [
          { minSubtotal: 0, percent: 5 },
          { minSubtotal: 500, percent: 10 },
        ],
      },
    ]);
  });

  it('builds conditions: adds and removes rows, and sends them', async () => {
    useRules([]);
    let body: { conditions: unknown } | undefined;
    server.use(
      http.post('*/bff/admin/discount-rules', async ({ request }) => {
        body = (await request.json()) as typeof body;
        return HttpResponse.json(rule(), { status: 201 });
      }),
    );
    const container = mount();
    const form = await openNew(container);
    fill(form, 'Name', 'Cond');
    fill(form, 'Percent', '5');

    fireEvent.change(getByLabelText(form, 'Kind of condition'), {
      target: { value: 'MinSubtotal' },
    });
    fireEvent.click(getByRole(form, 'button', { name: 'Add condition' }));
    fill(form, 'Min subtotal: amount', '250');
    fireEvent.change(getByLabelText(form, 'Kind of condition'), {
      target: { value: 'CategoryIn' },
    });
    fireEvent.click(getByRole(form, 'button', { name: 'Add condition' }));
    fill(form, 'Categories', 'Office, Kitchen');
    fireEvent.change(getByLabelText(form, 'Kind of condition'), { target: { value: 'ChannelIs' } });
    fireEvent.click(getByRole(form, 'button', { name: 'Add condition' }));
    fireEvent.change(getByLabelText(form, 'Channel is'), { target: { value: 'admin' } });
    fireEvent.change(getByLabelText(form, 'Kind of condition'), {
      target: { value: 'FirstOrderOfClient' },
    });
    fireEvent.click(getByRole(form, 'button', { name: 'Add condition' }));
    fireEvent.change(getByLabelText(form, 'Kind of condition'), {
      target: { value: 'DateWindow' },
    });
    fireEvent.click(getByRole(form, 'button', { name: 'Add condition' }));
    fill(form, 'Date window: from', '2026-10-01T08:00');
    fireEvent.change(getByLabelText(form, 'Kind of condition'), { target: { value: 'SkuIn' } });
    fireEvent.click(getByRole(form, 'button', { name: 'Add condition' }));
    fill(form, 'SKUs', 'A-1');
    fireEvent.click(getByRole(form, 'button', { name: 'Remove condition SKU in' }));
    fireEvent.change(getByLabelText(form, 'Kind of condition'), {
      target: { value: 'MinQuantity' },
    });
    fireEvent.click(getByRole(form, 'button', { name: 'Add condition' }));
    fill(form, 'Min quantity: units', '3');
    fill(form, 'Min quantity: only these SKUs (optional)', 'A-1');
    fireEvent.change(getByLabelText(form, 'Kind of condition'), {
      target: { value: 'ClientSegment' },
    });
    fireEvent.click(getByRole(form, 'button', { name: 'Add condition' }));
    fill(form, 'Segments', 'vip');
    save(form);

    await vi.waitFor(() => {
      expect(body).toBeDefined();
    });
    expect(body?.conditions).toEqual([
      { kind: 'MinSubtotal', amount: 250, currency: 'BRL' },
      { kind: 'CategoryIn', categories: ['Office', 'Kitchen'] },
      { kind: 'ChannelIs', channel: 'admin' },
      { kind: 'FirstOrderOfClient' },
      { kind: 'DateWindow', from: new Date(2026, 9, 1, 8, 0).toISOString() },
      { kind: 'MinQuantity', quantity: 3, skus: ['A-1'] },
      { kind: 'ClientSegment', segments: ['vip'] },
    ]);
  });

  it('names what is wrong with a condition', async () => {
    useRules([]);
    const container = mount();
    const form = await openNew(container);
    fill(form, 'Name', 'Cond');
    fill(form, 'Percent', '5');
    fireEvent.click(getByRole(form, 'button', { name: 'Add condition' }));

    save(form);

    expect(getByRole(form, 'alert').textContent).toContain(
      'Min subtotal: the amount must not be negative.',
    );
  });

  it('edits a rule: shows its values and saves a new version with If-Match set to the version it opened', async () => {
    useRules([rule({ version: 2 })]);
    let request: Request | undefined;
    let body: Record<string, unknown> = {};
    server.use(
      http.put('*/bff/admin/discount-rules/:id', async ({ request: incoming }) => {
        request = incoming.clone();
        body = (await incoming.json()) as typeof body;
        return HttpResponse.json(rule({ version: 3, name: 'Twelve off' }));
      }),
    );
    const container = mount();
    fireEvent.click(await findByRole(container, 'button', { name: 'Edit Ten off' }));
    const form = getByRole(container, 'form', { name: 'Rule editor' });

    expect(getByLabelText<HTMLInputElement>(form, 'Name').value).toBe('Ten off');
    expect(getByLabelText<HTMLInputElement>(form, 'Percent').value).toBe('10');
    expect(getByLabelText<HTMLInputElement>(form, 'Priority').value).toBe('5');
    expect(getByLabelText<HTMLInputElement>(form, 'Once per client').checked).toBe(true);
    expect(getByLabelText<HTMLInputElement>(form, 'Min subtotal: amount').value).toBe('100');
    expect(within(form).getByRole('heading', { name: 'Edit Ten off (version 2)' })).toBeTruthy();

    fill(form, 'Name', 'Twelve off');
    fill(form, 'Percent', '12');
    save(form);

    await findByText(container, 'Saved "Twelve off" as version 3.');
    expect(request?.headers.get('if-match')).toBe('"2"');
    expect(request?.headers.get('x-csrf')).toBe('1');
    expect(body).toMatchObject({
      name: 'Twelve off',
      parameters: { percent: 12, appliesTo: 'order' },
    });
  });

  it('on a 412 says the rule was changed by someone else and offers to reload the list', async () => {
    useRules([rule()]);
    server.use(
      http.put('*/bff/admin/discount-rules/:id', () =>
        HttpResponse.json(
          { title: 'Precondition failed', detail: 'The rule is at version 3.' },
          { status: 412 },
        ),
      ),
    );
    const container = mount();
    fireEvent.click(await findByRole(container, 'button', { name: 'Edit Ten off' }));
    const form = getByRole(container, 'form', { name: 'Rule editor' });

    save(form);

    const alert = await findByRole(form, 'alert');
    expect(alert.textContent).toContain(
      'This rule was changed by someone else. Reload the list and open it again.',
    );
    expect(getByRole(form, 'button', { name: 'Save rule' })).toBeTruthy();
  });

  it("shows Orders' own messages when it refuses a rule", async () => {
    useRules([]);
    server.use(
      http.post('*/bff/admin/discount-rules', () =>
        HttpResponse.json(
          {
            title: 'Validation',
            errors: { parameters: ['parameters: percent must be greater than 0.'] },
          },
          { status: 400 },
        ),
      ),
    );
    const container = mount();
    const form = await openNew(container);
    fill(form, 'Name', 'Rule');
    fill(form, 'Percent', '5');

    save(form);

    expect((await findByRole(form, 'alert')).textContent).toContain(
      'parameters: percent must be greater than 0.',
    );
  });

  it('closes the editor on Cancel without sending anything', async () => {
    useRules([]);
    const container = mount();
    const form = await openNew(container);

    fireEvent.click(getByRole(form, 'button', { name: 'Cancel' }));

    expect(queryByRole(container, 'form', { name: 'Rule editor' })).toBeNull();
  });
});

describe('preview', () => {
  const quote = {
    currency: 'BRL',
    itemCount: 2,
    subtotal: 200,
    discountTotal: 20,
    total: 180,
    items: [],
  };

  async function openPreview(existing?: DiscountRule) {
    useRules(existing === undefined ? [] : [existing]);
    const container = mount();
    if (existing === undefined) {
      fireEvent.click(await findByRole(container, 'button', { name: 'New rule' }));
    } else {
      fireEvent.click(await findByRole(container, 'button', { name: `Edit ${existing.name}` }));
    }

    return { container, form: getByRole(container, 'form', { name: 'Rule editor' }) };
  }

  it('prices a sample cart with the rule being edited, and names the rules applied', async () => {
    let body: Record<string, unknown> = {};
    server.use(
      http.post('*/bff/admin/discount-rules/preview', async ({ request }) => {
        body = (await request.json()) as typeof body;
        return HttpResponse.json({
          quote,
          applied: [{ ruleId: 'r1', ruleVersion: 3, amount: 20 }],
        });
      }),
    );
    const { form } = await openPreview(rule());
    fill(form, 'Percent', '10');
    fill(form, 'Preview SKU', 'DESK-1');
    fill(form, 'Preview quantity', '2');

    fireEvent.click(getByRole(form, 'button', { name: 'Preview rule' }));

    const result = await findByRole(form, 'region', { name: 'Preview result' });
    expect(result.textContent).toContain('Subtotal');
    expect(result.textContent).toContain('R$200.00');
    expect(result.textContent).toContain('R$180.00');
    expect(result.textContent).toContain('Ten off (version 3): -R$20.00');
    expect(body).toMatchObject({
      currency: 'BRL',
      items: [{ sku: 'DESK-1', quantity: 2 }],
      ruleId: 'r1',
      rule: {
        name: 'Ten off',
        type: 'Percentage',
        parameters: { percent: 10, appliesTo: 'order' },
      },
    });
  });

  it('previews a new rule without a rule id, and says when no rule applies', async () => {
    let body: Record<string, unknown> = {};
    server.use(
      http.post('*/bff/admin/discount-rules/preview', async ({ request }) => {
        body = (await request.json()) as typeof body;
        return HttpResponse.json({
          quote: { ...quote, discountTotal: 0, total: 200 },
          applied: [],
        });
      }),
    );
    const { form } = await openPreview();
    fill(form, 'Name', 'Draft');
    fill(form, 'Percent', '10');
    fill(form, 'Preview SKU', 'DESK-1');

    fireEvent.click(getByRole(form, 'button', { name: 'Preview rule' }));

    expect((await findByRole(form, 'region', { name: 'Preview result' })).textContent).toContain(
      'No rule applies to this cart.',
    );
    expect(body).not.toHaveProperty('ruleId');
  });

  it('shows the rule being edited by its form name and an unknown rule by its id', async () => {
    server.use(
      http.post('*/bff/admin/discount-rules/preview', () =>
        HttpResponse.json({
          quote,
          applied: [{ ruleId: 'other-rule-id', ruleVersion: 1, amount: 5 }],
        }),
      ),
    );
    const { form } = await openPreview();
    fill(form, 'Name', 'Draft');
    fill(form, 'Percent', '10');
    fill(form, 'Preview SKU', 'DESK-1');

    fireEvent.click(getByRole(form, 'button', { name: 'Preview rule' }));

    expect((await findByRole(form, 'region', { name: 'Preview result' })).textContent).toContain(
      'other-ru (version 1): -R$5.00',
    );
  });

  it('asks for a SKU first, adds and removes items, and does not preview an invalid rule', async () => {
    let calls = 0;
    server.use(
      http.post('*/bff/admin/discount-rules/preview', () => {
        calls++;
        return HttpResponse.json({ quote, applied: [] });
      }),
    );
    const { form } = await openPreview();

    fireEvent.click(getByRole(form, 'button', { name: 'Preview rule' }));
    expect(getByRole(form, 'alert').textContent).toContain('Name is required.');

    fill(form, 'Name', 'Draft');
    fill(form, 'Percent', '10');
    fireEvent.click(getByRole(form, 'button', { name: 'Preview rule' }));
    expect(getByRole(form, 'alert').textContent).toContain(
      'Add at least one SKU with a quantity of 1 or more to the sample cart.',
    );

    fireEvent.click(getByRole(form, 'button', { name: 'Add sample item' }));
    expect(getAllByLabelText(form, 'Preview SKU')).toHaveLength(2);
    fireEvent.click(getByRole(form, 'button', { name: 'Remove sample item 2' }));
    expect(getAllByLabelText(form, 'Preview SKU')).toHaveLength(1);
    expect(calls).toBe(0);
  });

  it("shows Orders' answer when the preview fails", async () => {
    server.use(
      http.post('*/bff/admin/discount-rules/preview', () =>
        HttpResponse.json(
          { title: 'Unprocessable', detail: 'SKU NOPE does not exist.' },
          { status: 422 },
        ),
      ),
    );
    const { form } = await openPreview();
    fill(form, 'Name', 'Draft');
    fill(form, 'Percent', '10');
    fill(form, 'Preview SKU', 'NOPE');

    fireEvent.click(getByRole(form, 'button', { name: 'Preview rule' }));

    expect((await findByRole(form, 'alert')).textContent).toContain('SKU NOPE does not exist.');
  });
});

describe('deactivating a rule', () => {
  it('asks first, then deactivates the version it shows with If-Match, and reloads the list', async () => {
    useRules([rule()]);
    let request: Request | undefined;
    server.use(
      http.post('*/bff/admin/discount-rules/:id/deactivate', ({ request: incoming }) => {
        request = incoming.clone();
        return HttpResponse.json(rule({ isActive: false, version: 3 }));
      }),
    );
    const container = mount();
    fireEvent.click(await findByRole(container, 'button', { name: 'Deactivate Ten off' }));
    useRules([rule({ isActive: false, version: 3 })]);

    fireEvent.click(getByRole(document.body, 'button', { name: 'Deactivate' }));

    await findByText(container, 'Deactivated "Ten off" (version 3).');
    expect(request?.headers.get('if-match')).toBe('"2"');
    expect(await findByText(container, 'Inactive')).toBeTruthy();
  });

  it('does nothing when the question is cancelled, and reports a failure', async () => {
    useRules([rule()]);
    let calls = 0;
    server.use(
      http.post('*/bff/admin/discount-rules/:id/deactivate', () => {
        calls++;
        return HttpResponse.json({ title: 'Precondition failed' }, { status: 412 });
      }),
    );
    const container = mount();
    fireEvent.click(await findByRole(container, 'button', { name: 'Deactivate Ten off' }));
    fireEvent.click(getByRole(document.body, 'button', { name: 'Cancel' }));
    await new Promise((resolve) => setTimeout(resolve, 20));
    expect(calls).toBe(0);

    fireEvent.click(getByRole(container, 'button', { name: 'Deactivate Ten off' }));
    fireEvent.click(getByRole(document.body, 'button', { name: 'Deactivate' }));

    expect((await findByRole(container, 'alert')).textContent).toContain(
      'This rule was changed by someone else.',
    );
  });
});

describe('discount rules accessibility', () => {
  it('has no violations in the list, the editor of each type with conditions, and the preview', async () => {
    useRules([rule()]);
    server.use(
      http.post('*/bff/admin/discount-rules/preview', () =>
        HttpResponse.json({
          quote: {
            currency: 'BRL',
            itemCount: 1,
            subtotal: 10,
            discountTotal: 1,
            total: 9,
            items: [],
          },
          applied: [{ ruleId: 'r1', ruleVersion: 2, amount: 1 }],
        }),
      ),
    );
    const container = mount();
    await findByRole(container, 'button', { name: 'Edit Ten off' });
    await expectAccessible(container);

    fireEvent.click(getByRole(container, 'button', { name: 'Edit Ten off' }));
    const form = getByRole(container, 'form', { name: 'Rule editor' });
    for (const kind of ['FixedAmount', 'BuyXGetY', 'Tiered', 'Percentage']) {
      type(form, kind);
      await expectAccessible(form);
    }
    fill(form, 'Percent', '10');
    fill(form, 'Preview SKU', 'A');
    fireEvent.click(getByRole(form, 'button', { name: 'Preview rule' }));
    await findByRole(form, 'region', { name: 'Preview result' });
    await expectAccessible(form);
    for (const kind of ['SkuIn', 'MinQuantity', 'DateWindow', 'ChannelIs', 'FirstOrderOfClient']) {
      fireEvent.change(getByLabelText(form, 'Kind of condition'), { target: { value: kind } });
      fireEvent.click(getByRole(form, 'button', { name: 'Add condition' }));
    }
    await expectAccessible(form);
    fill(form, 'Percent', '0');
    save(form);
    await expectAccessible(form);
  });
});
