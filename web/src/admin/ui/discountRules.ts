import {
  createDiscountRule,
  deactivateDiscountRule,
  listDiscountRules,
  previewDiscountRule,
  replaceDiscountRule,
  type DiscountRule,
  type RulePreview,
} from '../api/rules';
import {
  discountConditionKinds,
  discountDefinitionOf,
  discountTypes,
  splitList,
  toDiscountRequest,
  validateDiscount,
  type AppliesTo,
  type BuyXGetYParameters,
  type DiscountDefinition,
  type DiscountParameters,
  type DiscountType,
  type FixedAmountParameters,
  type PercentageParameters,
  type TieredParameters,
  discountTypeLabel,
} from '../domain/rules';
import { createConditionsEditor, type ConditionsEditor } from './conditionsEditor';
import { PreconditionFailedError } from '../../shared/api/errors';
import type { HttpClient } from '../../shared/api/http';
import { formatDateTime, fromLocalInput, toLocalInput } from '../../shared/domain/dates';
import { formatMoney } from '../../shared/domain/money';
import { confirmAction } from '../../shared/ui/dialog';
import { h } from '../../shared/ui/dom';
import { errorMessage } from '../../shared/ui/errors';
import {
  actionButton,
  checkbox,
  dateTimeInput,
  errorSummary,
  labelled,
  numberInput,
  selectOf,
  textArea,
  textInput,
} from '../../shared/ui/form';

export interface DiscountRulesOptions {
  readonly http: HttpClient;
}

interface Parameters {
  readonly element: HTMLElement;
  readonly read: () => DiscountParameters;
}

const staleRule =
  'Esta regra foi alterada por outra pessoa. Recarregue a lista e abra-a novamente.';

/** Discount rules: the list, an editor with a form for each type of strategy, a conditions builder and a preview. */
export function mountDiscountRules(container: HTMLElement, options: DiscountRulesOptions): void {
  let rules: readonly DiscountRule[] = [];
  let loaded = false;
  let failure: string | undefined;
  let activeOnly = false;
  let generation = 0;

  const status = h('p', { text: '', attrs: { role: 'status' } });
  const alertBox = h('div', {
    className: 'alert alert-danger d-flex align-items-center gap-3',
    attrs: { role: 'alert' },
  });
  const activeFilter = checkbox('Somente ativas', false);
  const create = h('button', {
    className: 'btn btn-primary btn-sm',
    text: 'Nova regra',
    attrs: { type: 'button' },
  });
  const body = h('tbody');
  const table = h(
    'table',
    { className: 'table align-middle' },
    h('caption', { className: 'visually-hidden', text: 'Regras de desconto' }),
    h(
      'thead',
      {},
      h(
        'tr',
        {},
        ...['Nome', 'Tipo', 'Prioridade', 'Validade', 'Versão', 'Situação', 'Ações'].map((name) =>
          h('th', { text: name, attrs: { scope: 'col' } }),
        ),
      ),
    ),
    body,
  );
  const empty = h('p', { text: 'Não há regras de desconto.' });
  const editor = h('div');

  container.append(
    h(
      'section',
      { attrs: { 'aria-labelledby': 'discount-heading' } },
      h('h2', { className: 'h4', text: 'Regras de desconto', attrs: { id: 'discount-heading' } }),
      h(
        'div',
        { className: 'd-flex flex-wrap align-items-center gap-3 mb-2' },
        create,
        activeFilter.element,
      ),
      status,
      alertBox,
      empty,
      table,
      editor,
    ),
  );

  function render(): void {
    body.replaceChildren(...rules.map(rowOf));
    table.hidden = rules.length === 0;
    empty.hidden = !loaded || failure !== undefined || rules.length > 0;
    alertBox.hidden = failure === undefined;
    alertBox.replaceChildren(
      ...(failure === undefined
        ? []
        : [
            h('span', { text: failure }),
            actionButton(
              'Tentar novamente',
              'btn btn-sm btn-outline-danger ms-auto',
              () => void load(),
            ),
          ]),
    );
  }

  function rowOf(rule: DiscountRule): HTMLElement {
    const edit = actionButton('Editar', 'btn btn-outline-primary btn-sm', () => {
      openEditor(rule);
    });
    edit.setAttribute('aria-label', `Editar ${rule.name}`);
    const actions = h('td', { className: 'd-flex gap-2' }, edit);

    if (rule.isActive) {
      const deactivate = actionButton(
        'Desativar',
        'btn btn-outline-danger btn-sm',
        () => void deactivateRule(rule),
      );
      deactivate.setAttribute('aria-label', `Desativar ${rule.name}`);
      actions.append(deactivate);
    }

    return h(
      'tr',
      {},
      h('th', { text: rule.name, attrs: { scope: 'row' } }),
      h('td', { text: discountTypeLabel(rule.type) }),
      h('td', { text: String(rule.priority) }),
      h('td', { text: validity(rule) }),
      h('td', { text: String(rule.version) }),
      h('td', { text: rule.isActive ? 'Ativa' : 'Inativa' }),
      actions,
    );
  }

  async function load(): Promise<void> {
    const mine = ++generation;
    failure = undefined;
    try {
      const found = await listDiscountRules(options.http, activeOnly ? true : undefined);
      if (mine !== generation) {
        return;
      }
      rules = found;
    } catch (error) {
      if (mine !== generation) {
        return;
      }
      failure = errorMessage(error, 'Não foi possível carregar as regras de desconto.');
    }
    loaded = true;
    render();
  }

  async function deactivateRule(rule: DiscountRule): Promise<void> {
    const yes = await confirmAction({
      title: `Desativar "${rule.name}"?`,
      message:
        'Os pedidos deixam de usar a regra a partir de agora. Os pedidos já feitos mantêm a versão que usaram.',
      confirmLabel: 'Desativar',
    });

    if (!yes) {
      return;
    }
    let problem: string | undefined;
    try {
      const stored = await deactivateDiscountRule(options.http, rule.id, rule.version);
      status.textContent = `Regra "${stored.name}" desativada (versão ${String(stored.version)}).`;
    } catch (error) {
      status.textContent = '';
      problem =
        error instanceof PreconditionFailedError
          ? staleRule
          : errorMessage(error, 'Não foi possível desativar a regra.');
    }
    await load();
    if (problem !== undefined) {
      failure = problem;
      render();
    }
  }

  function openEditor(rule?: DiscountRule): void {
    const form = buildEditor(options.http, rules, rule, {
      saved: (stored) => {
        editor.replaceChildren();
        status.textContent = `Regra "${stored.name}" salva como versão ${String(stored.version)}.`;
        void load();
        create.focus();
      },
      closed: () => {
        editor.replaceChildren();
        create.focus();
      },
      stale: () => {
        void load();
      },
    });
    editor.replaceChildren(form);
    form.querySelector<HTMLElement>('input')?.focus();
  }

  create.addEventListener('click', () => {
    status.textContent = '';
    openEditor();
  });
  activeFilter.input.addEventListener('change', () => {
    activeOnly = activeFilter.input.checked;
    void load();
  });

  render();
  void load();
}

