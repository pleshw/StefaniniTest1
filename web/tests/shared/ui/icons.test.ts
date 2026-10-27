import { getByRole } from '@testing-library/dom';
import { describe, expect, it } from 'vitest';
import { actionButton } from '../../../src/shared/ui/form';
import { icon, setIconText, withIcon } from '../../../src/shared/ui/icons';

describe('icons', () => {
  it('are hidden from assistive technology', () => {
    const glyph = icon('bi-cart3');

    expect(glyph.className).toBe('bi bi-cart3 me-2');
    expect(glyph.getAttribute('aria-hidden')).toBe('true');
  });

  it('go before the text and leave the accessible name alone', () => {
    document.body.append(withIcon(document.createElement('button'), 'bi-search'));
    const button = document.body.lastElementChild as HTMLButtonElement;
    button.append('Buscar');

    expect(button.firstElementChild?.classList.contains('bi-search')).toBe(true);
    expect(getByRole(document.body, 'button', { name: 'Buscar' })).toBe(button);
  });

  it('replace a changing label together with its text', () => {
    const button = document.createElement('button');
    setIconText(button, 'bi-pause-fill', 'Pausar');
    setIconText(button, 'bi-play-fill', 'Retomar');

    expect(button.textContent).toBe('Retomar');
    expect(button.querySelectorAll('.bi')).toHaveLength(1);
    expect(button.querySelector('.bi-play-fill')).not.toBeNull();
  });

  it('give the same action the same icon everywhere, and no icon to an unknown one', () => {
    const edit = actionButton('Editar', 'btn', () => undefined);
    const other = actionButton('Algo novo', 'btn', () => undefined);

    expect(edit.querySelector('.bi-pencil-square')).not.toBeNull();
    expect(edit.textContent).toBe('Editar');
    expect(other.querySelector('.bi')).toBeNull();
  });
});
