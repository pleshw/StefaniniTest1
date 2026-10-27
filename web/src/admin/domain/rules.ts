import type { ConfirmationRule, DiscountRule } from '../api/rules';

export const discountTypes = ['Percentage', 'FixedAmount', 'BuyXGetY', 'Tiered'] as const;
export type DiscountType = (typeof discountTypes)[number];

export type AppliesTo = 'order' | 'line';
export type Channel = 'shop' | 'admin';

export interface PercentageParameters {
  percent: number;
  appliesTo: AppliesTo;
}
export interface FixedAmountParameters {
  amounts: { currency: string; amount: number }[];
}
export interface BuyXGetYParameters {
  buy: number;
  get: number;
  skus?: string[];
}
export interface TieredParameters {
  currency: string;
  tiers: { minSubtotal: number; percent: number }[];
}
export type DiscountParameters =
  PercentageParameters | FixedAmountParameters | BuyXGetYParameters | TieredParameters;

/** A condition of a rule; a rule matches when all of its conditions do. */
export type Condition =
  | { kind: 'MinSubtotal'; amount: number; currency: string }
  | { kind: 'SkuIn'; skus: string[] }
  | { kind: 'CategoryIn'; categories: string[] }
  | { kind: 'MinQuantity'; quantity: number; skus?: string[] }
  | { kind: 'ClientSegment'; segments: string[] }
  | { kind: 'DateWindow'; from?: string; to?: string }
  | { kind: 'TotalAbove'; amount: number; currency: string }
  | { kind: 'FirstOrderOfClient' }
  | { kind: 'ChannelIs'; channel: Channel };

export type ConditionKind = Condition['kind'];

/** The kinds a confirmation rule understands; discount rules understand all of them. */
export const confirmationConditionKinds: readonly ConditionKind[] = [
  'TotalAbove',
  'FirstOrderOfClient',
  'ChannelIs',
];
export const discountConditionKinds: readonly ConditionKind[] = [
  'MinSubtotal',
  'SkuIn',
  'CategoryIn',
  'MinQuantity',
  'ClientSegment',
  'DateWindow',
  'TotalAbove',
  'FirstOrderOfClient',
  'ChannelIs',
];

export const conditionLabels: Readonly<Record<ConditionKind, string>> = {
  MinSubtotal: 'Min subtotal',
  SkuIn: 'SKU in',
  CategoryIn: 'Category in',
  MinQuantity: 'Min quantity',
  ClientSegment: 'Client segment',
  DateWindow: 'Date window',
  TotalAbove: 'Total above',
  FirstOrderOfClient: 'First order of client',
  ChannelIs: 'Channel is',
};

export interface DiscountDefinition {
  name: string;
  type: DiscountType;
  parameters: DiscountParameters;
  conditions: Condition[];
  priority: number;
  stackable: boolean;
  oncePerClient: boolean;
  /** ISO 8601; empty means "from now". */
  validFrom?: string;
  validTo?: string;
  isActive: boolean;
}

export interface ConfirmationDefinition {
  name: string;
  action: 'Hold';
  conditions: Condition[];
  priority: number;
  isActive: boolean;
}

const percentMessage = 'Percent must be greater than 0 and at most 100.';
const currencyCode = /^[A-Za-z]{3}$/;

/** Splits "a, b\nc" into trimmed, non-empty, distinct values. */
export function splitList(text: string): string[] {
  return [
    ...new Set(
      text
        .split(/[,\n]/)
        .map((value) => value.trim())
        .filter((value) => value !== ''),
    ),
  ];
}

const validPercent = (percent: number): boolean => percent > 0 && percent <= 100;
const isCode = (code: string): boolean => currencyCode.test(code.trim());
const isWhole = (value: number, minimum: number): boolean =>
  Number.isInteger(value) && value >= minimum;

/** The same checks Orders makes, so most mistakes show before anything is sent; Orders still has the last word. */
export function validateDiscount(definition: DiscountDefinition): string[] {
  const errors: string[] = [];

  if (definition.name.trim() === '') {
    errors.push('Name is required.');
  }
  errors.push(...parameterErrors(definition));
  if (!Number.isInteger(definition.priority)) {
    errors.push('Priority must be a whole number.');
  }
  if (
    definition.validFrom !== undefined &&
    definition.validTo !== undefined &&
    new Date(definition.validTo) <= new Date(definition.validFrom)
  ) {
    errors.push('Valid to must be later than valid from.');
  }
  errors.push(
    ...definition.conditions.flatMap((condition) =>
      conditionErrors(condition, discountConditionKinds, 'discount'),
    ),
  );

  return errors;
}

export function validateConfirmation(definition: ConfirmationDefinition): string[] {
  const errors: string[] = [];

  if (definition.name.trim() === '') {
    errors.push('Name is required.');
  }
  if (!Number.isInteger(definition.priority)) {
    errors.push('Priority must be a whole number.');
  }
  if (definition.conditions.length === 0) {
    errors.push('Add at least one condition.');
  }
  errors.push(
    ...definition.conditions.flatMap((condition) =>
      conditionErrors(condition, confirmationConditionKinds, 'confirmation'),
    ),
  );

  return errors;
}

