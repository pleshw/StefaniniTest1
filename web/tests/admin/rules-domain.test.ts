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
    expect(validateDiscount({ ...percentage, name: '  ' })).toEqual(['Name is required.']);
  });

  it.each([0, -1, 100.5, Number.NaN])('rejects the percent %s', (percent) => {
    expect(validateDiscount(withParameters('Percentage', { percent, appliesTo: 'order' }))).toEqual(
      ['Percent must be greater than 0 and at most 100.'],
    );
  });

  it('accepts exactly 100 percent', () => {
    expect(
      validateDiscount(withParameters('Percentage', { percent: 100, appliesTo: 'line' })),
    ).toEqual([]);
  });

  it('checks the fixed amounts: at least one, a currency code, a positive amount, one per currency', () => {
    expect(validateDiscount(withParameters('FixedAmount', { amounts: [] }))).toEqual([
      'Add at least one amount.',
    ]);
    expect(
      validateDiscount(
        withParameters('FixedAmount', { amounts: [{ currency: 'REAL', amount: 0 }] }),
      ),
    ).toEqual([
      'Every currency must be a three-letter code.',
      'Every amount must be greater than 0.',
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
    ).toEqual(['Each currency can appear only once.']);
  });

  it('checks buy and get', () => {
    expect(validateDiscount(withParameters('BuyXGetY', { buy: 0, get: Number.NaN }))).toEqual([
      'Buy must be a whole number of at least 1.',
      'Get must be a whole number of at least 1.',
    ]);
    expect(validateDiscount(withParameters('BuyXGetY', { buy: 1.5, get: 1 }))).toEqual([
      'Buy must be a whole number of at least 1.',
    ]);
  });

  it('checks the tiers: currency, at least one band, no negative minimum, a percent, one band per minimum', () => {
    expect(validateDiscount(withParameters('Tiered', { currency: 'X', tiers: [] }))).toEqual([
      'Currency must be a three-letter code.',
      'Add at least one band.',
    ]);
    expect(
      validateDiscount(
        withParameters('Tiered', { currency: 'BRL', tiers: [{ minSubtotal: -1, percent: 0 }] }),
      ),
    ).toEqual([
      'Min subtotal must not be negative.',
      'Percent must be greater than 0 and at most 100.',
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
    ).toEqual(['Every band needs its own min subtotal.']);
  });

  it('checks the priority and the validity window', () => {
    expect(validateDiscount({ ...percentage, priority: 1.5 })).toEqual([
      'Priority must be a whole number.',
    ]);
    expect(validateDiscount({ ...percentage, priority: Number.NaN })).toEqual([
      'Priority must be a whole number.',
    ]);
    expect(
      validateDiscount({
        ...percentage,
        validFrom: '2026-10-02T00:00:00.000Z',
        validTo: '2026-10-01T00:00:00.000Z',
      }),
    ).toEqual(['Valid to must be later than valid from.']);
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
        'Min subtotal: the amount must not be negative.',
        'Min subtotal: the currency must be a three-letter code.',
      ],
    ],
    [{ kind: 'SkuIn', skus: [] }, ['SKU in: list at least one SKU.']],
    [{ kind: 'CategoryIn', categories: [] }, ['Category in: list at least one category.']],
    [
      { kind: 'MinQuantity', quantity: 0 },
      ['Min quantity: the quantity must be a whole number of at least 1.'],
    ],
    [{ kind: 'ClientSegment', segments: [] }, ['Client segment: list at least one segment.']],
    [{ kind: 'DateWindow' }, ['Date window: give a start, an end or both.']],
    [
      { kind: 'DateWindow', from: '2026-10-02T00:00:00.000Z', to: '2026-10-01T00:00:00.000Z' },
      ['Date window: the start must be earlier than the end.'],
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
      'Name is required.',
      'Priority must be a whole number.',
      'Add at least one condition.',
    ]);
  });

  it('checks the amount and the currency of TotalAbove', () => {
    expect(
      validateConfirmation({
        ...hold,
        conditions: [{ kind: 'TotalAbove', amount: Number.NaN, currency: '' }],
      }),
    ).toEqual([
      'Total above: the amount must not be negative.',
      'Total above: the currency must be a three-letter code.',
    ]);
  });

  it('refuses the kinds of discount rules', () => {
    expect(validateConfirmation({ ...hold, conditions: [{ kind: 'SkuIn', skus: ['A'] }] })).toEqual(
      ['SKU in is not a condition of confirmation rules.'],
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