interface EditorEvents {
  readonly saved: (rule: DiscountRule) => void;
  readonly closed: () => void;
  readonly stale: () => void;
}

function buildEditor(
  http: HttpClient,
  rules: readonly DiscountRule[],
  existing: DiscountRule | undefined,
  events: EditorEvents,
): HTMLElement {
  const initial: DiscountDefinition =
    existing === undefined
      ? {
          name: '',
          type: 'Percentage',
          parameters: { percent: Number.NaN, appliesTo: 'order' },
          conditions: [],
          priority: 0,
          stackable: false,
          oncePerClient: false,
          isActive: true,
        }
      : discountDefinitionOf(existing);
  const summary = errorSummary();
  const name = textInput(initial.name);
  const typeSelect = selectOf(
    discountTypes.map((value) => [value, discountTypeLabel(value)] as const),
    initial.type,
  );
  const priority = numberInput(initial.priority, { step: '1' });
  const stackable = checkbox('Cumulativa', initial.stackable);
  const oncePerClient = checkbox('Uma vez por cliente', initial.oncePerClient);
  const active = checkbox('Ativa', initial.isActive);
  const validFrom = dateTimeInput(toLocalInput(initial.validFrom));
  const validTo = dateTimeInput(toLocalInput(initial.validTo));
  const parametersBox = h('div');
  const conditions: ConditionsEditor = createConditionsEditor(
    discountConditionKinds,
    initial.conditions,
  );
  const message = h('p', { className: 'mb-2', attrs: { role: 'status' } });
  let parameters = parametersFor(initial.type, initial.parameters);
  let saving = false;
  parametersBox.append(parameters.element);

  typeSelect.addEventListener('change', () => {
    parameters = parametersFor(
      typeSelect.value as DiscountType,
      defaultsOf(typeSelect.value as DiscountType),
    );
    parametersBox.replaceChildren(parameters.element);
  });

  function read(): DiscountDefinition {
    const from = fromLocalInput(validFrom.value);
    const to = fromLocalInput(validTo.value);

    return {
      name: name.value,
      type: typeSelect.value as DiscountType,
      parameters: parameters.read(),
      conditions: conditions.read(),
      priority: priority.valueAsNumber,
      stackable: stackable.input.checked,
      oncePerClient: oncePerClient.input.checked,
      ...(from === undefined ? {} : { validFrom: from }),
      ...(to === undefined ? {} : { validTo: to }),
      isActive: active.input.checked,
    };
  }

  /** The definition, or false after listing what to fix. */
  function checked(): DiscountDefinition | false {
    const definition = read();
    const problems = validateDiscount(definition);

    if (problems.length > 0) {
      summary.show(problems);
      return false;
    }
    summary.clear();

    return definition;
  }

  async function save(): Promise<void> {
    const definition = checked();
    if (definition === false || saving) {
      return;
    }
    saving = true;
    try {
      const body = toDiscountRequest(definition);
      const stored =
        existing === undefined
          ? await createDiscountRule(http, body)
          : await replaceDiscountRule(http, existing.id, existing.version, body);
      events.saved(stored);
    } catch (error) {
      const stale = error instanceof PreconditionFailedError;
      summary.show(
        [stale ? staleRule : errorMessage(error, 'Não foi possível salvar a regra.')],
        'Não salvo:',
      );
      if (stale) {
        events.stale();
      }
    }
    saving = false;
  }

  const preview = buildPreview(http, rules, existing, checked, name, summary);
  const form = h(
    'form',
    {
      className: 'border rounded p-3 mt-3',
      attrs: { 'aria-label': 'Editor de regra', novalidate: '' },
    },
    h('h3', {
      className: 'h5',
      text:
        existing === undefined
          ? 'Nova regra de desconto'
          : `Editar ${existing.name} (versão ${String(existing.version)})`,
    }),
    summary.element,
    h(
      'div',
      { className: 'row' },
      h('div', { className: 'col-md-6' }, labelled('Nome', name)),
      h('div', { className: 'col-md-3' }, labelled('Tipo', typeSelect)),
      h(
        'div',
        { className: 'col-md-3' },
        labelled('Prioridade', priority, 'O menor valor roda primeiro.'),
      ),
    ),
    parametersBox,
    h(
      'div',
      { className: 'row' },
      h(
        'div',
        { className: 'col-md-6' },
        labelled('Válida a partir de', validFrom, 'Deixe em branco para começar agora.'),
      ),
      h(
        'div',
        { className: 'col-md-6' },
        labelled('Válida até', validTo, 'Deixe em branco para não ter fim.'),
      ),
    ),
    stackable.element,
    oncePerClient.element,
    active.element,
    conditions.element,
    message,
    h(
      'div',
      { className: 'd-flex gap-2 mb-3' },
      actionButton('Salvar regra', 'btn btn-primary', () => void save(), 'submit'),
      actionButton('Cancelar', 'btn btn-outline-secondary', events.closed),
    ),
    preview,
  );
  form.addEventListener('submit', (event) => {
    event.preventDefault();
    void save();
  });

  return form;
}

