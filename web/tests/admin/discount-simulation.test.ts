import {
  findByRole,
  fireEvent,
  getAllByLabelText,
  getByLabelText,
  getByRole,
  queryByRole,
} from '@testing-library/dom';
import { http, HttpResponse } from 'msw';
import { describe, expect, it, vi } from 'vitest';
import { server } from '../../mocks/server';
import type { DiscountRule } from '../../src/admin/api/rules';
import { discountDefinitionOf } from '../../src/admin/domain/rules';
import { mountDiscountSimulation } from '../../src/admin/ui/discountSimulation';
import { createHttpClient } from '../../src/shared/api/http';
import { expectAccessible } from '../helpers';

const rule = (extra: Partial<DiscountRule> = {}): DiscountRule => ({
  id: 'r1',
  version: 2,
  name: 'Ten off',
  type: 'Percentage',
  parameters: { percent: 10, appliesTo: 'order' },
  conditions: [],
  priority: 5,
  stackable: false,
  oncePerClient: false,
  validFrom: '2026-10-01T12:00:00Z',
  validTo: null,
  isActive: true,
  createdBy: 'admin',
  createdAt: '2026-10-01T12:00:00Z',
  ...extra,
});

const quote = {
  currency: 'BRL',
  itemCount: 2,
  subtotal: 200,
  discountTotal: 20,
  total: 180,
  items: [],
};

/** Answers the preview with `applied` and keeps each request body. */
function usePreview(applied: { ruleId: string; ruleVersion: number; amount: number }[]) {
  const bodies: Record<string, unknown>[] = [];
  server.use(
    http.post('*/bff/admin/discount-rules/preview', async ({ request }) => {
      bodies.push((await request.json()) as Record<string, unknown>);
      return HttpResponse.json({ quote, applied });
    }),
  );

  return bodies;
}

async function mount(rules: DiscountRule[], onBack: () => void = () => undefined) {
  server.use(http.get('*/bff/admin/discount-rules', () => HttpResponse.json(rules)));
  const container = document.createElement('div');
  document.body.append(container);
  const simulation = mountDiscountSimulation(container, {
    http: createHttpClient({ sleep: () => Promise.resolve() }),
    onBack,
  });
  const form = getByRole(container, 'form', { name: 'Simulação' });
  // The saved rules arrive after the mount; the picker lists them then.
  await vi.waitFor(() => {
    expect(getByLabelText(form, 'Regra a simular').querySelectorAll('option')).toHaveLength(
      rules.length + 1,
    );
  });

  return { container, form, simulation };
}

const fill = (form: HTMLElement, label: string, value: string): void => {
  fireEvent.input(getByLabelText(form, label), { target: { value } });
};
const pick = (form: HTMLElement, value: string): void => {
  fireEvent.change(getByLabelText(form, 'Regra a simular'), { target: { value } });
};
const simulate = (form: HTMLElement): void => {
  fireEvent.click(getByRole(form, 'button', { name: 'Simular' }));
};
/** The result's text, with the non-breaking spaces the money format uses read as plain ones. */
const resultOf = async (form: HTMLElement): Promise<string> =>
  (await findByRole(form, 'region', { name: 'Resultado da simulação' })).textContent.replace(
    /\u00a0/g,
    ' ',
  );

