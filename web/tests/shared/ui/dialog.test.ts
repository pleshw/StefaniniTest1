import { getByRole, getByLabelText, fireEvent } from '@testing-library/dom';
import { describe, expect, it } from 'vitest';
import { askReason, confirmAction } from '../../../src/shared/ui/dialog';
import { expectAccessible } from '../../helpers';

const hold = {
  title: 'Put order ORD-1 on hold',
  message: 'The order waits until an admin confirms or blocks it.',
  label: 'Reason',
  confirmLabel: 'Put on hold',
  required: true,
  maxLength: 500,
};

function opener(): HTMLButtonElement {
  const button = document.createElement('button');
  button.textContent = 'Hold';
  document.body.append(button);
  button.focus();

  return button;
}

const dialog = (): HTMLElement => getByRole(document.body, 'dialog', { name: hold.title });

describe('askReason', () => {
  it('resolves to the trimmed reason when it is confirmed', async () => {
    opener();
    const answer = askReason(hold);

    fireEvent.input(getByLabelText(dialog(), 'Reason'), {
      target: { value: '  Customer asked  ' },
    });
    fireEvent.click(getByRole(dialog(), 'button', { name: 'Put on hold' }));

    expect(await answer).toBe('Customer asked');
    expect(document.querySelector('dialog')).toBeNull();
  });

  it('refuses an empty reason when one is required, says why, and keeps the dialog open', async () => {
    opener();
    const answer = askReason(hold);
    let settled = false;
    void answer.then(() => {
      settled = true;
    });

    fireEvent.click(getByRole(dialog(), 'button', { name: 'Put on hold' }));
    await Promise.resolve();

    const field = getByLabelText(dialog(), 'Reason');
    expect(getByRole(dialog(), 'alert').textContent).toBe('Reason is required.');
    expect(field.getAttribute('aria-invalid')).toBe('true');
    expect(document.activeElement).toBe(field);
    expect(settled).toBe(false);

    fireEvent.input(field, { target: { value: 'ok' } });
    fireEvent.click(getByRole(dialog(), 'button', { name: 'Put on hold' }));
    expect(await answer).toBe('ok');
  });

  it('treats a blank reason as empty', async () => {
    opener();
    const answer = askReason(hold);

    fireEvent.input(getByLabelText(dialog(), 'Reason'), { target: { value: '   ' } });
    fireEvent.click(getByRole(dialog(), 'button', { name: 'Put on hold' }));
    expect(getByRole(dialog(), 'alert').textContent).toBe('Reason is required.');

    fireEvent.click(getByRole(dialog(), 'button', { name: 'Cancel' }));
    expect(await answer).toBeNull();
  });

  it('accepts an empty reason when it is optional, and says it is optional', async () => {
    opener();
    const answer = askReason({ ...hold, required: false });

    expect(getByLabelText(dialog(), 'Reason (optional)')).toBeTruthy();
    fireEvent.click(getByRole(dialog(), 'button', { name: 'Put on hold' }));

    expect(await answer).toBe('');
  });

  it('resolves to null on Cancel and on Escape, and gives the focus back', async () => {
    const button = opener();
    const cancelled = askReason(hold);
    fireEvent.click(getByRole(dialog(), 'button', { name: 'Cancel' }));
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
      label: 'Reason',
      confirmLabel: 'Go',
      required: true,
      maxLength: 20,
    });

    expect(getByLabelText(dialog(), 'Reason').getAttribute('maxlength')).toBe('20');
    expect(dialog().querySelectorAll('p')).toHaveLength(0);
    fireEvent.click(getByRole(dialog(), 'button', { name: 'Cancel' }));
    await answer;
  });

  it('has no accessibility violations, empty or with an error', async () => {
    opener();
    const answer = askReason(hold);

    await expectAccessible(dialog());
    fireEvent.click(getByRole(dialog(), 'button', { name: 'Put on hold' }));
    await expectAccessible(dialog());

    fireEvent.click(getByRole(dialog(), 'button', { name: 'Cancel' }));
    await answer;
  });
});

describe('confirmAction', () => {
  const options = {
    title: 'Replay this message?',
    message: 'It goes back through the exchange.',
    confirmLabel: 'Replay',
  };

  it('resolves to true on confirm, false on Cancel and false on Escape, with the focus given back', async () => {
    const button = opener();

    const yes = confirmAction(options);
    expect(getByRole(document.body, 'dialog', { name: options.title })).toBeTruthy();
    expect(document.activeElement).toBe(getByRole(document.body, 'button', { name: 'Cancel' }));
    fireEvent.click(getByRole(document.body, 'button', { name: 'Replay' }));
    expect(await yes).toBe(true);
    expect(document.activeElement).toBe(button);

    const no = confirmAction(options);
    fireEvent.click(getByRole(document.body, 'button', { name: 'Cancel' }));
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

    fireEvent.click(getByRole(document.body, 'button', { name: 'Cancel' }));
    await answer;
  });
});