function defaultsOf(type: DiscountType): DiscountParameters {
  switch (type) {
    case 'Percentage':
      return { percent: Number.NaN, appliesTo: 'order' };
    case 'FixedAmount':
      return { amounts: [{ currency: 'BRL', amount: Number.NaN }] };
    case 'BuyXGetY':
      return { buy: Number.NaN, get: Number.NaN };
    case 'Tiered':
      return { currency: 'BRL', tiers: [{ minSubtotal: 0, percent: Number.NaN }] };
  }
}

function parametersFor(type: DiscountType, value: DiscountParameters): Parameters {
  switch (type) {
    case 'Percentage': {
      const { percent, appliesTo } = value as PercentageParameters;
      const percentInput = numberInput(percent, { min: '0', max: '100' });
      const scope = selectOf(
        [
          ['order', 'O pedido inteiro'],
          ['line', 'Cada linha'],
        ],
        appliesTo,
      );

      return {
        element: h(
          'div',
          { className: 'row' },
          h('div', { className: 'col-md-6' }, labelled('Percentual', percentInput)),
          h('div', { className: 'col-md-6' }, labelled('Aplica-se a', scope)),
        ),
        read: () => ({ percent: percentInput.valueAsNumber, appliesTo: scope.value as AppliesTo }),
      };
    }
    case 'FixedAmount':
      return fixedAmountFields(value as FixedAmountParameters);
    case 'BuyXGetY': {
      const { buy, get, skus } = value as BuyXGetYParameters;
      const buyInput = numberInput(buy, { min: '1', step: '1' });
      const getInput = numberInput(get, { min: '1', step: '1' });
      const only = textArea((skus ?? []).join(', '));

      return {
        element: h(
          'div',
          { className: 'row' },
          h('div', { className: 'col-md-3' }, labelled('Compre', buyInput)),
          h(
            'div',
            { className: 'col-md-3' },
            labelled('Leve', getInput, 'Unidades dadas de graça a cada Compre comprado.'),
          ),
          h(
            'div',
            { className: 'col-md-6' },
            labelled(
              'Somente estes SKUs (opcional)',
              only,
              'Separe por vírgulas. Deixe em branco para todos os SKUs.',
            ),
          ),
        ),
        read: () => {
          const list = splitList(only.value);

          return {
            buy: buyInput.valueAsNumber,
            get: getInput.valueAsNumber,
            ...(list.length === 0 ? {} : { skus: list }),
          };
        },
      };
    }
    case 'Tiered':
      return tieredFields(value as TieredParameters);
  }
}