function parameterErrors(definition: DiscountDefinition): string[] {
  const parameters = definition.parameters;

  switch (definition.type) {
    case 'Percentage': {
      const { percent } = parameters as PercentageParameters;

      return validPercent(percent) ? [] : [percentMessage];
    }
    case 'FixedAmount': {
      const { amounts } = parameters as FixedAmountParameters;
      const errors: string[] = [];

      if (amounts.length === 0) {
        return ['Add at least one amount.'];
      }
      if (!amounts.every((item) => isCode(item.currency))) {
        errors.push('Every currency must be a three-letter code.');
      }
      if (!amounts.every((item) => item.amount > 0)) {
        errors.push('Every amount must be greater than 0.');
      }
      if (
        new Set(amounts.map((item) => item.currency.trim().toUpperCase())).size !== amounts.length
      ) {
        errors.push('Each currency can appear only once.');
      }

      return errors;
    }
    case 'BuyXGetY': {
      const { buy, get } = parameters as BuyXGetYParameters;

      return [
        ...(isWhole(buy, 1) ? [] : ['Buy must be a whole number of at least 1.']),
        ...(isWhole(get, 1) ? [] : ['Get must be a whole number of at least 1.']),
      ];
    }
    case 'Tiered': {
      const { currency, tiers } = parameters as TieredParameters;
      const errors: string[] = [];

      if (!isCode(currency)) {
        errors.push('Currency must be a three-letter code.');
      }
      if (tiers.length === 0) {
        errors.push('Add at least one band.');
      }
      if (!tiers.every((tier) => tier.minSubtotal >= 0)) {
        errors.push('Min subtotal must not be negative.');
      }
      if (!tiers.every((tier) => validPercent(tier.percent))) {
        errors.push(percentMessage);
      }
      if (new Set(tiers.map((tier) => tier.minSubtotal)).size !== tiers.length) {
        errors.push('Every band needs its own min subtotal.');
      }

      return errors;
    }
  }
}

function conditionErrors(
  condition: Condition,
  allowed: readonly ConditionKind[],
  rules: string,
): string[] {
  const label = conditionLabels[condition.kind];

  if (!allowed.includes(condition.kind)) {
    return [`${label} is not a condition of ${rules} rules.`];
  }

  switch (condition.kind) {
    case 'MinSubtotal':
    case 'TotalAbove':
      return [
        ...(condition.amount >= 0 ? [] : [`${label}: the amount must not be negative.`]),
        ...(isCode(condition.currency)
          ? []
          : [`${label}: the currency must be a three-letter code.`]),
      ];
    case 'SkuIn':
      return condition.skus.length > 0 ? [] : [`${label}: list at least one SKU.`];
    case 'CategoryIn':
      return condition.categories.length > 0 ? [] : [`${label}: list at least one category.`];
    case 'ClientSegment':
      return condition.segments.length > 0 ? [] : [`${label}: list at least one segment.`];
    case 'MinQuantity':
      return isWhole(condition.quantity, 1)
        ? []
        : [`${label}: the quantity must be a whole number of at least 1.`];
    case 'DateWindow':
      if (condition.from === undefined && condition.to === undefined) {
        return [`${label}: give a start, an end or both.`];
      }

      return condition.from !== undefined &&
        condition.to !== undefined &&
        new Date(condition.from) >= new Date(condition.to)
        ? [`${label}: the start must be earlier than the end.`]
        : [];
    case 'FirstOrderOfClient':
    case 'ChannelIs':
      return [];
  }
}

/** The body Orders expects: codes upper-cased, empty optional lists and dates left out. */
export function toDiscountRequest(definition: DiscountDefinition): Record<string, unknown> {
  return {
    name: definition.name.trim(),
    type: definition.type,
    parameters: parametersRequest(definition),
    conditions: definition.conditions.map(conditionRequest),
    priority: definition.priority,
    stackable: definition.stackable,
    oncePerClient: definition.oncePerClient,
    ...(definition.validFrom === undefined ? {} : { validFrom: definition.validFrom }),
    ...(definition.validTo === undefined ? {} : { validTo: definition.validTo }),
    isActive: definition.isActive,
  };
}

export function toConfirmationRequest(definition: ConfirmationDefinition): Record<string, unknown> {
  return {
    name: definition.name.trim(),
    action: definition.action,
    conditions: definition.conditions.map(conditionRequest),
    priority: definition.priority,
    isActive: definition.isActive,
  };
}

function parametersRequest(definition: DiscountDefinition): unknown {
  switch (definition.type) {
    case 'Percentage':
      return definition.parameters;
    case 'FixedAmount':
      return {
        amounts: (definition.parameters as FixedAmountParameters).amounts.map((item) => ({
          currency: item.currency.trim().toUpperCase(),
          amount: item.amount,
        })),
      };
    case 'BuyXGetY': {
      const { buy, get, skus } = definition.parameters as BuyXGetYParameters;

      return { buy, get, ...(skus === undefined || skus.length === 0 ? {} : { skus }) };
    }
    case 'Tiered': {
      const { currency, tiers } = definition.parameters as TieredParameters;

      return { currency: currency.trim().toUpperCase(), tiers };
    }
  }
}

