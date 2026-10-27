import { fromLocalInput, toLocalInput } from '../../shared/domain/dates';
import { h } from '../../shared/ui/dom';
import { helpTip, legendOf } from '../../shared/ui/help';
import { withIcon } from '../../shared/ui/icons';
import {
  dateTimeInput,
  labelled,
  numberInput,
  selectOf,
  textArea,
  textInput,
  uniqueId,
} from '../../shared/ui/form';
import { conditionLabels, splitList, type Condition, type ConditionKind } from '../domain/rules';

export interface ConditionsEditor {
  readonly element: HTMLElement;
  /** The conditions as typed; a number box left empty reads as NaN so validation can name it. */
  readonly read: () => Condition[];
}

interface Row {
  readonly element: HTMLElement;
  readonly read: () => Condition;
}

/** What each condition checks, shown under the picker and on each condition. */
export const conditionDescriptions: Readonly<Record<ConditionKind, string>> = {
  MinSubtotal:
    'O subtotal do carrinho, antes dos descontos, é pelo menos este valor nesta moeda. Um carrinho em outra moeda não atende.',
  SkuIn: 'O carrinho tem pelo menos um destes SKUs.',
  CategoryIn:
    'O carrinho tem pelo menos um produto destas categorias (maiúsculas e minúsculas não importam).',
  MinQuantity:
    'O carrinho soma pelo menos esta quantidade de unidades; se houver SKUs listados, só eles contam.',
  ClientSegment:
    'O cliente pertence a um destes segmentos. Ainda não há fonte de segmentos, então hoje esta condição nunca é atendida.',
  DateWindow: 'Vale só dentro deste período. Uma das pontas pode ficar em branco, mas não as duas.',
  TotalAbove:
    'O valor do pedido é maior que este valor (igual não basta), nesta moeda. Outra moeda não atende.',
  FirstOrderOfClient: 'Vale quando o cliente não tem nenhum pedido anterior.',
  ChannelIs:
    'Vale só para pedidos feitos por este canal: a Loja, pelo cliente, ou a Administração, por um administrador.',
};

export interface ConditionsEditorOptions {
  readonly legend?: string;
  /** The step number shown before the legend, when the form is laid out in steps. */
  readonly step?: number;
}

/**
 * A list of conditions, combined with AND: a card per condition with a (?) on what it checks and a button to remove it,
 * and below them a picker whose (?) describes the chosen kind. With no condition the rule always applies.
 */
export function createConditionsEditor(
  kinds: readonly ConditionKind[],
  initial: readonly Condition[],
  options: ConditionsEditorOptions = {},
): ConditionsEditor {
  const rows: Row[] = [];
  const list = h('div', { className: 'condition-list' });
  const empty = withIcon(
    h('p', {
      className: 'condition-empty',
      text: 'Nenhuma condição: vale para qualquer carrinho.',
    }),
    'bi-info-circle',
  );
  const picker = selectOf(
    kinds.map((kind) => [kind, conditionLabels[kind]] as const),
    kinds[0] ?? 'MinSubtotal',
  );
  const add = withIcon(
    h('button', {
      className: 'btn btn-outline-primary',
      text: 'Adicionar condição',
      attrs: { type: 'button' },
    }),
    'bi-plus-lg',
    'me-1',
  );
  const pickerField = labelled(
    'Tipo de condição',
    picker,
    undefined,
    conditionDescriptions[picker.value as ConditionKind],
  );
  pickerField.classList.replace('mb-3', 'condition-picker-field');
  const pickerTip = pickerField.querySelector('[role="tooltip"]');
  const describePicker = (): void => {
    if (pickerTip !== null) {
      pickerTip.textContent = conditionDescriptions[picker.value as ConditionKind];
    }
  };
  const element = h('fieldset', { className: options.step === undefined ? 'mb-3' : 'form-step' });
  element.append(
    legendOf(element, options.legend ?? 'Condições', {
      ...(options.step === undefined ? {} : { step: options.step }),
      help: 'Sem condições, a regra vale sempre. Com várias, todas precisam ser atendidas ao mesmo tempo (E); para "uma coisa OU outra", crie duas regras.',
    }),
    empty,
    list,
    h('div', { className: 'condition-add' }, pickerField, add),
  );

  function refresh(): void {
    empty.hidden = rows.length > 0;
  }

  function addRow(condition: Condition): HTMLElement {
    const row = createRow(condition, () => {
      rows.splice(rows.indexOf(row), 1);
      row.element.remove();
      refresh();
      picker.focus();
    });
    rows.push(row);
    list.append(row.element);
    refresh();

    return row.element;
  }

  picker.addEventListener('change', describePicker);
  add.addEventListener('click', () => {
    const row = addRow(emptyCondition(picker.value as ConditionKind));
    row.querySelector<HTMLElement>('input, textarea, select')?.focus();
  });
  for (const condition of initial) {
    addRow(condition);
  }
  describePicker();
  refresh();

  return { element, read: () => rows.map((row) => row.read()) };
}