describe('discount simulation', () => {
  it('prices a sample cart with the active rules alone, and names the rules applied', async () => {
    const bodies = usePreview([{ ruleId: 'r1', ruleVersion: 2, amount: 20 }]);
    const { form } = await mount([rule()]);
    fill(form, 'SKU da simulação', 'DESK-1');
    fill(form, 'Quantidade da simulação', '2');

    simulate(form);

    const result = await resultOf(form);
    expect(result).toContain('R$ 200,00');
    expect(result).toContain('R$ 180,00');
    expect(result).toContain('Ten off (versão 2): -R$ 20,00');
    expect(result).not.toContain('se aplicou');
    expect(bodies[0]).toEqual({ currency: 'BRL', items: [{ sku: 'DESK-1', quantity: 2 }] });
  });

  it('adds a saved rule in place of its version, an inactive one as if active, and says whether it applied', async () => {
    const bodies = usePreview([{ ruleId: 'r2', ruleVersion: 4, amount: 20 }]);
    const { form } = await mount([
      rule(),
      rule({ id: 'r2', version: 4, name: 'Old', isActive: false }),
    ]);
    expect(getByLabelText(form, 'Regra a simular').textContent).toContain(
      'Old (versão 4, inativa)',
    );
    fill(form, 'SKU da simulação', 'DESK-1');

    pick(form, 'r2');
    simulate(form);

    expect(await resultOf(form)).toContain('"Old" se aplicou: -R$ 20,00.');
    expect(bodies[0]).toMatchObject({ ruleId: 'r2', rule: { name: 'Old', isActive: true } });

    pick(form, 'r1');
    simulate(form);

    await vi.waitFor(async () => {
      expect(await resultOf(form)).toContain('"Ten off" não se aplicou a este carrinho.');
    });
  });

  it("simulates the editor's draft of a saved rule, named by its form name, with a way back", async () => {
    const bodies = usePreview([{ ruleId: 'r1', ruleVersion: 3, amount: 20 }]);
    const onBack = vi.fn();
    const { container, form, simulation } = await mount([rule()], onBack);
    expect(queryByRole(container, 'button', { name: 'Voltar ao editor' })).toBeNull();

    simulation.simulate({
      definition: { ...discountDefinitionOf(rule()), name: 'Fifteen off' },
      existing: rule(),
    });
    fill(form, 'SKU da simulação', 'DESK-1');
    simulate(form);

    const result = await resultOf(form);
    expect(result).toContain('"Fifteen off" se aplicou: -R$ 20,00.');
    expect(result).toContain('Fifteen off (versão 3): -R$ 20,00');
    expect(bodies[0]).toMatchObject({ ruleId: 'r1', rule: { name: 'Fifteen off' } });
    expect(getByLabelText<HTMLSelectElement>(form, 'Regra a simular').value).toBe('draft');

    fireEvent.click(getByRole(container, 'button', { name: 'Voltar ao editor' }));
    expect(onBack).toHaveBeenCalledOnce();
  });

  it('names a new draft by its form name, since Orders prices it under an id of its own', async () => {
    const bodies = usePreview([
      { ruleId: 'made-up-id', ruleVersion: 1, amount: 5 },
      { ruleId: 'r1', ruleVersion: 2, amount: 15 },
    ]);
    const { form, simulation } = await mount([rule()]);

    simulation.simulate({ definition: { ...discountDefinitionOf(rule()), name: 'Draft' } });
    fill(form, 'SKU da simulação', 'DESK-1');
    simulate(form);

    const result = await resultOf(form);
    expect(result).toContain('"Draft" se aplicou: -R$ 5,00.');
    expect(result).toContain('Draft (versão 1): -R$ 5,00');
    expect(result).toContain('Ten off (versão 2): -R$ 15,00');
    expect(bodies[0]).not.toHaveProperty('ruleId');
  });

  it('shows a rule it does not know by its id, and says when no rule applies', async () => {
    usePreview([{ ruleId: 'other-rule-id', ruleVersion: 1, amount: 5 }]);
    const { form } = await mount([]);
    fill(form, 'SKU da simulação', 'DESK-1');
    simulate(form);
    expect(await resultOf(form)).toContain('other-ru (versão 1): -R$ 5,00');

    usePreview([]);
    simulate(form);
    await vi.waitFor(async () => {
      expect(await resultOf(form)).toContain('Nenhuma regra se aplica a este carrinho.');
    });
  });

  it('asks for a SKU first, and adds and removes items', async () => {
    const bodies = usePreview([]);
    const { form } = await mount([]);

    simulate(form);

    expect(getByRole(form, 'alert').textContent).toContain(
      'Adicione ao carrinho de exemplo pelo menos um SKU com quantidade 1 ou mais.',
    );
    fireEvent.click(getByRole(form, 'button', { name: 'Adicionar item de exemplo' }));
    expect(getAllByLabelText(form, 'SKU da simulação')).toHaveLength(2);
    fireEvent.click(getByRole(form, 'button', { name: 'Remover item de exemplo 2' }));
    expect(getAllByLabelText(form, 'SKU da simulação')).toHaveLength(1);
    expect(bodies).toHaveLength(0);
  });

  it("shows Orders' answer when the simulation fails, and still works without the saved rules", async () => {
    server.use(
      http.get('*/bff/admin/discount-rules', () => HttpResponse.json({}, { status: 503 })),
      http.post('*/bff/admin/discount-rules/preview', () =>
        HttpResponse.json(
          { title: 'Unprocessable', detail: 'SKU NOPE does not exist.' },
          { status: 422 },
        ),
      ),
    );
    const container = document.createElement('div');
    document.body.append(container);
    mountDiscountSimulation(container, {
      http: createHttpClient({ sleep: () => Promise.resolve() }),
      onBack: () => undefined,
    });
    const form = getByRole(container, 'form', { name: 'Simulação' });
    await vi.waitFor(() => {
      expect(container.textContent).toContain('Não foi possível carregar as regras salvas');
    });
    fill(form, 'SKU da simulação', 'NOPE');

    simulate(form);

    expect((await findByRole(form, 'alert')).textContent).toContain('SKU NOPE does not exist.');
  });

  it('has no accessibility violations, with a result', async () => {
    usePreview([{ ruleId: 'r1', ruleVersion: 2, amount: 20 }]);
    const { container, form } = await mount([rule()]);
    await expectAccessible(container);
    fill(form, 'SKU da simulação', 'DESK-1');
    pick(form, 'r1');

    simulate(form);
    await resultOf(form);

    await expectAccessible(container);
  });
});