function conditionRequest(condition: Condition): Record<string, unknown> {
  switch (condition.kind) {
    case 'MinSubtotal':
    case 'TotalAbove':
      return { ...condition, currency: condition.currency.trim().toUpperCase() };
    case 'MinQuantity':
      return {
        kind: condition.kind,
        quantity: condition.quantity,
        ...(condition.skus === undefined || condition.skus.length === 0
          ? {}
          : { skus: condition.skus }),
      };
    default:
      return { ...condition };
  }
}

/** Turns a stored rule back into the definition the editor shows. Shapes it does not know become empty ones. */
export function discountDefinitionOf(rule: DiscountRule): DiscountDefinition {
  const type = (discountTypes as readonly string[]).includes(rule.type)
    ? (rule.type as DiscountType)
    : 'Percentage';

  return {
    name: rule.name,
    type,
    parameters: parametersOf(type, rule.parameters),
    conditions: conditionsOf(rule.conditions),
    priority: rule.priority,
    stackable: rule.stackable,
    oncePerClient: rule.oncePerClient,
    validFrom: rule.validFrom,
    ...(rule.validTo === null ? {} : { validTo: rule.validTo }),
    isActive: rule.isActive,
  };
}

export function confirmationDefinitionOf(rule: ConfirmationRule): ConfirmationDefinition {
  return {
    name: rule.name,
    action: 'Hold',
    conditions: conditionsOf(rule.conditions),
    priority: rule.priority,
    isActive: rule.isActive,
  };
}

function fields(value: unknown): Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
    ? (value as Record<string, unknown>)
    : {};
}

const text = (value: unknown): string | undefined =>
  typeof value === 'string' ? value : undefined;
const num = (value: unknown): number => (typeof value === 'number' ? value : Number.NaN);
const list = (value: unknown): string[] | undefined =>
  Array.isArray(value) && value.every((item) => typeof item === 'string') ? value : undefined;

function parametersOf(type: DiscountType, value: unknown): DiscountParameters {
  const stored = fields(value);

  switch (type) {
    case 'Percentage':
      return {
        percent: typeof stored['percent'] === 'number' ? stored['percent'] : 0,
        appliesTo: stored['appliesTo'] === 'line' ? 'line' : 'order',
      };
    case 'FixedAmount':
      return {
        amounts: (Array.isArray(stored['amounts']) ? (stored['amounts'] as unknown[]) : []).map(
          (item) => ({
            currency: text(fields(item)['currency']) ?? '',
            amount: num(fields(item)['amount']),
          }),
        ),
      };
    case 'BuyXGetY': {
      const skus = list(stored['skus']);

      return {
        buy: num(stored['buy']),
        get: num(stored['get']),
        ...(skus === undefined ? {} : { skus }),
      };
    }
    case 'Tiered':
      return {
        currency: text(stored['currency']) ?? '',
        tiers: (Array.isArray(stored['tiers']) ? (stored['tiers'] as unknown[]) : []).map(
          (tier) => ({
            minSubtotal: num(fields(tier)['minSubtotal']),
            percent: num(fields(tier)['percent']),
          }),
        ),
      };
  }
}

function conditionsOf(value: unknown): Condition[] {
  return (Array.isArray(value) ? (value as unknown[]) : []).flatMap((item) => {
    const condition = conditionOf(fields(item));

    return condition === undefined ? [] : [condition];
  });
}

function conditionOf(stored: Record<string, unknown>): Condition | undefined {
  switch (stored['kind']) {
    case 'MinSubtotal':
    case 'TotalAbove':
      return {
        kind: stored['kind'],
        amount: num(stored['amount']),
        currency: text(stored['currency']) ?? '',
      };
    case 'SkuIn':
      return { kind: 'SkuIn', skus: list(stored['skus']) ?? [] };
    case 'CategoryIn':
      return { kind: 'CategoryIn', categories: list(stored['categories']) ?? [] };
    case 'ClientSegment':
      return { kind: 'ClientSegment', segments: list(stored['segments']) ?? [] };
    case 'MinQuantity': {
      const skus = list(stored['skus']);

      return {
        kind: 'MinQuantity',
        quantity: num(stored['quantity']),
        ...(skus === undefined ? {} : { skus }),
      };
    }
    case 'DateWindow': {
      const from = text(stored['from']);
      const to = text(stored['to']);

      return {
        kind: 'DateWindow',
        ...(from === undefined ? {} : { from }),
        ...(to === undefined ? {} : { to }),
      };
    }
    case 'FirstOrderOfClient':
      return { kind: 'FirstOrderOfClient' };
    case 'ChannelIs':
      return stored['channel'] === 'shop' || stored['channel'] === 'admin'
        ? { kind: 'ChannelIs', channel: stored['channel'] }
        : undefined;
    default:
      return undefined;
  }
}
