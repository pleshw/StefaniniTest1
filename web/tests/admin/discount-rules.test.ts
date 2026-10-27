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
  fireEvent.click(await findByRole(container, 'button', { name: 'Nova regra' }));

  return getByRole(container, 'form', { name: 'Editor de regra' });
}

const type = (form: HTMLElement, value: string): void => {
  fireEvent.change(getByLabelText(form, 'Tipo'), { target: { value } });
};
const fill = (form: HTMLElement, label: string, value: string): void => {
  fireEvent.input(getByLabelText(form, label), { target: { value } });
};
const save = (form: HTMLElement): void => {
  fireEvent.click(getByRole(form, 'button', { name: 'Salvar regra' }));
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
    expect(rows[1]?.textContent).toContain('Percentual');
    expect(rows[1]?.textContent).toContain('5');
    expect(rows[1]?.textContent).toContain('1 de out. de 2026');
    expect(rows[1]?.textContent).toContain('Ativa');
    expect(rows[1]?.textContent).toContain('2');
    expect(rows[2]?.textContent).toContain('Inativa');
    expect(rows[2]?.textContent).toContain('1 de dez. de 2026');
    expect(
      within(rows[2] as HTMLElement).queryByRole('button', { name: 'Desativar Old' }),
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
    await findByText(container, 'Não há regras de desconto.');

    fireEvent.click(getByLabelText(container, 'Somente ativas'));

    await vi.waitFor(() => {
      expect(asked).toEqual([null, 'true']);
    });
  });

  it('says there are none, and shows an error with a way to try again', async () => {
    useRules([]);
    const empty = mount();
    await findByText(empty, 'Não há regras de desconto.');
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
    fireEvent.click(getByRole(alert, 'button', { name: 'Tentar novamente' }));
    expect(await findByRole(failed, 'button', { name: 'Editar Ten off' })).toBeTruthy();
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

    fill(form, 'Nome', '  Ten off ');
    fill(form, 'Percentual', '10');
    fill(form, 'Prioridade', '5');
    fireEvent.click(getByLabelText(form, 'Uma vez por cliente'));
    useRules([rule({ version: 1 })]);
    save(form);

    await findByText(container, 'Regra "Ten off" salva como versão 1.');
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
    expect(queryByRole(container, 'form', { name: 'Editor de regra' })).toBeNull();
    expect(await findByRole(container, 'button', { name: 'Editar Ten off' })).toBeTruthy();
  });

  it.each([
    ['FixedAmount', ['Todo valor deve ser maior que 0.']],
    [
      'BuyXGetY',
      [
        'Compre deve ser um número inteiro de pelo menos 1.',
        'Leve deve ser um número inteiro de pelo menos 1.',
      ],
    ],
    [
      'Tiered',
      [
        'A moeda deve ser um código de três letras.',
        'O percentual deve ser maior que 0 e no máximo 100.',
      ],
    ],
    ['Percentage', ['O percentual deve ser maior que 0 e no máximo 100.']],
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
    fill(form, 'Nome', 'Rule');
    fill(form, 'Percentual', '0');
    type(form, kind);
    if (kind === 'Tiered') {
      fill(form, 'Moeda', '');
    }
    if (kind === 'BuyXGetY') {
      fill(form, 'Compre', '0');
      fill(form, 'Leve', '');
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

    expect(getByLabelText(form, 'Percentual')).toBeTruthy();
    expect(getByLabelText(form, 'Aplica-se a')).toBeTruthy();

    type(form, 'BuyXGetY');
    expect(getByLabelText(form, 'Compre')).toBeTruthy();
    expect(getByLabelText(form, 'Somente estes SKUs (opcional)')).toBeTruthy();
    expect(within(form).queryByLabelText('Percentual')).toBeNull();

    type(form, 'FixedAmount');
    expect(getAllByLabelText(form, 'Valor')).toHaveLength(1);
    fireEvent.click(getByRole(form, 'button', { name: 'Adicionar valor' }));
    expect(getAllByLabelText(form, 'Valor')).toHaveLength(2);
    fireEvent.click(getAllByLabelText(form, 'Valor')[1] as HTMLElement);
    fireEvent.click(getByRole(form, 'button', { name: 'Remover valor 2' }));
    expect(getAllByLabelText(form, 'Valor')).toHaveLength(1);
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
    fill(form, 'Nome', 'Fixed');
    type(form, 'FixedAmount');
    fill(form, 'Moeda', 'brl');
    fill(form, 'Valor', '15.5');
    fireEvent.click(getByRole(form, 'button', { name: 'Adicionar valor' }));
    fireEvent.input(getAllByLabelText(form, 'Moeda')[1] as HTMLElement, {
      target: { value: 'USD' },
    });
    fireEvent.input(getAllByLabelText(form, 'Valor')[1] as HTMLElement, {
      target: { value: '3' },
    });
    save(form);
    await findByText(container, 'Regra "Ten off" salva como versão 2.');

    form = await openNew(container);
    fill(form, 'Nome', 'Bogo');
    type(form, 'BuyXGetY');
    fill(form, 'Compre', '2');
    fill(form, 'Leve', '1');
    fill(form, 'Somente estes SKUs (opcional)', 'A-1, B-2');
    save(form);
    await vi.waitFor(() => {
      expect(bodies).toHaveLength(2);
    });

    form = await openNew(container);
    fill(form, 'Nome', 'Tiers');
    type(form, 'Tiered');
    fill(form, 'Moeda', 'brl');
    fill(form, 'Subtotal mínimo', '0');
    fill(form, 'Percentual', '5');
    fireEvent.click(getByRole(form, 'button', { name: 'Adicionar faixa' }));
    fireEvent.input(getAllByLabelText(form, 'Subtotal mínimo')[1] as HTMLElement, {
      target: { value: '500' },
    });
    fireEvent.input(getAllByLabelText(form, 'Percentual')[1] as HTMLElement, {
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
    fill(form, 'Nome', 'Cond');
    fill(form, 'Percentual', '5');

    fireEvent.change(getByLabelText(form, 'Tipo de condição'), {
      target: { value: 'MinSubtotal' },
    });
    fireEvent.click(getByRole(form, 'button', { name: 'Adicionar condição' }));
    fill(form, 'Subtotal mínimo: valor', '250');
    fireEvent.change(getByLabelText(form, 'Tipo de condição'), {
      target: { value: 'CategoryIn' },
    });
    fireEvent.click(getByRole(form, 'button', { name: 'Adicionar condição' }));
    fill(form, 'Categorias', 'Office, Kitchen');
    fireEvent.change(getByLabelText(form, 'Tipo de condição'), { target: { value: 'ChannelIs' } });
    fireEvent.click(getByRole(form, 'button', { name: 'Adicionar condição' }));
    fireEvent.change(getByLabelText(form, 'Canal é'), { target: { value: 'admin' } });
    fireEvent.change(getByLabelText(form, 'Tipo de condição'), {
      target: { value: 'FirstOrderOfClient' },
    });
    fireEvent.click(getByRole(form, 'button', { name: 'Adicionar condição' }));
    fireEvent.change(getByLabelText(form, 'Tipo de condição'), {
      target: { value: 'DateWindow' },
    });
    fireEvent.click(getByRole(form, 'button', { name: 'Adicionar condição' }));
    fill(form, 'Janela de datas: de', '2026-10-01T08:00');
    fireEvent.change(getByLabelText(form, 'Tipo de condição'), { target: { value: 'SkuIn' } });
    fireEvent.click(getByRole(form, 'button', { name: 'Adicionar condição' }));
    fill(form, 'SKUs', 'A-1');
    fireEvent.click(getByRole(form, 'button', { name: 'Remover condição SKU em' }));
    fireEvent.change(getByLabelText(form, 'Tipo de condição'), {
      target: { value: 'MinQuantity' },
    });
    fireEvent.click(getByRole(form, 'button', { name: 'Adicionar condição' }));
    fill(form, 'Quantidade mínima: unidades', '3');
    fill(form, 'Quantidade mínima: somente estes SKUs (opcional)', 'A-1');
    fireEvent.change(getByLabelText(form, 'Tipo de condição'), {
      target: { value: 'ClientSegment' },
    });
    fireEvent.click(getByRole(form, 'button', { name: 'Adicionar condição' }));
    fill(form, 'Segmentos', 'vip');
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
    fill(form, 'Nome', 'Cond');
    fill(form, 'Percentual', '5');
    fireEvent.click(getByRole(form, 'button', { name: 'Adicionar condição' }));

    save(form);

    expect(getByRole(form, 'alert').textContent).toContain(
      'Subtotal mínimo: o valor não pode ser negativo.',
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
    fireEvent.click(await findByRole(container, 'button', { name: 'Editar Ten off' }));
    const form = getByRole(container, 'form', { name: 'Editor de regra' });

    expect(getByLabelText<HTMLInputElement>(form, 'Nome').value).toBe('Ten off');
    expect(getByLabelText<HTMLInputElement>(form, 'Percentual').value).toBe('10');
    expect(getByLabelText<HTMLInputElement>(form, 'Prioridade').value).toBe('5');
    expect(getByLabelText<HTMLInputElement>(form, 'Uma vez por cliente').checked).toBe(true);
    expect(getByLabelText<HTMLInputElement>(form, 'Subtotal mínimo: valor').value).toBe('100');
    expect(within(form).getByRole('heading', { name: 'Editar Ten off (versão 2)' })).toBeTruthy();

    fill(form, 'Nome', 'Twelve off');
    fill(form, 'Percentual', '12');
    save(form);

    await findByText(container, 'Regra "Twelve off" salva como versão 3.');
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
    fireEvent.click(await findByRole(container, 'button', { name: 'Editar Ten off' }));
    const form = getByRole(container, 'form', { name: 'Editor de regra' });

    save(form);

    const alert = await findByRole(form, 'alert');
    expect(alert.textContent).toContain(
      'Esta regra foi alterada por outra pessoa. Recarregue a lista e abra-a novamente.',
    );
    expect(getByRole(form, 'button', { name: 'Salvar regra' })).toBeTruthy();
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
    fill(form, 'Nome', 'Rule');
    fill(form, 'Percentual', '5');

    save(form);

    expect((await findByRole(form, 'alert')).textContent).toContain(
      'parameters: percent must be greater than 0.',
    );
  });

  it('closes the editor on Cancel without sending anything', async () => {
    useRules([]);
    const container = mount();
    const form = await openNew(container);

    fireEvent.click(getByRole(form, 'button', { name: 'Cancelar' }));

    expect(queryByRole(container, 'form', { name: 'Editor de regra' })).toBeNull();
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
      fireEvent.click(await findByRole(container, 'button', { name: 'Nova regra' }));
    } else {
      fireEvent.click(await findByRole(container, 'button', { name: `Editar ${existing.name}` }));
    }

    return { container, form: getByRole(container, 'form', { name: 'Editor de regra' }) };
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
    fill(form, 'Percentual', '10');
    fill(form, 'SKU da simulação', 'DESK-1');
    fill(form, 'Quantidade da simulação', '2');

    fireEvent.click(getByRole(form, 'button', { name: 'Simular regra' }));

    const result = await findByRole(form, 'region', { name: 'Resultado da simulação' });
    expect(result.textContent).toContain('Subtotal');
    expect(result.textContent).toContain('R$ 200,00');
    expect(result.textContent).toContain('R$ 180,00');
    expect(result.textContent).toContain('Ten off (versão 3): -R$ 20,00');
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
    fill(form, 'Nome', 'Draft');
    fill(form, 'Percentual', '10');
    fill(form, 'SKU da simulação', 'DESK-1');

    fireEvent.click(getByRole(form, 'button', { name: 'Simular regra' }));

    expect(
      (await findByRole(form, 'region', { name: 'Resultado da simulação' })).textContent,
    ).toContain('Nenhuma regra se aplica a este carrinho.');
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
    fill(form, 'Nome', 'Draft');
    fill(form, 'Percentual', '10');
    fill(form, 'SKU da simulação', 'DESK-1');

    fireEvent.click(getByRole(form, 'button', { name: 'Simular regra' }));

    expect(
      (await findByRole(form, 'region', { name: 'Resultado da simulação' })).textContent,
    ).toContain('other-ru (versão 1): -R$ 5,00');
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

    fireEvent.click(getByRole(form, 'button', { name: 'Simular regra' }));
    expect(getByRole(form, 'alert').textContent).toContain('O nome é obrigatório.');

    fill(form, 'Nome', 'Draft');
    fill(form, 'Percentual', '10');
    fireEvent.click(getByRole(form, 'button', { name: 'Simular regra' }));
    expect(getByRole(form, 'alert').textContent).toContain(
      'Adicione ao carrinho de exemplo pelo menos um SKU com quantidade 1 ou mais.',
    );

    fireEvent.click(getByRole(form, 'button', { name: 'Adicionar item de exemplo' }));
    expect(getAllByLabelText(form, 'SKU da simulação')).toHaveLength(2);
    fireEvent.click(getByRole(form, 'button', { name: 'Remover item de exemplo 2' }));
    expect(getAllByLabelText(form, 'SKU da simulação')).toHaveLength(1);
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
    fill(form, 'Nome', 'Draft');
    fill(form, 'Percentual', '10');
    fill(form, 'SKU da simulação', 'NOPE');

    fireEvent.click(getByRole(form, 'button', { name: 'Simular regra' }));

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
    fireEvent.click(await findByRole(container, 'button', { name: 'Desativar Ten off' }));
    useRules([rule({ isActive: false, version: 3 })]);

    fireEvent.click(getByRole(document.body, 'button', { name: 'Desativar' }));

    await findByText(container, 'Regra "Ten off" desativada (versão 3).');
    expect(request?.headers.get('if-match')).toBe('"2"');
    expect(await findByText(container, 'Inativa')).toBeTruthy();
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
    fireEvent.click(await findByRole(container, 'button', { name: 'Desativar Ten off' }));
    fireEvent.click(getByRole(document.body, 'button', { name: 'Cancelar' }));
    await new Promise((resolve) => setTimeout(resolve, 20));
    expect(calls).toBe(0);

    fireEvent.click(getByRole(container, 'button', { name: 'Desativar Ten off' }));
    fireEvent.click(getByRole(document.body, 'button', { name: 'Desativar' }));

    expect((await findByRole(container, 'alert')).textContent).toContain(
      'Esta regra foi alterada por outra pessoa.',
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
    await findByRole(container, 'button', { name: 'Editar Ten off' });
    await expectAccessible(container);

    fireEvent.click(getByRole(container, 'button', { name: 'Editar Ten off' }));
    const form = getByRole(container, 'form', { name: 'Editor de regra' });
    for (const kind of ['FixedAmount', 'BuyXGetY', 'Tiered', 'Percentage']) {
      type(form, kind);
      await expectAccessible(form);
    }
    fill(form, 'Percentual', '10');
    fill(form, 'SKU da simulação', 'A');
    fireEvent.click(getByRole(form, 'button', { name: 'Simular regra' }));
    await findByRole(form, 'region', { name: 'Resultado da simulação' });
    await expectAccessible(form);
    for (const kind of ['SkuIn', 'MinQuantity', 'DateWindow', 'ChannelIs', 'FirstOrderOfClient']) {
      fireEvent.change(getByLabelText(form, 'Tipo de condição'), { target: { value: kind } });
      fireEvent.click(getByRole(form, 'button', { name: 'Adicionar condição' }));
    }
    await expectAccessible(form);
    fill(form, 'Percentual', '0');
    save(form);
    await expectAccessible(form);
  });
});
