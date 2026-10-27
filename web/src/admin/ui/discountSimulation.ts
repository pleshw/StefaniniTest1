import {
  listDiscountRules,
  previewDiscountRule,
  type DiscountRule,
  type RulePreview,
} from '../api/rules';
import { discountDefinitionOf, toDiscountRequest } from '../domain/rules';
import type { DiscountDraft } from './discountRules';
import { rowsEditor } from './rowsEditor';
import type { HttpClient } from '../../shared/api/http';
import { formatMoney } from '../../shared/domain/money';
import { h } from '../../shared/ui/dom';
import { errorMessage } from '../../shared/ui/errors';
import {
  actionButton,
  errorSummary,
  labelled,
  numberInput,
  selectOf,
  textInput,
} from '../../shared/ui/form';
import { withIcon } from '../../shared/ui/icons';

export interface DiscountSimulationOptions {
  readonly http: HttpClient;
  /** Goes back to the rule editor the draft came from. */
  readonly onBack: () => void;
}

export interface DiscountSimulation {
  /** Selects the editor's draft as the rule to simulate. */
  simulate(draft: DiscountDraft): void;
  /** Reads the saved rules again, for the picker and for naming the rules applied. */
  refresh(): void;
}

const activeOnly = '';
const draftOption = 'draft';

/** What to simulate, and how its result names it. */
interface Choice {
  readonly rule?: object;
  readonly ruleId?: string;
  /** The rule whose effect the result calls out; none when only the active rules are priced. */
  readonly name?: string;
  /** Whether an applied rule is the chosen one. */
  readonly isChosen: (ruleId: string) => boolean;
}

/**
 * The discount simulation: a sample cart priced with the active rules, alone or with one more rule in place of its
 * saved version (a saved rule, inactive ones as if active, or the editor's draft). Nothing is stored.
 */
