import { h } from './dom';

let count = 0;

/** An id that is unique on the page, for tying a label to its control. */
export function uniqueId(prefix: string): string {
  return `${prefix}-${String(++count)}`;
}

/** A label above its control; the control gets an id if it has none. */
export function labelled(label: string, control: HTMLElement, hint?: string): HTMLElement {
  if (control.id === '') {
    control.id = uniqueId('field');
  }
  const hintId = `${control.id}-hint`;
  if (hint !== undefined) {
    control.setAttribute('aria-describedby', hintId);
  }

  return h(
    'div',
    { className: 'mb-3' },
    h('label', { className: 'form-label', text: label, attrs: { for: control.id } }),
    control,
    ...(hint === undefined
      ? []
      : [h('div', { className: 'form-text', text: hint, attrs: { id: hintId } })]),
  );
}

export function textInput(
  value = '',
  attrs: Readonly<Record<string, string>> = {},
): HTMLInputElement {
  const input = h('input', {
    className: 'form-control',
    attrs: { type: 'text', autocomplete: 'off', ...attrs },
  });
  input.value = value;

  return input;
}

/** A number box; an empty box reads as NaN, so it is never mistaken for 0. */
export function numberInput(
  value: number | undefined,
  attrs: Readonly<Record<string, string>> = {},
): HTMLInputElement {
  const input = h('input', {
    className: 'form-control',
    attrs: { type: 'number', step: 'any', ...attrs },
  });
  input.value = value === undefined || Number.isNaN(value) ? '' : String(value);

  return input;
}

export function textArea(value = '', rows = 2): HTMLTextAreaElement {
  const area = h('textarea', { className: 'form-control', attrs: { rows: String(rows) } });
  area.value = value;

  return area;
}

export function dateTimeInput(value: string): HTMLInputElement {
  const input = h('input', { className: 'form-control', attrs: { type: 'datetime-local' } });
  input.value = value;

  return input;
}

export function selectOf(
  values: readonly (readonly [string, string])[],
  selected: string,
): HTMLSelectElement {
  const select = h(
    'select',
    { className: 'form-select' },
    ...values.map(([value, text]) => h('option', { text, attrs: { value } })),
  );
  select.value = selected;

  return select;
}

export function checkbox(
  label: string,
  checked: boolean,
): { element: HTMLElement; input: HTMLInputElement } {
  const input = h('input', { className: 'form-check-input', attrs: { type: 'checkbox' } });
  input.id = uniqueId('check');
  input.checked = checked;

  return {
    input,
    element: h(
      'div',
      { className: 'form-check mb-2' },
      input,
      h('label', { className: 'form-check-label', text: label, attrs: { for: input.id } }),
    ),
  };
}

/** A list of problems the person must fix; focused so it is read out when it appears. */
export function errorSummary(): {
  element: HTMLElement;
  show: (messages: readonly string[], heading?: string) => void;
  clear: () => void;
} {
  const element = h('div', {
    className: 'alert alert-danger',
    attrs: { role: 'alert', tabindex: '-1' },
  });
  element.hidden = true;

  return {
    element,
    show(messages, heading = 'Corrija estes itens antes de continuar:') {
      element.replaceChildren(
        h('p', { className: 'mb-1 fw-semibold', text: heading }),
        h('ul', { className: 'mb-0' }, ...messages.map((message) => h('li', { text: message }))),
      );
      element.hidden = false;
      element.focus();
    },
    clear() {
      element.hidden = true;
      element.replaceChildren();
    },
  };
}

/** A button with text; a "button" type runs `onClick`, a "submit" type leaves the click to the form's submit event. */
export function actionButton(
  text: string,
  className: string,
  onClick: () => void,
  type: 'button' | 'submit' = 'button',
): HTMLButtonElement {
  const element = h('button', { className, text, attrs: { type } });
  if (type === 'button') {
    element.addEventListener('click', onClick);
  }

  return element;
}
