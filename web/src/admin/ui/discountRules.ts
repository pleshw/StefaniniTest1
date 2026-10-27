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

const staleRule = 'This rule was changed by someone else. Reload the list and open it again.';

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
  const activeFilter = checkbox('Active only', false);
  const create = h('button', {
    className: 'btn btn-primary btn-sm',
    text: 'New rule',
    attrs: { type: 'button' },
  });
  const body = h('tbody');
  const table = h(
    'table',
    { className: 'table align-middle' },
    h('caption', { className: 'visually-hidden', text: 'Discount rules' }),
    h(
      'thead',
      {},
      h(
        'tr',
        {},
        ...['Name', 'Type', 'Priority', 'Valid', 'Version', 'State', 'Actions'].map((name) =>
          h('th', { text: name, attrs: { scope: 'col' } }),
        ),
      ),
    ),
    body,
  );
  const empty = h('p', { text: 'There are no discount rules.' });
  const editor = h('div');

  container.append(
    h(
      'section',
      { attrs: { 'aria-labelledby': 'discount-heading' } },
      h('h2', { className: 'h4', text: 'Discount rules', attrs: { id: 'discount-heading' } }),
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
            actionButton('Try again', 'btn btn-sm btn-outline-danger ms-auto', () => void load()),
          ]),
    );
  }

  function rowOf(rule: DiscountRule): HTMLElement {
    const edit = actionButton('Edit', 'btn btn-outline-primary btn-sm', () => {
      openEditor(rule);
    });
    edit.setAttribute('aria-label', `Edit ${rule.name}`);
    const actions = h('td', { className: 'd-flex gap-2' }, edit);

    if (rule.isActive) {
      const deactivate = actionButton(
        'Deactivate',
        'btn btn-outline-danger btn-sm',
        () => void deactivateRule(rule),
      );
      deactivate.setAttribute('aria-label', `Deactivate ${rule.name}`);
      actions.append(deactivate);
    }

    return h(
      'tr',
      {},
      h('th', { text: rule.name, attrs: { scope: 'row' } }),
      h('td', { text: rule.type }),
      h('td', { text: String(rule.priority) }),
      h('td', { text: validity(rule) }),
      h('td', { text: String(rule.version) }),
      h('td', { text: rule.isActive ? 'Active' : 'Inactive' }),
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
      failure = errorMessage(error, 'The discount rules could not be loaded.');
    }
    loaded = true;
    render();
  }

  async function deactivateRule(rule: DiscountRule): Promise<void> {
    const yes = await confirmAction({
      title: `Deactivate "${rule.name}"?`,
      message:
        'Orders stop using the rule from now on. Orders already placed keep the version they used.',
      confirmLabel: 'Deactivate',
    });

    if (!yes) {
      return;
    }
    let problem: string | undefined;
    try {
      const stored = await deactivateDiscountRule(options.http, rule.id, rule.version);
      status.textContent = `Deactivated "${stored.name}" (version ${String(stored.version)}).`;
    } catch (error) {
      status.textContent = '';
      problem =
        error instanceof PreconditionFailedError
          ? staleRule
          : errorMessage(error, 'The rule could not be deactivated.');
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
        status.textContent = `Saved "${stored.name}" as version ${String(stored.version)}.`;
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
    discountTypes.map((value) => [value, value] as const),
    initial.type,
  );
  const priority = numberInput(initial.priority, { step: '1' });
  const stackable = checkbox('Stackable', initial.stackable);
  const oncePerClient = checkbox('Once per client', initial.oncePerClient);
  const active = checkbox('Active', initial.isActive);
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
        [stale ? staleRule : errorMessage(error, 'The rule could not be saved.')],
        'Not saved:',
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
      attrs: { 'aria-label': 'Rule editor', novalidate: '' },
    },
    h('h3', {
      className: 'h5',
      text:
        existing === undefined
          ? 'New discount rule'
          : `Edit ${existing.name} (version ${String(existing.version)})`,
    }),
    summary.element,
    h(
      'div',
      { className: 'row' },
      h('div', { className: 'col-md-6' }, labelled('Name', name)),
      h('div', { className: 'col-md-3' }, labelled('Type', typeSelect)),
      h('div', { className: 'col-md-3' }, labelled('Priority', priority, 'Lower runs first.')),
    ),
    parametersBox,
    h(
      'div',
      { className: 'row' },
      h(
        'div',
        { className: 'col-md-6' },
        labelled('Valid from', validFrom, 'Leave empty to start now.'),
      ),
      h('div', { className: 'col-md-6' }, labelled('Valid to', validTo, 'Leave empty for no end.')),
    ),
    stackable.element,
    oncePerClient.element,
    active.element,
    conditions.element,
    message,
    h(
      'div',
      { className: 'd-flex gap-2 mb-3' },
      actionButton('Save rule', 'btn btn-primary', () => void save(), 'submit'),
      actionButton('Cancel', 'btn btn-outline-secondary', events.closed),
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
          ['order', 'The whole order'],
          ['line', 'Each line'],
        ],
        appliesTo,
      );

      return {
        element: h(
          'div',
          { className: 'row' },
          h('div', { className: 'col-md-6' }, labelled('Percent', percentInput)),
          h('div', { className: 'col-md-6' }, labelled('Applies to', scope)),
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
          h('div', { className: 'col-md-3' }, labelled('Buy', buyInput)),
          h(
            'div',
            { className: 'col-md-3' },
            labelled('Get', getInput, 'Units given free for every Buy bought.'),
          ),
          h(
            'div',
            { className: 'col-md-6' },
            labelled(
              'Only these SKUs (optional)',
              only,
              'Separate with commas. Leave empty for every SKU.',
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
    const remove = actionButton('Remove', 'btn btn-outline-danger btn-sm', () => {
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
    'Amounts off',
    'Add amount',
    'Remove amount',
    value.amounts,
    () => ({ currency: 'BRL', amount: Number.NaN }),
    (item) => {
      const currency = textInput(item.currency, { maxlength: '3' });
      const amount = numberInput(item.amount, { min: '0' });

      return {
        fields: [
          h('div', { className: 'col-md-4' }, labelled('Currency', currency)),
          h('div', { className: 'col-md-4' }, labelled('Amount', amount)),
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
    'Bands',
    'Add band',
    'Remove band',
    value.tiers,
    () => ({ minSubtotal: Number.NaN, percent: Number.NaN }),
    (tier) => {
      const minimum = numberInput(tier.minSubtotal, { min: '0' });
      const percent = numberInput(tier.percent, { min: '0', max: '100' });

      return {
        fields: [
          h('div', { className: 'col-md-4' }, labelled('Min subtotal', minimum)),
          h('div', { className: 'col-md-4' }, labelled('Percent', percent)),
        ],
        read: () => ({ minSubtotal: minimum.valueAsNumber, percent: percent.valueAsNumber }),
      };
    },
  );

  return {
    element: h(
      'div',
      {},
      h('div', { className: 'col-md-4' }, labelled('Currency', currency)),
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
  const result = h('section', { attrs: { 'aria-label': 'Preview result' } });
  result.hidden = true;
  const items = rowsEditor(
    'Sample cart',
    'Add sample item',
    'Remove sample item',
    [{ sku: '', quantity: 1 }],
    () => ({ sku: '', quantity: 1 }),
    (item) => {
      const sku = textInput(item.sku);
      const quantity = numberInput(item.quantity, { min: '1', step: '1' });

      return {
        fields: [
          h('div', { className: 'col-md-5' }, labelled('Preview SKU', sku)),
          h('div', { className: 'col-md-3' }, labelled('Preview quantity', quantity)),
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
      summary.show(['Add at least one SKU with a quantity of 1 or more to the sample cart.']);
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
      summary.show([errorMessage(error, 'The preview failed.')], 'No preview:');
    }
  }

  return h(
    'fieldset',
    { className: 'border-top pt-3' },
    h('legend', { className: 'h5', text: 'Preview' }),
    h('p', {
      className: 'text-body-secondary',
      text: 'Prices a sample cart with the rule as it is in the form. Nothing is saved.',
    }),
    h('div', { className: 'col-md-3' }, labelled('Preview currency', currency)),
    items.element,
    actionButton('Preview rule', 'btn btn-outline-primary mb-3', () => void run()),
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
      h('dt', { className: 'col-6', text: 'Discounts' }),
      h('dd', { className: 'col-6 text-end', text: `-${money(preview.quote.discountTotal)}` }),
      h('dt', { className: 'col-6', text: 'Total' }),
      h('dd', { className: 'col-6 text-end fw-semibold', text: money(preview.quote.total) }),
    ),
    preview.applied.length === 0
      ? h('p', { text: 'No rule applies to this cart.' })
      : h(
          'ul',
          {},
          ...preview.applied.map((applied) =>
            h('li', {
              text: `${nameOf(applied.ruleId)} (version ${String(applied.ruleVersion)}): -${money(applied.amount)}`,
            }),
          ),
        ),
  ];
}

function validity(rule: DiscountRule): string {
  return rule.validTo === null
    ? `From ${formatDateTime(rule.validFrom)}`
    : `${formatDateTime(rule.validFrom)} – ${formatDateTime(rule.validTo)}`;
}
