import type { ConfirmationRule, DiscountRule } from '../api/rules';

export const discountTypes = ['Percentage', 'FixedAmount', 'BuyXGetY', 'Tiered'] as const;
export type DiscountType = (typeof discountTypes)[number];

const discountTypeLabels: Readonly<Record<DiscountType, string>> = {
  Percentage: 'Percentual',
  FixedAmount: 'Valor fixo',
  BuyXGetY: 'Compre X, leve Y',
  Tiered: 'Por faixas',
};

/** The name shown for a discount type; a type Orders adds later shows under its own name. */
export function discountTypeLabel(type: string): string {
  return (discountTypes as readonly string[]).includes(type)
    ? discountTypeLabels[type as DiscountType]
    : type;
}

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
  MinSubtotal: 'Subtotal mínimo',
  SkuIn: 'SKU em',
  CategoryIn: 'Categoria em',
  MinQuantity: 'Quantidade mínima',
  ClientSegment: 'Segmento do cliente',
  DateWindow: 'Janela de datas',
  TotalAbove: 'Total acima de',
  FirstOrderOfClient: 'Primeiro pedido do cliente',
  ChannelIs: 'Canal é',
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

const percentMessage = 'O percentual deve ser maior que 0 e no máximo 100.';
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
    errors.push('O nome é obrigatório.');
  }
  errors.push(...parameterErrors(definition));
  if (!Number.isInteger(definition.priority)) {
    errors.push('A prioridade deve ser um número inteiro.');
  }
  if (
    definition.validFrom !== undefined &&
    definition.validTo !== undefined &&
    new Date(definition.validTo) <= new Date(definition.validFrom)
  ) {
    errors.push('O fim da validade deve ser posterior ao início.');
  }
  errors.push(
    ...definition.conditions.flatMap((condition) =>
      conditionErrors(condition, discountConditionKinds, 'desconto'),
    ),
  );

  return errors;
}

export function validateConfirmation(definition: ConfirmationDefinition): string[] {
  const errors: string[] = [];

  if (definition.name.trim() === '') {
    errors.push('O nome é obrigatório.');
  }
  if (!Number.isInteger(definition.priority)) {
    errors.push('A prioridade deve ser um número inteiro.');
  }
  if (definition.conditions.length === 0) {
    errors.push('Adicione pelo menos uma condição.');
  }
  errors.push(
    ...definition.conditions.flatMap((condition) =>
      conditionErrors(condition, confirmationConditionKinds, 'confirmação'),
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
        return ['Adicione pelo menos um valor.'];
      }
      if (!amounts.every((item) => isCode(item.currency))) {
        errors.push('Toda moeda deve ser um código de três letras.');
      }
      if (!amounts.every((item) => item.amount > 0)) {
        errors.push('Todo valor deve ser maior que 0.');
      }
      if (
        new Set(amounts.map((item) => item.currency.trim().toUpperCase())).size !== amounts.length
      ) {
        errors.push('Cada moeda pode aparecer só uma vez.');
      }

      return errors;
    }
    case 'BuyXGetY': {
      const { buy, get } = parameters as BuyXGetYParameters;

      return [
        ...(isWhole(buy, 1) ? [] : ['Compre deve ser um número inteiro de pelo menos 1.']),
        ...(isWhole(get, 1) ? [] : ['Leve deve ser um número inteiro de pelo menos 1.']),
      ];
    }
    case 'Tiered': {
      const { currency, tiers } = parameters as TieredParameters;
      const errors: string[] = [];

      if (!isCode(currency)) {
        errors.push('A moeda deve ser um código de três letras.');
      }
      if (tiers.length === 0) {
        errors.push('Adicione pelo menos uma faixa.');
      }
      if (!tiers.every((tier) => tier.minSubtotal >= 0)) {
        errors.push('O subtotal mínimo não pode ser negativo.');
      }
      if (!tiers.every((tier) => validPercent(tier.percent))) {
        errors.push(percentMessage);
      }
      if (new Set(tiers.map((tier) => tier.minSubtotal)).size !== tiers.length) {
        errors.push('Cada faixa precisa de um subtotal mínimo próprio.');
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
    return [`${label} não é uma condição de regras de ${rules}.`];
  }

  switch (condition.kind) {
    case 'MinSubtotal':
    case 'TotalAbove':
      return [
        ...(condition.amount >= 0 ? [] : [`${label}: o valor não pode ser negativo.`]),
        ...(isCode(condition.currency)
          ? []
          : [`${label}: a moeda deve ser um código de três letras.`]),
      ];
    case 'SkuIn':
      return condition.skus.length > 0 ? [] : [`${label}: informe pelo menos um SKU.`];
    case 'CategoryIn':
      return condition.categories.length > 0 ? [] : [`${label}: informe pelo menos uma categoria.`];
    case 'ClientSegment':
      return condition.segments.length > 0 ? [] : [`${label}: informe pelo menos um segmento.`];
    case 'MinQuantity':
      return isWhole(condition.quantity, 1)
        ? []
        : [`${label}: a quantidade deve ser um número inteiro de pelo menos 1.`];
    case 'DateWindow':
      if (condition.from === undefined && condition.to === undefined) {
        return [`${label}: informe um início, um fim ou ambos.`];
      }

      return condition.from !== undefined &&
        condition.to !== undefined &&
        new Date(condition.from) >= new Date(condition.to)
        ? [`${label}: o início deve ser anterior ao fim.`]
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
