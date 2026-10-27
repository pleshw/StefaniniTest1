import { fromLocalInput, toLocalInput } from '../../shared/domain/dates';
import { h } from '../../shared/ui/dom';
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

/** A list of conditions: rows of fields for each kind, with a picker to add one and a button to remove one. */
export function createConditionsEditor(
  kinds: readonly ConditionKind[],
  initial: readonly Condition[],
  legend = 'Condições',
): ConditionsEditor {
  const rows: Row[] = [];
  const list = h('div');
  const picker = selectOf(
    kinds.map((kind) => [kind, conditionLabels[kind]] as const),
    kinds[0] ?? 'MinSubtotal',
  );
  const add = withIcon(
    h('button', {
      className: 'btn btn-outline-secondary btn-sm',
      text: 'Adicionar condição',
      attrs: { type: 'button' },
    }),
    'bi-plus-lg',
    'me-1',
  );
  const element = h(
    'fieldset',
    { className: 'mb-3' },
    h('legend', { className: 'h6', text: legend }),
    list,
    h(
      'div',
      { className: 'd-flex flex-wrap gap-2 align-items-end' },
      labelled('Tipo de condição', picker),
      add,
    ),
  );

  function addRow(condition: Condition): HTMLElement {
    const row = createRow(condition, () => {
      rows.splice(rows.indexOf(row), 1);
      row.element.remove();
      picker.focus();
    });
    rows.push(row);
    list.append(row.element);

    return row.element;
  }

  add.addEventListener('click', () => {
    const row = addRow(emptyCondition(picker.value as ConditionKind));
    row.querySelector<HTMLElement>('input, textarea, select')?.focus();
  });
  for (const condition of initial) {
    addRow(condition);
  }

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

  return {
    element: h(
      'fieldset',
      { className: 'border rounded p-2 mb-2', attrs: { id: uniqueId('condition') } },
      h('legend', { className: 'float-none w-auto fs-6 px-1', text: label }),
      h('div', { className: 'row g-2' }, ...fields),
      button,
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
        fields: [column(amount, `${label}: valor`), column(currency, `${label}: moeda`)],
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
      return {
        fields: [
          h('p', {
            className: 'col-12 mb-0',
            text: 'Vale quando o cliente não tem nenhum pedido anterior.',
          }),
        ],
        read: () => ({ kind: 'FirstOrderOfClient' }),
      };
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
