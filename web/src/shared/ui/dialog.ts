import { h } from './dom';
import { withIcon } from './icons';

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
  const confirm = withIcon(
    h('button', {
      className: 'btn btn-primary',
      text: options.confirmLabel,
      attrs: { type: 'submit' },
    }),
    'bi-check-lg',
    'me-1',
  );
  const cancel = withIcon(
    h('button', {
      className: 'btn btn-outline-secondary',
      text: 'Cancelar',
      attrs: { type: 'button' },
    }),
    'bi-x-lg',
    'me-1',
  );
  const form = h(
    'form',
    { attrs: { novalidate: '' } },
    h('h2', { className: 'h5', text: options.title, attrs: { id: `${id}-title` } }),
    ...(options.message === undefined ? [] : [h('p', { text: options.message })]),
    h('label', {
      className: 'form-label',
      text: options.required ? options.label : `${options.label} (opcional)`,
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
        error.textContent = `${options.label} é obrigatório.`;
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

export interface ConfirmDialog {
  readonly title: string;
  readonly message: string;
  readonly confirmLabel: string;
}

/** Asks for a yes in a modal dialog; resolves to false on Cancel and on Escape. */
export function confirmAction(options: ConfirmDialog): Promise<boolean> {
  const id = `confirm-dialog-${String(++dialogCount)}`;
  const opener = document.activeElement;
  const confirm = withIcon(
    h('button', {
      className: 'btn btn-primary',
      text: options.confirmLabel,
      attrs: { type: 'button' },
    }),
    'bi-check-lg',
    'me-1',
  );
  const cancel = withIcon(
    h('button', {
      className: 'btn btn-outline-secondary',
      text: 'Cancelar',
      attrs: { type: 'button' },
    }),
    'bi-x-lg',
    'me-1',
  );
  const dialog = h(
    'dialog',
    {
      className: 'rounded border p-3',
      attrs: { 'aria-labelledby': `${id}-title`, 'aria-describedby': `${id}-message` },
    },
    h('h2', { className: 'h5', text: options.title, attrs: { id: `${id}-title` } }),
    h('p', { text: options.message, attrs: { id: `${id}-message` } }),
    h('div', { className: 'd-flex justify-content-end gap-2 mt-3' }, cancel, confirm),
  );

  return new Promise((resolve) => {
    let answer = false;

    confirm.addEventListener('click', () => {
      answer = true;
      dialog.close();
    });
    cancel.addEventListener('click', () => {
      dialog.close();
    });
    dialog.addEventListener('close', () => {
      dialog.remove();
      if (opener instanceof HTMLElement) {
        opener.focus();
      }
      resolve(answer);
    });

    document.body.append(dialog);
    dialog.showModal();
    cancel.focus();
  });
}
