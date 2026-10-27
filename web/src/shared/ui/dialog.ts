import { h } from './dom';

export interface ReasonDialog {
  readonly title: string;
  readonly message?: string;
  readonly label: string;
  readonly confirmLabel: string;
  readonly required: boolean;
  readonly maxLength: number;
}

let dialogCount = 0;

/**
 * Asks for a reason in a modal dialog (the native dialog element: it traps the focus and closes on Escape). Resolves
 * to the text, or to null if the person cancels. An empty reason is refused here when one is required.
 */
export function askReason(options: ReasonDialog): Promise<string | null> {
  const id = `reason-dialog-${String(++dialogCount)}`;
  const opener = document.activeElement;

  const field = h('textarea', {
    className: 'form-control',
    attrs: {
      id: `${id}-reason`,
      rows: '3',
      maxlength: String(options.maxLength),
      'aria-describedby': `${id}-error`,
    },
  });
  const error = h('div', {
    className: 'invalid-feedback',
    attrs: { id: `${id}-error`, role: 'alert' },
  });
  const confirm = h('button', {
    className: 'btn btn-primary',
    text: options.confirmLabel,
    attrs: { type: 'submit' },
  });
  const cancel = h('button', {
    className: 'btn btn-outline-secondary',
    text: 'Cancel',
    attrs: { type: 'button' },
  });
  const form = h(
    'form',
    { attrs: { novalidate: '' } },
    h('h2', { className: 'h5', text: options.title, attrs: { id: `${id}-title` } }),
    ...(options.message === undefined ? [] : [h('p', { text: options.message })]),
    h('label', {
      className: 'form-label',
      text: options.required ? options.label : `${options.label} (optional)`,
      attrs: { for: `${id}-reason` },
    }),
    field,
    error,
    h('div', { className: 'd-flex justify-content-end gap-2 mt-3' }, cancel, confirm),
  );
  const dialog = h(
    'dialog',
    { className: 'rounded border p-3', attrs: { 'aria-labelledby': `${id}-title` } },
    form,
  );

  return new Promise((resolve) => {
    let answer: string | null = null;

    form.addEventListener('submit', (event) => {
      event.preventDefault();
      const reason = field.value.trim();

      if (options.required && reason === '') {
        field.classList.add('is-invalid');
        field.setAttribute('aria-invalid', 'true');
        error.textContent = `${options.label} is required.`;
        field.focus();
        return;
      }
      answer = reason;
      dialog.close();
    });
    cancel.addEventListener('click', () => {
      dialog.close();
    });
    // Escape closes the dialog without a submit.
    dialog.addEventListener('close', () => {
      dialog.remove();
      if (opener instanceof HTMLElement) {
        opener.focus();
      }
      resolve(answer);
    });

    document.body.append(dialog);
    dialog.showModal();
    field.focus();
  });
}
