import { h } from './dom';
import { icon } from './icons';

let count = 0;

function uniqueId(prefix: string): string {
  return `${prefix}-${String(++count)}`;
}

export interface Help {
  /** The (?) button with its tooltip, to place next to a label or a title. */
  readonly element: HTMLElement;
  /** The explanation; its id can describe a field, and its text can change. */
  readonly tip: HTMLElement;
}

/**
 * A (?) button whose explanation shows on hover, on keyboard focus and on a tap (for touch screens). The tooltip can be
 * hovered without closing and Escape closes it (WCAG 1.4.13); a screen reader reads it as the button's description.
 */
export function helpTip(topic: string, text: string): Help {
  const tip = h('span', {
    className: 'help-tip',
    text,
    attrs: { id: uniqueId('help'), role: 'tooltip' },
  });
  const button = h(
    'button',
    {
      className: 'help-toggle',
      attrs: { type: 'button', 'aria-label': `Ajuda: ${topic}`, 'aria-describedby': tip.id },
    },
    icon('bi-question-circle', ''),
  );
  const element = h('span', { className: 'help' }, button, tip);
  const close = (): void => {
    element.classList.remove('is-open');
  };

  button.addEventListener('click', () => {
    element.classList.remove('is-dismissed');
    element.classList.toggle('is-open');
  });
  button.addEventListener('blur', close);
  button.addEventListener('keydown', (event) => {
    if (event.key === 'Escape') {
      close();
      element.classList.add('is-dismissed');
    }
  });
  element.addEventListener('mouseleave', () => {
    element.classList.remove('is-dismissed');
  });
  button.addEventListener('focus', () => {
    element.classList.remove('is-dismissed');
  });

  return { element, tip };
}

/** Adds an element's id to a control's description, after whatever already describes it. */
export function describeWith(control: HTMLElement, id: string): void {
  const current = control.getAttribute('aria-describedby');
  control.setAttribute('aria-describedby', current === null ? id : `${current} ${id}`);
}

/**
 * A fieldset's legend with an optional step number and a (?). The fieldset is named by the title alone (through
 * `aria-labelledby`), so the button's name does not end up in the group's name.
 */
export function legendOf(
  fieldset: HTMLFieldSetElement,
  title: string,
  options: { step?: number; help?: string; className?: string } = {},
): HTMLLegendElement {
  const titleId = uniqueId('legend');
  const legend = h(
    'legend',
    { className: options.className ?? 'legend-with-help h6' },
    ...(options.step === undefined
      ? []
      : [
          h('span', {
            className: 'step-number',
            text: String(options.step),
            attrs: { 'aria-hidden': 'true' },
          }),
        ]),
    h('span', { text: title, attrs: { id: titleId } }),
  );
  fieldset.setAttribute('aria-labelledby', titleId);
  if (options.help !== undefined) {
    const help = helpTip(title, options.help);
    legend.append(help.element);
    describeWith(fieldset, help.tip.id);
  }

  return legend;
}