/** Rows that can be added and removed, each with its own fields; `describe` names a row for the Remove button. */
function rowsEditor<T>(
  legend: string,
  addLabel: string,
  removeLabel: string,
  initial: readonly T[],
  fresh: () => T,
  fieldsOf: (value: T) => { fields: HTMLElement[]; read: () => T },
): { element: HTMLElement; read: () => T[] } {
  const rows: { element: HTMLElement; read: () => T }[] = [];
  const list = h('div');
  const add = actionButton(addLabel, 'btn btn-outline-secondary btn-sm', () => {
    addRow(fresh()).querySelector<HTMLElement>('input')?.focus();
  });

  function addRow(value: T): HTMLElement {
    const { fields, read } = fieldsOf(value);
    const remove = actionButton('Remover', 'btn btn-outline-danger btn-sm', () => {
      rows.splice(rows.indexOf(row), 1);
      row.element.remove();
      renumber();
      add.focus();
    });
    const row = {
      element: h(
        'div',
        { className: 'row g-2 align-items-end mb-2' },
        ...fields,
        h('div', { className: 'col-auto mb-3' }, remove),
      ),
      read,
    };
    rows.push(row);
    list.append(row.element);
    renumber();

    return row.element;
  }

  function renumber(): void {
    rows.forEach((row, index) => {
      row.element
        .querySelector('button')
        ?.setAttribute('aria-label', `${removeLabel} ${String(index + 1)}`);
    });
  }

  for (const value of initial) {
    addRow(value);
  }

  return {
    element: h(
      'fieldset',
      { className: 'mb-3' },
      h('legend', { className: 'h6', text: legend }),
      list,
      add,
    ),
    read: () => rows.map((row) => row.read()),
  };
}

function fixedAmountFields(value: FixedAmountParameters): Parameters {
  const amounts = rowsEditor(
    'Valores de desconto',
    'Adicionar valor',
    'Remover valor',
    value.amounts,
    () => ({ currency: 'BRL', amount: Number.NaN }),
    (item) => {
      const currency = textInput(item.currency, { maxlength: '3' });
      const amount = numberInput(item.amount, { min: '0' });

      return {
        fields: [
          h('div', { className: 'col-md-4' }, labelled('Moeda', currency)),
          h('div', { className: 'col-md-4' }, labelled('Valor', amount)),
        ],
        read: () => ({ currency: currency.value, amount: amount.valueAsNumber }),
      };
    },
  );

  return { element: amounts.element, read: () => ({ amounts: amounts.read() }) };
}

function tieredFields(value: TieredParameters): Parameters {
  const currency = textInput(value.currency, { maxlength: '3' });
  const tiers = rowsEditor(
    'Faixas',
    'Adicionar faixa',
    'Remover faixa',
    value.tiers,
    () => ({ minSubtotal: Number.NaN, percent: Number.NaN }),
    (tier) => {
      const minimum = numberInput(tier.minSubtotal, { min: '0' });
      const percent = numberInput(tier.percent, { min: '0', max: '100' });

      return {
        fields: [
          h('div', { className: 'col-md-4' }, labelled('Subtotal mínimo', minimum)),
          h('div', { className: 'col-md-4' }, labelled('Percentual', percent)),
        ],
        read: () => ({ minSubtotal: minimum.valueAsNumber, percent: percent.valueAsNumber }),
      };
    },
  );

  return {
    element: h(
      'div',
      {},
      h('div', { className: 'col-md-4' }, labelled('Moeda', currency)),
      tiers.element,
    ),
    read: () => ({ currency: currency.value, tiers: tiers.read() }),
  };
}

