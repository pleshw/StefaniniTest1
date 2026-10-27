import { describe, expect, it } from 'vitest';
import {
  confirmationDefinitionOf,
  discountDefinitionOf,
  splitList,
  toConfirmationRequest,
  toDiscountRequest,
  validateConfirmation,
  validateDiscount,
  type Condition,
  type ConfirmationDefinition,
  type DiscountDefinition,
} from '../../src/admin/domain/rules';

const percentage: DiscountDefinition = {
  name: 'Ten off',
  type: 'Percentage',
  parameters: { percent: 10, appliesTo: 'order' },
  conditions: [],
  priority: 1,
  stackable: false,
  oncePerClient: false,
  isActive: true,
};

const withParameters = (
  type: DiscountDefinition['type'],
  parameters: DiscountDefinition['parameters'],
): DiscountDefinition => ({
  ...percentage,
  type,
  parameters,
});

describe('splitList', () => {
  it('splits on commas and new lines, trims, drops blanks and repeats', () => {
    expect(splitList(' a-1, b-2\nc-3 ,, \n a-1')).toEqual(['a-1', 'b-2', 'c-3']);
    expect(splitList('')).toEqual([]);
    expect(splitList('  ')).toEqual([]);
  });
});

describe('discount rule validation', () => {
  it('accepts a valid rule of each type', () => {
    expect(validateDiscount(percentage)).toEqual([]);
    expect(
      validateDiscount(
        withParameters('FixedAmount', {
          amounts: [
            { currency: 'BRL', amount: 5 },
            { currency: 'usd', amount: 1 },
          ],
        }),
      ),
    ).toEqual([]);
    expect(validateDiscount(withParameters('BuyXGetY', { buy: 2, get: 1, skus: ['A-1'] }))).toEqual(
      [],
    );
    expect(validateDiscount(withParameters('BuyXGetY', { buy: 2, get: 1 }))).toEqual([]);
    expect(
      validateDiscount(
        withParameters('Tiered', {
          currency: 'BRL',
          tiers: [
            { minSubtotal: 0, percent: 5 },
            { minSubtotal: 500, percent: 10 },
          ],
        }),
      ),
    ).toEqual([]);
  });

  it('asks for a name', () => {
    expect(validateDiscount({ ...percentage, name: '  ' })).toEqual(['O nome é obrigatório.']);
  });

  it.each([0, -1, 100.5, Number.NaN])('rejects the percent %s', (percent) => {
    expect(validateDiscount(withParameters('Percentage', { percent, appliesTo: 'order' }))).toEqual(
      ['O percentual deve ser maior que 0 e no máximo 100.'],
    );
  });

  it('accepts exactly 100 percent', () => {
    expect(
      validateDiscount(withParameters('Percentage', { percent: 100, appliesTo: 'line' })),
    ).toEqual([]);
  });

  it('checks the fixed amounts: at least one, a currency code, a positive amount, one per currency', () => {
    expect(validateDiscount(withParameters('FixedAmount', { amounts: [] }))).toEqual([
      'Adicione pelo menos um valor.',
    ]);
    expect(
      validateDiscount(
        withParameters('FixedAmount', { amounts: [{ currency: 'REAL', amount: 0 }] }),
      ),
    ).toEqual([
      'Toda moeda deve ser um código de três letras.',
      'Todo valor deve ser maior que 0.',
    ]);
    expect(
      validateDiscount(
        withParameters('FixedAmount', {
          amounts: [
            { currency: 'BRL', amount: 1 },
            { currency: ' brl', amount: 2 },
          ],
        }),
      ),
    ).toEqual(['Cada moeda pode aparecer só uma vez.']);
  });

  it('checks buy and get', () => {
    expect(validateDiscount(withParameters('BuyXGetY', { buy: 0, get: Number.NaN }))).toEqual([
      'Compre deve ser um número inteiro de pelo menos 1.',
      'Leve deve ser um número inteiro de pelo menos 1.',
    ]);
    expect(validateDiscount(withParameters('BuyXGetY', { buy: 1.5, get: 1 }))).toEqual([
      'Compre deve ser um número inteiro de pelo menos 1.',
    ]);
  });

  it('checks the tiers: currency, at least one band, no negative minimum, a percent, one band per minimum', () => {
    expect(validateDiscount(withParameters('Tiered', { currency: 'X', tiers: [] }))).toEqual([
      'A moeda deve ser um código de três letras.',
      'Adicione pelo menos uma faixa.',
    ]);
    expect(
      validateDiscount(
        withParameters('Tiered', { currency: 'BRL', tiers: [{ minSubtotal: -1, percent: 0 }] }),
      ),
    ).toEqual([
      'O subtotal mínimo não pode ser negativo.',
      'O percentual deve ser maior que 0 e no máximo 100.',
    ]);
    expect(
      validateDiscount(
        withParameters('Tiered', {
          currency: 'BRL',
          tiers: [
            { minSubtotal: 5, percent: 1 },
            { minSubtotal: 5, percent: 2 },
          ],
        }),
      ),
    ).toEqual(['Cada faixa precisa de um subtotal mínimo próprio.']);
  });

  it('checks the priority and the validity window', () => {
    expect(validateDiscount({ ...percentage, priority: 1.5 })).toEqual([
      'A prioridade deve ser um número inteiro.',
    ]);
    expect(validateDiscount({ ...percentage, priority: Number.NaN })).toEqual([
      'A prioridade deve ser um número inteiro.',
    ]);
    expect(
      validateDiscount({
        ...percentage,
        validFrom: '2026-10-02T00:00:00.000Z',
        validTo: '2026-10-01T00:00:00.000Z',
      }),
    ).toEqual(['O fim da validade deve ser posterior ao início.']);
    expect(
      validateDiscount({
        ...percentage,
        validFrom: '2026-10-01T00:00:00.000Z',
        validTo: '2026-10-02T00:00:00.000Z',
      }),
    ).toEqual([]);
  });

  it.each<[Condition, string[]]>([
    [
      { kind: 'MinSubtotal', amount: -1, currency: 'BR' },
      [
        'Subtotal mínimo: o valor não pode ser negativo.',
        'Subtotal mínimo: a moeda deve ser um código de três letras.',
      ],
    ],
    [{ kind: 'SkuIn', skus: [] }, ['SKU em: informe pelo menos um SKU.']],
    [{ kind: 'CategoryIn', categories: [] }, ['Categoria em: informe pelo menos uma categoria.']],
    [
      { kind: 'MinQuantity', quantity: 0 },
      ['Quantidade mínima: a quantidade deve ser um número inteiro de pelo menos 1.'],
    ],
    [{ kind: 'ClientSegment', segments: [] }, ['Segmento do cliente: informe pelo menos um segmento.']],
    [{ kind: 'DateWindow' }, ['Janela de datas: informe um início, um fim ou ambos.']],
    [
      { kind: 'DateWindow', from: '2026-10-02T00:00:00.000Z', to: '2026-10-01T00:00:00.000Z' },
      ['Janela de datas: o início deve ser anterior ao fim.'],
    ],
  ])('checks the condition %j', (condition, errors) => {
    expect(validateDiscount({ ...percentage, conditions: [condition] })).toEqual(errors);
  });

  it('accepts valid conditions of every discount kind', () => {
    const conditions: Condition[] = [
      { kind: 'MinSubtotal', amount: 100, currency: 'BRL' },
      { kind: 'SkuIn', skus: ['A-1'] },
      { kind: 'CategoryIn', categories: ['Office'] },
      { kind: 'MinQuantity', quantity: 3, skus: ['A-1'] },
      { kind: 'MinQuantity', quantity: 3 },
      { kind: 'ClientSegment', segments: ['vip'] },
      { kind: 'DateWindow', from: '2026-10-01T00:00:00.000Z' },
      { kind: 'DateWindow', to: '2026-10-01T00:00:00.000Z' },
    ];

    expect(validateDiscount({ ...percentage, conditions })).toEqual([]);
  });

  it('accepts the kinds that look at the order itself too', () => {
    const conditions: Condition[] = [
      { kind: 'TotalAbove', amount: 100, currency: 'BRL' },
      { kind: 'FirstOrderOfClient' },
      { kind: 'ChannelIs', channel: 'shop' },
    ];

    expect(validateDiscount({ ...percentage, conditions })).toEqual([]);
  });
});