function emptyCondition(kind: ConditionKind): Condition {
  switch (kind) {
    case 'MinSubtotal':
    case 'TotalAbove':
      return { kind, amount: Number.NaN, currency: 'BRL' };
    case 'SkuIn':
      return { kind, skus: [] };
    case 'CategoryIn':
      return { kind, categories: [] };
    case 'ClientSegment':
      return { kind, segments: [] };
    case 'MinQuantity':
      return { kind, quantity: Number.NaN };
    case 'DateWindow':
      return { kind };
    case 'FirstOrderOfClient':
      return { kind };
    case 'ChannelIs':
      return { kind, channel: 'shop' };
  }
}

function createRow(condition: Condition, remove: () => void): Row {
  const label = conditionLabels[condition.kind];
  const { fields, read } = fieldsFor(condition);
  const button = withIcon(
    h('button', {
      className: 'btn btn-outline-danger btn-sm',
      text: 'Remover',
      attrs: { type: 'button', 'aria-label': `Remover condição ${label}` },
    }),
    'bi-trash',
    'me-1',
  );
  button.addEventListener('click', remove);
  const id = uniqueId('condition');
  const help = helpTip(label, conditionDescriptions[condition.kind]);

  return {
    element: h(
      'fieldset',
      { className: 'condition-card', attrs: { id, 'aria-describedby': help.tip.id } },
      h('legend', { className: 'condition-card-header', text: label }),
      h('div', { className: 'condition-actions' }, help.element, button),
      ...(fields.length === 0 ? [] : [h('div', { className: 'row g-2' }, ...fields)]),
    ),
    read,
  };
}

function fieldsFor(condition: Condition): { fields: HTMLElement[]; read: () => Condition } {
  const column = (
    control: HTMLElement,
    text: string,
    hint?: string,
    width = 'col-md-6',
  ): HTMLElement => h('div', { className: width }, labelled(text, control, hint));

  switch (condition.kind) {
    case 'MinSubtotal':
    case 'TotalAbove': {
      const label = conditionLabels[condition.kind];
      const amount = numberInput(condition.amount, { min: '0' });
      const currency = textInput(condition.currency, { maxlength: '3' });

      return {
        fields: [
          column(amount, `${label}: valor`),
          h(
            'div',
            { className: 'col-md-6' },
            labelled(
              `${label}: moeda`,
              currency,
              undefined,
              'Código de 3 letras, como BRL ou USD.',
            ),
          ),
        ],
        read: () => ({
          kind: condition.kind,
          amount: amount.valueAsNumber,
          currency: currency.value,
        }),
      };
    }
    case 'SkuIn': {
      const skus = textArea(condition.skus.join(', '));

      return {
        fields: [column(skus, 'SKUs', 'Separe por vírgulas ou quebras de linha.', 'col-12')],
        read: () => ({ kind: 'SkuIn', skus: splitList(skus.value) }),
      };
    }
    case 'CategoryIn': {
      const categories = textArea(condition.categories.join(', '));

      return {
        fields: [
          column(categories, 'Categorias', 'Separe por vírgulas ou quebras de linha.', 'col-12'),
        ],
        read: () => ({ kind: 'CategoryIn', categories: splitList(categories.value) }),
      };
    }
    case 'ClientSegment': {
      const segments = textArea(condition.segments.join(', '));

      return {
        fields: [
          column(segments, 'Segmentos', 'Separe por vírgulas ou quebras de linha.', 'col-12'),
        ],
        read: () => ({ kind: 'ClientSegment', segments: splitList(segments.value) }),
      };
    }
    case 'MinQuantity': {
      const quantity = numberInput(condition.quantity, { min: '1', step: '1' });
      const skus = textArea((condition.skus ?? []).join(', '));

      return {
        fields: [
          column(quantity, 'Quantidade mínima: unidades'),
          column(
            skus,
            'Quantidade mínima: somente estes SKUs (opcional)',
            'Deixe em branco para contar todos os SKUs.',
          ),
        ],
        read: () => {
          const only = splitList(skus.value);

          return {
            kind: 'MinQuantity',
            quantity: quantity.valueAsNumber,
            ...(only.length === 0 ? {} : { skus: only }),
          };
        },
      };
    }
    case 'DateWindow': {
      const from = dateTimeInput(toLocalInput(condition.from));
      const to = dateTimeInput(toLocalInput(condition.to));

      return {
        fields: [
          column(from, 'Janela de datas: de', 'Deixe em branco para não ter início.'),
          column(to, 'Janela de datas: até', 'Deixe em branco para não ter fim.'),
        ],
        read: () => {
          const start = fromLocalInput(from.value);
          const end = fromLocalInput(to.value);

          return {
            kind: 'DateWindow',
            ...(start === undefined ? {} : { from: start }),
            ...(end === undefined ? {} : { to: end }),
          };
        },
      };
    }
    case 'FirstOrderOfClient':
      return { fields: [], read: () => ({ kind: 'FirstOrderOfClient' }) };
    case 'ChannelIs': {
      const channel = selectOf(
        [
          ['shop', 'Loja'],
          ['admin', 'Administração'],
        ],
        condition.channel,
      );

      return {
        fields: [column(channel, 'Canal é')],
        read: () => ({ kind: 'ChannelIs', channel: channel.value === 'admin' ? 'admin' : 'shop' }),
      };
    }
  }
}
