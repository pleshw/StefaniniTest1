import {
  createConfirmationRule,
  deactivateConfirmationRule,
  listConfirmationRules,
  replaceConfirmationRule,
  type ConfirmationRule,
} from '../api/rules';
import {
  confirmationConditionKinds,
  confirmationDefinitionOf,
  toConfirmationRequest,
  validateConfirmation,
  type ConfirmationDefinition,
} from '../domain/rules';
import { createConditionsEditor } from './conditionsEditor';
import { PreconditionFailedError } from '../../shared/api/errors';
import type { HttpClient } from '../../shared/api/http';
import { confirmAction } from '../../shared/ui/dialog';
import { h } from '../../shared/ui/dom';
import { errorMessage } from '../../shared/ui/errors';
import {
  actionButton,
  checkbox,
  errorSummary,
  labelled,
  numberInput,
  selectOf,
  textInput,
} from '../../shared/ui/form';

export interface ConfirmationRulesOptions {
  readonly http: HttpClient;
}

const staleRule = 'This rule was changed by someone else. Reload the list and open it again.';

/**
 * Confirmation rules: an active Hold rule that matches a new order sends it to OnHold for an admin; otherwise the
 * Worker confirms it. The list, and an editor with a conditions builder limited to what these rules understand.
 */
export function mountConfirmationRules(
  container: HTMLElement,
  options: ConfirmationRulesOptions,
): void {
  let rules: readonly ConfirmationRule[] = [];
  let loaded = false;
  let failure: string | undefined;
  let generation = 0;

  const status = h('p', { attrs: { role: 'status' } });
  const alertBox = h('div', {
    className: 'alert alert-danger d-flex align-items-center gap-3',
    attrs: { role: 'alert' },
  });
  const create = actionButton('New rule', 'btn btn-primary btn-sm mb-2', () => {
    status.textContent = '';
    openEditor();
  });
  const body = h('tbody');
  const table = h(
    'table',
    { className: 'table align-middle' },
    h('caption', { className: 'visually-hidden', text: 'Confirmation rules' }),
    h(
      'thead',
      {},
      h(
        'tr',
        {},
        ...['Name', 'Action', 'Priority', 'Version', 'State', 'Actions'].map((name) =>
          h('th', { text: name, attrs: { scope: 'col' } }),
        ),
      ),
    ),
    body,
  );
  const empty = h('p', { text: 'There are no confirmation rules.' });
  const editor = h('div');

  container.append(
    h(
      'section',
      { attrs: { 'aria-labelledby': 'confirmation-heading' } },
      h('h2', {
        className: 'h4',
        text: 'Confirmation rules',
        attrs: { id: 'confirmation-heading' },
      }),
      h('p', {
        className: 'text-body-secondary',
        text: 'A new order that matches an active Hold rule waits in the OnHold queue; any other order is confirmed.',
      }),
      create,
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

  function rowOf(rule: ConfirmationRule): HTMLElement {
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
      h('td', { text: rule.action }),
      h('td', { text: String(rule.priority) }),
      h('td', { text: String(rule.version) }),
      h('td', { text: rule.isActive ? 'Active' : 'Inactive' }),
      actions,
    );
  }

  async function load(): Promise<void> {
    const mine = ++generation;
    failure = undefined;
    try {
      const found = await listConfirmationRules(options.http);
      if (mine !== generation) {
        return;
      }
      rules = found;
    } catch (error) {
      if (mine !== generation) {
        return;
      }
      failure = errorMessage(error, 'The confirmation rules could not be loaded.');
    }
    loaded = true;
    render();
  }

  async function deactivateRule(rule: ConfirmationRule): Promise<void> {
    const yes = await confirmAction({
      title: `Deactivate "${rule.name}"?`,
      message: 'New orders stop being checked against this rule from now on.',
      confirmLabel: 'Deactivate',
    });

    if (!yes) {
      return;
    }
    let problem: string | undefined;
    try {
      const stored = await deactivateConfirmationRule(options.http, rule.id, rule.version);
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

  function openEditor(rule?: ConfirmationRule): void {
    const form = buildEditor(options.http, rule, {
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

  render();
  void load();
}

interface EditorEvents {
  readonly saved: (rule: ConfirmationRule) => void;
  readonly closed: () => void;
  readonly stale: () => void;
}

function buildEditor(
  http: HttpClient,
  existing: ConfirmationRule | undefined,
  events: EditorEvents,
): HTMLElement {
  const initial: ConfirmationDefinition =
    existing === undefined
      ? { name: '', action: 'Hold', conditions: [], priority: 0, isActive: true }
      : confirmationDefinitionOf(existing);
  const summary = errorSummary();
  const name = textInput(initial.name);
  const action = selectOf([['Hold', 'Hold (send to the OnHold queue)']], initial.action);
  const priority = numberInput(initial.priority, { step: '1' });
  const active = checkbox('Active', initial.isActive);
  const conditions = createConditionsEditor(confirmationConditionKinds, initial.conditions);
  let saving = false;

  async function save(): Promise<void> {
    const definition: ConfirmationDefinition = {
      name: name.value,
      action: 'Hold',
      conditions: conditions.read(),
      priority: priority.valueAsNumber,
      isActive: active.input.checked,
    };
    const problems = validateConfirmation(definition);

    if (problems.length > 0) {
      summary.show(problems);
      return;
    }
    if (saving) {
      return;
    }
    summary.clear();
    saving = true;
    try {
      const body = toConfirmationRequest(definition);
      const stored =
        existing === undefined
          ? await createConfirmationRule(http, body)
          : await replaceConfirmationRule(http, existing.id, existing.version, body);
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

  const form = h(
    'form',
    {
      className: 'border rounded p-3 mt-3',
      attrs: { 'aria-label': 'Confirmation rule editor', novalidate: '' },
    },
    h('h3', {
      className: 'h5',
      text:
        existing === undefined
          ? 'New confirmation rule'
          : `Edit ${existing.name} (version ${String(existing.version)})`,
    }),
    summary.element,
    h(
      'div',
      { className: 'row' },
      h('div', { className: 'col-md-6' }, labelled('Name', name)),
      h('div', { className: 'col-md-3' }, labelled('Action', action)),
      h('div', { className: 'col-md-3' }, labelled('Priority', priority, 'Lower runs first.')),
    ),
    active.element,
    conditions.element,
    h(
      'div',
      { className: 'd-flex gap-2' },
      actionButton('Save rule', 'btn btn-primary', () => void save(), 'submit'),
      actionButton('Cancel', 'btn btn-outline-secondary', events.closed),
    ),
  );
  form.addEventListener('submit', (event) => {
    event.preventDefault();
    void save();
  });

  return form;
}