describe('confirmation rule validation', () => {
  const hold: ConfirmationDefinition = {
    name: 'Big orders',
    action: 'Hold',
    conditions: [{ kind: 'TotalAbove', amount: 5000, currency: 'BRL' }],
    priority: 10,
    isActive: true,
  };

  it('accepts the three kinds', () => {
    expect(validateConfirmation(hold)).toEqual([]);
    expect(
      validateConfirmation({
        ...hold,
        conditions: [{ kind: 'FirstOrderOfClient' }, { kind: 'ChannelIs', channel: 'admin' }],
      }),
    ).toEqual([]);
  });

  it('needs a name, at least one condition and a whole priority', () => {
    expect(validateConfirmation({ ...hold, name: '', conditions: [], priority: 0.5 })).toEqual([
      'O nome é obrigatório.',
      'A prioridade deve ser um número inteiro.',
      'Adicione pelo menos uma condição.',
    ]);
  });

  it('checks the amount and the currency of TotalAbove', () => {
    expect(
      validateConfirmation({
        ...hold,
        conditions: [{ kind: 'TotalAbove', amount: Number.NaN, currency: '' }],
      }),
    ).toEqual([
      'Total acima de: o valor não pode ser negativo.',
      'Total acima de: a moeda deve ser um código de três letras.',
    ]);
  });

  it('refuses the kinds of discount rules', () => {
    expect(validateConfirmation({ ...hold, conditions: [{ kind: 'SkuIn', skus: ['A'] }] })).toEqual(
      ['SKU em não é uma condição de regras de confirmação.'],
    );
  });
});