export function mountDiscountSimulation(
  container: HTMLElement,
  options: DiscountSimulationOptions,
): DiscountSimulation {
  let rules: readonly DiscountRule[] = [];
  let draft: DiscountDraft | undefined;

  const picker = selectOf([], activeOnly);
  const currency = textInput('BRL', { maxlength: '3' });
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
    'Os produtos e quantidades de um carrinho imaginário, com os SKUs do catálogo.',
  );
  const problem = errorSummary();
  const result = h('section', {
    className: 'preview-result',
    attrs: { 'aria-label': 'Resultado da simulação' },
  });
  result.hidden = true;
  const listFailure = h('p', {
    className: 'alert alert-warning',
    text: 'Não foi possível carregar as regras salvas; dá para simular só as regras ativas.',
  });
  listFailure.hidden = true;
  const back = withIcon(
    h('button', {
      className: 'btn btn-outline-secondary btn-sm mb-3',
      text: 'Voltar ao editor',
      attrs: { type: 'button' },
    }),
    'bi-arrow-left',
    'me-1',
  );
  back.hidden = true;
  back.addEventListener('click', options.onBack);

  const form = h(
    'form',
    { attrs: { 'aria-label': 'Simulação', novalidate: '' } },
    h(
      'div',
      { className: 'row' },
      h(
        'div',
        { className: 'col-md-8' },
        labelled(
          'Regra a simular',
          picker,
          undefined,
          'Só as regras ativas: o que um cliente pagaria hoje. Uma regra salva ou o rascunho do editor: as regras ativas mais ela, no lugar da versão salva; uma regra inativa entra como se estivesse ativa. Nada é salvo.',
        ),
      ),
      h(
        'div',
        { className: 'col-md-4' },
        labelled('Moeda', currency, undefined, 'Código de 3 letras, como BRL.'),
      ),
    ),
    items.element,
    actionButton('Simular', 'btn btn-primary mb-3', () => undefined, 'submit'),
    problem.element,
    result,
  );
  form.addEventListener('submit', (event) => {
    event.preventDefault();
    void run();
  });

  container.append(
    h(
      'section',
      { attrs: { 'aria-labelledby': 'simulation-heading' } },
      withIcon(
        h('h2', {
          className: 'h4',
          text: 'Simulação de descontos',
          attrs: { id: 'simulation-heading' },
        }),
        'bi-calculator',
      ),
      back,
      listFailure,
      form,
    ),
  );

  function renderPicker(): void {
    const selected = picker.value;
    picker.replaceChildren(
      h('option', { text: 'Só as regras ativas', attrs: { value: activeOnly } }),
      ...(draft === undefined
        ? []
        : [
            h('option', {
              text: `Rascunho do editor: ${draft.definition.name.trim()}`,
              attrs: { value: draftOption },
            }),
          ]),
      ...rules.map((rule) =>
        h('option', {
          text: `${rule.name} (versão ${String(rule.version)}${rule.isActive ? '' : ', inativa'})`,
          attrs: { value: rule.id },
        }),
      ),
    );
    picker.value = [...picker.options].some((option) => option.value === selected)
      ? selected
      : activeOnly;
  }

  function choice(): Choice {
    if (picker.value === draftOption && draft !== undefined) {
      const { definition, existing } = draft;

      return {
        rule: toDiscountRequest(definition),
        ...(existing === undefined ? {} : { ruleId: existing.id }),
        name: definition.name.trim(),
        // A new rule is priced under an id Orders makes up, so it is the one rule this page does not know.
        isChosen: (id) =>
          existing === undefined ? !rules.some((rule) => rule.id === id) : id === existing.id,
      };
    }
    const saved = rules.find((rule) => rule.id === picker.value);
    if (saved !== undefined) {
      return {
        rule: toDiscountRequest({ ...discountDefinitionOf(saved), isActive: true }),
        ruleId: saved.id,
        name: saved.name,
        isChosen: (id) => id === saved.id,
      };
    }

    return { isChosen: () => false };
  }

  async function run(): Promise<void> {
    problem.clear();
    const cart = items.read().filter((item) => item.sku !== '');
    if (
      cart.length === 0 ||
      !cart.every((item) => Number.isInteger(item.quantity) && item.quantity >= 1)
    ) {
      result.hidden = true;
      problem.show(
        ['Adicione ao carrinho de exemplo pelo menos um SKU com quantidade 1 ou mais.'],
        'Sem simulação:',
      );
      return;
    }
    const chosen = choice();

    try {
      const preview = await previewDiscountRule(
        options.http,
        { currency: currency.value.trim().toUpperCase(), items: cart },
        chosen.rule,
        chosen.ruleId,
      );
      result.replaceChildren(...previewRows(preview, rules, chosen));
      result.hidden = false;
    } catch (error) {
      result.hidden = true;
      problem.show([errorMessage(error, 'A simulação falhou.')], 'Sem simulação:');
    }
  }

  async function load(): Promise<void> {
    try {
      rules = await listDiscountRules(options.http);
      listFailure.hidden = true;
    } catch {
      listFailure.hidden = false;
    }
    renderPicker();
  }

  renderPicker();
  void load();

  return {
    simulate(next) {
      draft = next;
      renderPicker();
      picker.value = draftOption;
      back.hidden = false;
      result.hidden = true;
      problem.clear();
      form.querySelector<HTMLElement>('input')?.focus();
    },
    refresh() {
      void load();
    },
  };
}

function previewRows(
  preview: RulePreview,
  rules: readonly DiscountRule[],
  chosen: Choice,
): HTMLElement[] {
  const money = (amount: number): string => formatMoney(amount, preview.quote.currency);
  const nameOf = (id: string): string =>
    chosen.name !== undefined && chosen.isChosen(id)
      ? chosen.name
      : (rules.find((rule) => rule.id === id)?.name ?? id.slice(0, 8));
  const mine = preview.applied.find((applied) => chosen.isChosen(applied.ruleId));

  return [
    ...(chosen.name === undefined
      ? []
      : [
          mine === undefined
            ? withIcon(
                h('p', {
                  className: 'fw-semibold',
                  text: `"${chosen.name}" não se aplicou a este carrinho.`,
                }),
                'bi-dash-circle',
              )
            : withIcon(
                h('p', {
                  className: 'fw-semibold',
                  text: `"${chosen.name}" se aplicou: -${money(mine.amount)}.`,
                }),
                'bi-check-circle-fill',
              ),
        ]),
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
