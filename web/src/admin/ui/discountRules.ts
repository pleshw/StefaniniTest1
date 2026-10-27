import {
  createDiscountRule,
  deactivateDiscountRule,
  listDiscountRules,
  replaceDiscountRule,
  type DiscountRule,
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

/** What each type of discount does, shown under the type picker. */
const discountTypeDescriptions: Readonly<Record<DiscountType, string>> = {
  Percentage: 'Tira um percentual do subtotal do pedido ou de cada item. Vale em qualquer moeda.',
  FixedAmount:
    'Tira um valor fixo do pedido, informado por moeda. Um carrinho em moeda sem valor não ganha desconto, e o desconto nunca passa do subtotal.',
  BuyXGetY:
    'A cada "Compre + Leve" unidades, "Leve" saem de graça, sempre as mais baratas. Pode valer só para alguns SKUs.',
  Tiered:
    'Um percentual que cresce com o subtotal: vale a maior faixa que o subtotal alcança, na moeda das faixas.',
};
import { createConditionsEditor, type ConditionsEditor } from './conditionsEditor';
import { rowsEditor } from './rowsEditor';
import { PreconditionFailedError } from '../../shared/api/errors';
import type { HttpClient } from '../../shared/api/http';
import { formatDateTime, fromLocalInput, toLocalInput } from '../../shared/domain/dates';
import { confirmAction } from '../../shared/ui/dialog';
import { h } from '../../shared/ui/dom';
import { helpTip } from '../../shared/ui/help';
import { withIcon } from '../../shared/ui/icons';
import { errorMessage } from '../../shared/ui/errors';
import {
  actionButton,
  checkbox,
  dateTimeInput,
  errorSummary,
  formStep,
  labelled,
  numberInput,
  selectOf,
  textArea,
  textInput,
} from '../../shared/ui/form';

export interface DiscountRulesOptions {
  readonly http: HttpClient;
  /** Opens the simulation with the rule as it is in the editor; without it the editor has no simulation step. */
  readonly onSimulate?: (draft: DiscountDraft) => void;
}

/** A rule as it is in the editor, valid but not saved, and the saved rule it would replace. */
export interface DiscountDraft {
  readonly definition: DiscountDefinition;
  readonly existing?: DiscountRule;
}

export interface DiscountRulesView {
  /** Puts the focus back where the person left: the open editor's simulation button, or "Nova regra". */
  resume(): void;
}

interface Parameters {
  readonly element: HTMLElement;
  readonly read: () => DiscountParameters;
}

const staleRule =
  'Esta regra foi alterada por outra pessoa. Recarregue a lista e abra-a novamente.';

/** Discount rules: the list and an editor in steps, with a form for each type of strategy and a conditions builder. */
export function mountDiscountRules(
  container: HTMLElement,
  options: DiscountRulesOptions,
): DiscountRulesView {
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
  const activeFilter = checkbox(
    'Somente ativas',
    false,
    'Esconde as regras desativadas. Uma regra desativada não volta a valer, mas continua na lista para consulta.',
  );
  const create = withIcon(
    h('button', {
      className: 'btn btn-primary btn-sm',
      text: 'Nova regra',
      attrs: { type: 'button' },
    }),
    'bi-plus-lg',
    'me-1',
  );
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
      withIcon(
        h('h2', { className: 'h4', text: 'Regras de desconto', attrs: { id: 'discount-heading' } }),
        'bi-percent',
      ),
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
    const form = buildEditor(options.http, rule, {
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
      ...(options.onSimulate === undefined ? {} : { simulate: options.onSimulate }),
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

  return {
    resume() {
      (editor.querySelector<HTMLElement>('[data-simulate]') ?? create).focus();
    },
  };
}

interface EditorEvents {
  readonly saved: (rule: DiscountRule) => void;
  readonly closed: () => void;
  readonly stale: () => void;
  readonly simulate?: (draft: DiscountDraft) => void;
}

function buildEditor(
  http: HttpClient,
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
  const stackable = checkbox(
    'Cumulativa',
    initial.stackable,
    'Desmarcada: entre as regras não cumulativas que se aplicam ao carrinho, só vale a que dá o maior desconto. Marcada: a regra soma com as outras, depois da melhor não cumulativa, na ordem de prioridade. O desconto total nunca passa de 50% do subtotal.',
  );
  const oncePerClient = checkbox(
    'Uma vez por cliente',
    initial.oncePerClient,
    'Cada cliente recebe esta regra em um só pedido; depois disso ela deixa de valer para ele.',
  );
  const active = checkbox(
    'Ativa',
    initial.isActive,
    'Desmarcada, a regra fica guardada, mas não é aplicada a nenhum carrinho.',
  );
  const validFrom = dateTimeInput(toLocalInput(initial.validFrom));
  const validTo = dateTimeInput(toLocalInput(initial.validTo));
  const parametersBox = h('div');
  const conditions: ConditionsEditor = createConditionsEditor(
    discountConditionKinds,
    initial.conditions,
    { legend: 'Quando vale', step: 3 },
  );
  const message = h('p', { className: 'mb-2', attrs: { role: 'status' } });
  let parameters = parametersFor(initial.type, initial.parameters);
  let saving = false;
  parametersBox.append(parameters.element);

  const typeField = labelled('Tipo', typeSelect, undefined, discountTypeDescriptions[initial.type]);
  const typeTip = typeField.querySelector('[role="tooltip"]');
  const describeType = (): void => {
    if (typeTip !== null) {
      typeTip.textContent = discountTypeDescriptions[typeSelect.value as DiscountType];
    }
  };
  typeSelect.addEventListener('change', () => {
    describeType();
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

  const simulate = withIcon(
    h('button', {
      className: 'btn btn-outline-primary',
      text: 'Simular esta regra',
      attrs: { type: 'button', 'data-simulate': '' },
    }),
    'bi-calculator',
    'me-1',
  );
  simulate.addEventListener('click', () => {
    const definition = checked();
    if (definition !== false) {
      events.simulate?.({ definition, ...(existing === undefined ? {} : { existing }) });
    }
  });
  const form = h(
    'form',
    {
      className: 'rule-editor',
      attrs: { 'aria-label': 'Editor de regra', novalidate: '' },
    },
    h(
      'div',
      { className: 'rule-editor-header' },
      h('h3', {
        className: 'h5 mb-0',
        text:
          existing === undefined
            ? 'Nova regra de desconto'
            : `Editar ${existing.name} (versão ${String(existing.version)})`,
      }),
    ),
    summary.element,
    formStep(
      1,
      'Identificação',
      undefined,
      h(
        'div',
        { className: 'row' },
        h(
          'div',
          { className: 'col-md-8' },
          labelled(
            'Nome',
            name,
            undefined,
            'Aparece na lista de regras e na simulação. Use um nome que diga o que a regra faz, como "10% acima de R$ 500".',
          ),
        ),
        h(
          'div',
          { className: 'col-md-4' },
          labelled(
            'Prioridade',
            priority,
            undefined,
            'O menor valor roda primeiro. Ordem e desempate. As regras cumulativas são aplicadas da menor para a maior prioridade; entre regras não cumulativas que dão o mesmo desconto, vale a de menor prioridade.',
          ),
        ),
      ),
      active.element,
    ),
    formStep(
      2,
      'Desconto',
      'Como o desconto é calculado. Os campos mudam conforme o tipo.',
      h('div', { className: 'row' }, h('div', { className: 'col-md-6' }, typeField)),
      parametersBox,
    ),
    conditions.element,
    formStep(
      4,
      'Validade e combinação',
      'Fora do período a regra não se aplica. Com as datas em branco, ela começa ao salvar e não tem fim.',
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
    ),
    ...(events.simulate === undefined
      ? []
      : [
          formStep(
            5,
            'Simular (opcional)',
            'Abre a Simulação com a regra como está aqui, para ver o desconto num carrinho de exemplo antes de salvar. Nada é salvo.',
            simulate,
          ),
        ]),
    message,
    h(
      'div',
      { className: 'rule-editor-actions' },
      actionButton('Salvar regra', 'btn btn-primary', () => void save(), 'submit'),
      actionButton('Cancelar', 'btn btn-outline-secondary', events.closed),
      helpTip(
        'Salvar regra',
        existing === undefined
          ? 'A regra passa a valer ao salvar, se estiver ativa e dentro da validade.'
          : `Salvar grava a versão ${String(existing.version + 1)}; os pedidos já feitos mantêm a versão que usaram.`,
      ).element,
    ),
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
          ['line', 'Cada item (linha)'],
        ],
        appliesTo,
      );

      return {
        element: h(
          'div',
          { className: 'row' },
          h(
            'div',
            { className: 'col-md-6' },
            labelled('Percentual', percentInput, undefined, 'Entre 0 e 100, sem o símbolo %.'),
          ),
          h(
            'div',
            { className: 'col-md-6' },
            labelled(
              'Aplica-se a',
              scope,
              undefined,
              'O pedido inteiro: o percentual sai do subtotal, arredondado uma vez. Cada item: sai de cada linha, arredondado linha a linha, então o total pode diferir em alguns centavos.',
            ),
          ),
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
          h(
            'div',
            { className: 'col-md-3' },
            labelled(
              'Compre',
              buyInput,
              undefined,
              'Quantas unidades o cliente paga. Ex.: Compre 2, Leve 1 = a cada 3 unidades, a mais barata sai de graça.',
            ),
          ),
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
          h(
            'div',
            { className: 'col-md-4' },
            labelled('Moeda', currency, undefined, 'Código de 3 letras, como BRL.'),
          ),
          h('div', { className: 'col-md-4' }, labelled('Valor', amount)),
        ],
        read: () => ({ currency: currency.value, amount: amount.valueAsNumber }),
      };
    },
    'Um valor por moeda. Um carrinho numa moeda que não está aqui não ganha desconto; o desconto nunca passa do subtotal.',
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
    'Vale a maior faixa que o subtotal alcança. Ex.: a partir de 0 → 5% e a partir de 500 → 10%: um carrinho de 600 ganha 10%.',
  );

  return {
    element: h(
      'div',
      {},
      h(
        'div',
        { className: 'col-md-4' },
        labelled(
          'Moeda',
          currency,
          undefined,
          'Código de 3 letras, como BRL. Outra moeda não ganha desconto.',
        ),
      ),
      tiers.element,
    ),
    read: () => ({ currency: currency.value, tiers: tiers.read() }),
  };
}

function validity(rule: DiscountRule): string {
  return rule.validTo === null
    ? `A partir de ${formatDateTime(rule.validFrom)}`
    : `${formatDateTime(rule.validFrom)} – ${formatDateTime(rule.validTo)}`;
}