describe('requests', () => {
  it('builds a discount request with the optional dates only when given', () => {
    expect(toDiscountRequest(percentage)).toEqual({
      name: 'Ten off',
      type: 'Percentage',
      parameters: { percent: 10, appliesTo: 'order' },
      conditions: [],
      priority: 1,
      stackable: false,
      oncePerClient: false,
      isActive: true,
    });
    expect(
      toDiscountRequest({
        ...percentage,
        name: '  Ten off ',
        validFrom: '2026-10-01T00:00:00.000Z',
        validTo: '2026-11-01T00:00:00.000Z',
      }),
    ).toMatchObject({
      name: 'Ten off',
      validFrom: '2026-10-01T00:00:00.000Z',
      validTo: '2026-11-01T00:00:00.000Z',
    });
  });

  it('normalises the codes and SKUs, and sends no empty SKU list', () => {
    expect(
      toDiscountRequest(
        withParameters('FixedAmount', { amounts: [{ currency: ' brl ', amount: 5 }] }),
      ).parameters,
    ).toEqual({ amounts: [{ currency: 'BRL', amount: 5 }] });
    expect(
      toDiscountRequest(withParameters('BuyXGetY', { buy: 2, get: 1, skus: [] })).parameters,
    ).toEqual({ buy: 2, get: 1 });
    expect(
      toDiscountRequest({
        ...percentage,
        conditions: [
          { kind: 'MinQuantity', quantity: 2, skus: [] },
          { kind: 'MinSubtotal', amount: 1, currency: 'usd' },
        ],
      }).conditions,
    ).toEqual([
      { kind: 'MinQuantity', quantity: 2 },
      { kind: 'MinSubtotal', amount: 1, currency: 'USD' },
    ]);
  });

  it('builds a confirmation request', () => {
    expect(
      toConfirmationRequest({
        name: ' Big ',
        action: 'Hold',
        conditions: [{ kind: 'TotalAbove', amount: 10, currency: 'brl' }],
        priority: 3,
        isActive: false,
      }),
    ).toEqual({
      name: 'Big',
      action: 'Hold',
      conditions: [{ kind: 'TotalAbove', amount: 10, currency: 'BRL' }],
      priority: 3,
      isActive: false,
    });
  });
});

