import { fireEvent, getByLabelText, getByRole } from '@testing-library/dom';
import { describe, expect, it } from 'vitest';
import { checkbox, formStep, labelled } from '../../../src/shared/ui/form';
import { helpTip } from '../../../src/shared/ui/help';
import { expectAccessible } from '../../helpers';

describe('help tips', () => {
  it('describe the (?) button and the field, without changing their names', async () => {
    const field = document.createElement('input');
    const main = document.createElement('main');
    main.append(labelled('Prioridade', field, undefined, 'O menor valor roda primeiro.'));
    document.body.append(main);

    const button = getByRole(document.body, 'button', { name: 'Ajuda: Prioridade' });
    const tip = getByRole(document.body, 'tooltip', { hidden: true });

    expect(tip.textContent).toBe('O menor valor roda primeiro.');
    expect(button.getAttribute('aria-describedby')).toBe(tip.id);
    expect(getByLabelText(document.body, 'Prioridade')).toBe(field);
    expect(field.getAttribute('aria-describedby')).toBe(tip.id);
    await expectAccessible();
  });

  it('open on a tap, close on blur, and Escape hides them until the pointer leaves', () => {
    const { element } = helpTip('Cumulativa', 'Soma com as outras.');
    document.body.append(element);
    const button = getByRole(document.body, 'button', { name: 'Ajuda: Cumulativa' });

    fireEvent.click(button);
    expect(element.classList).toContain('is-open');
    fireEvent.click(button);
    expect(element.classList).not.toContain('is-open');

    fireEvent.click(button);
    fireEvent.keyDown(button, { key: 'Escape' });
    expect(element.classList).not.toContain('is-open');
    expect(element.classList).toContain('is-dismissed');
    fireEvent.mouseLeave(element);
    expect(element.classList).not.toContain('is-dismissed');

    fireEvent.click(button);
    fireEvent.blur(button);
    expect(element.classList).not.toContain('is-open');
  });

  it('name a step by its title alone, and sit next to a checkbox', () => {
    const { element } = checkbox('Ativa', true, 'Desmarcada, não vale.');
    document.body.append(formStep(1, 'Identificação', 'Como a regra aparece.', element));

    expect(getByRole(document.body, 'group', { name: 'Identificação' })).toBeTruthy();
    expect(getByRole(document.body, 'button', { name: 'Ajuda: Ativa' })).toBeTruthy();
    expect(getByLabelText(document.body, 'Ativa').getAttribute('aria-describedby')).toBeTruthy();
  });
});