function buildPreview(
  http: HttpClient,
  rules: readonly DiscountRule[],
  existing: DiscountRule | undefined,
  checked: () => DiscountDefinition | false,
  name: HTMLInputElement,
  summary: ReturnType<typeof errorSummary>,
): HTMLElement {
  const currency = textInput('BRL', { maxlength: '3' });
  const result = h('section', { attrs: { 'aria-label': 'Resultado da simulação' } });
  result.hidden = true;
  const items = rowsEditor(
    'Carrinho de exemplo',
    'Adicionar item de exemplo',
    'Remover item de exemplo',
    [{ sku: '', quantity: 1 }],
    () => ({ sku: '', quantity: 1 }),
    (item) => {
      const sku = textInput(item.sku);
      const quantity = numberInput(item.quantity, { min: '1', step: '1' });

      return {
        fields: [
          h('div', { className: 'col-md-5' }, labelled('SKU da simulação', sku)),
          h('div', { className: 'col-md-3' }, labelled('Quantidade da simulação', quantity)),
        ],
        read: () => ({ sku: sku.value.trim(), quantity: quantity.valueAsNumber }),
      };
    },
  );

  async function run(): Promise<void> {
    const definition = checked();
    if (definition === false) {
      return;
    }
    const cart = items.read().filter((item) => item.sku !== '');
    if (
      cart.length === 0 ||
      !cart.every((item) => Number.isInteger(item.quantity) && item.quantity >= 1)
    ) {
      summary.show(['Adicione ao carrinho de exemplo pelo menos um SKU com quantidade 1 ou mais.']);
      return;
    }

    try {
      const preview = await previewDiscountRule(
        http,
        { currency: currency.value.trim().toUpperCase(), items: cart },
        toDiscountRequest(definition),
        existing?.id,
      );
      summary.clear();
      result.replaceChildren(...previewRows(preview, rules, existing, name.value.trim()));
      result.hidden = false;
    } catch (error) {
      result.hidden = true;
      summary.show([errorMessage(error, 'A simulação falhou.')], 'Sem simulação:');
    }
  }

  return h(
    'fieldset',
    { className: 'border-top pt-3' },
    h('legend', { className: 'h5', text: 'Simulação' }),
    h('p', {
      className: 'text-body-secondary',
      text: 'Precifica um carrinho de exemplo com a regra como está no formulário. Nada é salvo.',
    }),
    h('div', { className: 'col-md-3' }, labelled('Moeda da simulação', currency)),
    items.element,
    actionButton('Simular regra', 'btn btn-outline-primary mb-3', () => void run()),
    result,
  );
}

function previewRows(
  preview: RulePreview,
  rules: readonly DiscountRule[],
  existing: DiscountRule | undefined,
  draftName: string,
): HTMLElement[] {
  const money = (amount: number): string => formatMoney(amount, preview.quote.currency);
  const nameOf = (id: string): string =>
    id === existing?.id && draftName !== ''
      ? draftName
      : (rules.find((rule) => rule.id === id)?.name ?? id.slice(0, 8));

  return [
    h(
      'dl',
      { className: 'row' },
      h('dt', { className: 'col-6', text: 'Subtotal' }),
      h('dd', { className: 'col-6 text-end', text: money(preview.quote.subtotal) }),
      h('dt', { className: 'col-6', text: 'Descontos' }),
      h('dd', { className: 'col-6 text-end', text: `-${money(preview.quote.discountTotal)}` }),
      h('dt', { className: 'col-6', text: 'Total' }),
      h('dd', { className: 'col-6 text-end fw-semibold', text: money(preview.quote.total) }),
    ),
    preview.applied.length === 0
      ? h('p', { text: 'Nenhuma regra se aplica a este carrinho.' })
      : h(
          'ul',
          {},
          ...preview.applied.map((applied) =>
            h('li', {
              text: `${nameOf(applied.ruleId)} (versão ${String(applied.ruleVersion)}): -${money(applied.amount)}`,
            }),
          ),
        ),
  ];
}

function validity(rule: DiscountRule): string {
  return rule.validTo === null
    ? `A partir de ${formatDateTime(rule.validFrom)}`
    : `${formatDateTime(rule.validFrom)} – ${formatDateTime(rule.validTo)}`;
}