describe('reading a stored rule back into a definition', () => {
  const stored = {
    id: 'r1',
    version: 3,
    name: 'Tiers',
    type: 'Tiered',
    parameters: { currency: 'BRL', tiers: [{ minSubtotal: 0, percent: 5 }] },
    conditions: [
      { kind: 'MinSubtotal', amount: 10, currency: 'BRL' },
      { kind: 'DateWindow', from: '2026-10-01T00:00:00+00:00' },
    ],
    priority: 4,
    stackable: true,
    oncePerClient: true,
    validFrom: '2026-10-01T00:00:00+00:00',
    validTo: null,
    isActive: false,
    createdBy: 'a',
    createdAt: '2026-10-01T00:00:00+00:00',
  };

  it('keeps every field, and turns a null end date into none', () => {
    const definition = discountDefinitionOf(stored);

    expect(definition).toEqual({
      name: 'Tiers',
      type: 'Tiered',
      parameters: { currency: 'BRL', tiers: [{ minSubtotal: 0, percent: 5 }] },
      conditions: [
        { kind: 'MinSubtotal', amount: 10, currency: 'BRL' },
        { kind: 'DateWindow', from: '2026-10-01T00:00:00+00:00' },
      ],
      priority: 4,
      stackable: true,
      oncePerClient: true,
      validFrom: '2026-10-01T00:00:00+00:00',
      isActive: false,
    });
  });

  it('reads every type of parameters and falls back to empty ones for a shape it does not know', () => {
    expect(
      discountDefinitionOf({
        ...stored,
        type: 'Percentage',
        parameters: { percent: 5, appliesTo: 'line' },
      }).parameters,
    ).toEqual({
      percent: 5,
      appliesTo: 'line',
    });
    expect(
      discountDefinitionOf({
        ...stored,
        type: 'FixedAmount',
        parameters: { amounts: [{ currency: 'BRL', amount: 2 }] },
      }).parameters,
    ).toEqual({
      amounts: [{ currency: 'BRL', amount: 2 }],
    });
    expect(
      discountDefinitionOf({
        ...stored,
        type: 'BuyXGetY',
        parameters: { buy: 2, get: 1, skus: ['A'] },
      }).parameters,
    ).toEqual({
      buy: 2,
      get: 1,
      skus: ['A'],
    });
    expect(
      discountDefinitionOf({ ...stored, type: 'BuyXGetY', parameters: { buy: 2, get: 1 } })
        .parameters,
    ).toEqual({ buy: 2, get: 1 });
    expect(
      discountDefinitionOf({ ...stored, type: 'Percentage', parameters: 'x' }).parameters,
    ).toEqual({ percent: 0, appliesTo: 'order' });
    expect(
      discountDefinitionOf({ ...stored, type: 'Tiered', parameters: null }).parameters,
    ).toEqual({ currency: '', tiers: [] });
    expect(
      discountDefinitionOf({ ...stored, type: 'FixedAmount', parameters: {} }).parameters,
    ).toEqual({ amounts: [] });
    expect(discountDefinitionOf({ ...stored, type: 'Mystery', parameters: {} }).type).toBe(
      'Percentage',
    );
  });

  it('reads every kind of condition and skips what it does not know', () => {
    const conditions = [
      { kind: 'SkuIn', skus: ['A'] },
      { kind: 'CategoryIn', categories: ['Office'] },
      { kind: 'MinQuantity', quantity: 2, skus: ['A'] },
      { kind: 'MinQuantity', quantity: 2 },
      { kind: 'ClientSegment', segments: ['vip'] },
      { kind: 'DateWindow', to: '2026-10-01T00:00:00+00:00' },
      { kind: 'TotalAbove', amount: 1, currency: 'BRL' },
      { kind: 'FirstOrderOfClient' },
      { kind: 'ChannelIs', channel: 'admin' },
      { kind: 'ChannelIs', channel: 'other' },
      { kind: 'Mystery' },
      'text',
      null,
    ];

    const definition = confirmationDefinitionOf({
      id: 'c1',
      version: 1,
      name: 'N',
      action: 'Hold',
      conditions,
      priority: 1,
      isActive: true,
      createdBy: 'a',
      createdAt: '2026-10-01T00:00:00+00:00',
    });

    expect(definition.conditions).toEqual([
      { kind: 'SkuIn', skus: ['A'] },
      { kind: 'CategoryIn', categories: ['Office'] },
      { kind: 'MinQuantity', quantity: 2, skus: ['A'] },
      { kind: 'MinQuantity', quantity: 2 },
      { kind: 'ClientSegment', segments: ['vip'] },
      { kind: 'DateWindow', to: '2026-10-01T00:00:00+00:00' },
      { kind: 'TotalAbove', amount: 1, currency: 'BRL' },
      { kind: 'FirstOrderOfClient' },
      { kind: 'ChannelIs', channel: 'admin' },
    ]);
    expect(definition).toMatchObject({ name: 'N', action: 'Hold', priority: 1, isActive: true });
  });

  it('treats conditions that are not a list as none', () => {
    expect(discountDefinitionOf({ ...stored, conditions: {} }).conditions).toEqual([]);
  });
});
