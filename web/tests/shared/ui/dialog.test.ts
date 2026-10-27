import { getByRole, getByLabelText, fireEvent } from '@testing-library/dom';
import { describe, expect, it } from 'vitest';
import { askReason, confirmAction } from '../../../src/shared/ui/dialog';
import { expectAccessible } from '../../helpers';

const hold = {
  title: 'Colocar o pedido ORD-1 em espera',
  message: 'O pedido aguarda até que um administrador o confirme ou bloqueie.',
  label: 'Motivo',
  confirmLabel: 'Colocar em espera',
  required: true,
  maxLength: 500,
};

function opener(): HTMLButtonElement {
  const button = document.createElement('button');
  button.textContent = 'Espera';
  document.body.append(button);
  button.focus();

  return button;
}

const dialog = (): HTMLElement => getByRole(document.body, 'dialog', { name: hold.title });

describe('askReason', () => {
  it('resolves to the trimmed reason when it is confirmed', async () => {
    opener();
    const answer = askReason(hold);

    fireEvent.input(getByLabelText(dialog(), 'Motivo'), {
      target: { value: '  Cliente pediu  ' },
    });
    fireEvent.click(getByRole(dialog(), 'button', { name: 'Colocar em espera' }));

    expect(await answer).toBe('Cliente pediu');
    expect(document.querySelector('dialog')).toBeNull();
  });

  it('refuses an empty reason when one is required, says why, and keeps the dialog open', async () => {
    opener();
    const answer = askReason(hold);
    let settled = false;
    void answer.then(() => {
      settled = true;
    });

    fireEvent.click(getByRole(dialog(), 'button', { name: 'Colocar em espera' }));
    await Promise.resolve();

    const field = getByLabelText(dialog(), 'Motivo');
    expect(getByRole(dialog(), 'alert').textContent).toBe('Motivo é obrigatório.');
    expect(field.getAttribute('aria-invalid')).toBe('true');
    expect(document.activeElement).toBe(field);
    expect(settled).toBe(false);

    fireEvent.input(field, { target: { value: 'ok' } });
    fireEvent.click(getByRole(dialog(), 'button', { name: 'Colocar em espera' }));
    expect(await answer).toBe('ok');
  });

  it('treats a blank reason as empty', async () => {
    opener();
    const answer = askReason(hold);

    fireEvent.input(getByLabelText(dialog(), 'Motivo'), { target: { value: '   ' } });
    fireEvent.click(getByRole(dialog(), 'button', { name: 'Colocar em espera' }));
    expect(getByRole(dialog(), 'alert').textContent).toBe('Motivo é obrigatório.');

    fireEvent.click(getByRole(dialog(), 'button', { name: 'Cancelar' }));
    expect(await answer).toBeNull();
  });

  it('accepts an empty reason when it is optional, and says it is optional', async () => {
    opener();
    const answer = askReason({ ...hold, required: false });

    expect(getByLabelText(dialog(), 'Motivo (opcional)')).toBeTruthy();
    fireEvent.click(getByRole(dialog(), 'button', { name: 'Colocar em espera' }));

    expect(await answer).toBe('');
  });

  it('resolves to null on Cancel and on Escape, and gives the focus back', async () => {
    const button = opener();
    const cancelled = askReason(hold);
    fireEvent.click(getByRole(dialog(), 'button', { name: 'Cancelar' }));
    expect(await cancelled).toBeNull();
    expect(document.activeElement).toBe(button);

    const escaped = askReason(hold);
    document.querySelector('dialog')?.close();
    expect(await escaped).toBeNull();
    expect(document.activeElement).toBe(button);
  });

  it('limits the reason to the length Orders accepts and has no message when none is given', async () => {
    opener();
    const answer = askReason({
      title: hold.title,
      label: 'Motivo',
      confirmLabel: 'Ir',
      required: true,
      maxLength: 20,
    });

    expect(getByLabelText(dialog(), 'Motivo').getAttribute('maxlength')).toBe('20');
    expect(dialog().querySelectorAll('p')).toHaveLength(0);
    fireEvent.click(getByRole(dialog(), 'button', { name: 'Cancelar' }));
    await answer;
  });

  it('has no accessibility violations, empty or with an error', async () => {
    opener();
    const answer = askReason(hold);

    await expectAccessible(dialog());
    fireEvent.click(getByRole(dialog(), 'button', { name: 'Colocar em espera' }));
    await expectAccessible(dialog());

    fireEvent.click(getByRole(dialog(), 'button', { name: 'Cancelar' }));
    await answer;
  });
});

describe('confirmAction', () => {
  const options = {
    title: 'Reprocessar esta mensagem?',
    message: 'Ela volta para a exchange.',
    confirmLabel: 'Reprocessar',
  };

  it('resolves to true on confirm, false on Cancel and false on Escape, with the focus given back', async () => {
    const button = opener();

    const yes = confirmAction(options);
    expect(getByRole(document.body, 'dialog', { name: options.title })).toBeTruthy();
    expect(document.activeElement).toBe(getByRole(document.body, 'button', { name: 'Cancelar' }));
    fireEvent.click(getByRole(document.body, 'button', { name: 'Reprocessar' }));
    expect(await yes).toBe(true);
    expect(document.activeElement).toBe(button);

    const no = confirmAction(options);
    fireEvent.click(getByRole(document.body, 'button', { name: 'Cancelar' }));
    expect(await no).toBe(false);

    const escaped = confirmAction(options);
    document.querySelector('dialog')?.close();
    expect(await escaped).toBe(false);
    expect(document.querySelector('dialog')).toBeNull();
  });

  it('describes itself to screen readers and has no violations', async () => {
    opener();
    const answer = confirmAction(options);

    const dialog = getByRole(document.body, 'dialog', { name: options.title });
    expect(dialog.getAttribute('aria-describedby')).toBeTruthy();
    await expectAccessible(dialog);

    fireEvent.click(getByRole(document.body, 'button', { name: 'Cancelar' }));
    await answer;
  });
});
