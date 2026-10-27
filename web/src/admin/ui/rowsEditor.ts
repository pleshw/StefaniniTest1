import { h } from '../../shared/ui/dom';
import { actionButton } from '../../shared/ui/form';
import { legendOf } from '../../shared/ui/help';

/** Rows that can be added and removed, each with its own fields; `describe` names a row for the Remove button. */
export function rowsEditor<T>(
  legend: string,
  addLabel: string,
  removeLabel: string,
  initial: readonly T[],
  fresh: () => T,
  fieldsOf: (value: T) => { fields: HTMLElement[]; read: () => T },
  help?: string,
): { element: HTMLElement; read: () => T[] } {
  const rows: { element: HTMLElement; remove: HTMLButtonElement; read: () => T }[] = [];
  const list = h('div');
  const add = actionButton(addLabel, 'btn btn-outline-primary btn-sm', () => {
    addRow(fresh()).querySelector<HTMLElement>('input')?.focus();
  });

  function addRow(value: T): HTMLElement {
    const { fields, read } = fieldsOf(value);
    const remove = actionButton('Remover', 'btn btn-outline-danger btn-sm', () => {
      rows.splice(rows.indexOf(row), 1);
      row.element.remove();
      renumber();
      add.focus();
    });
    const row = {
      element: h(
        'div',
        { className: 'row g-2 align-items-end mb-2' },
        ...fields,
        h('div', { className: 'col-auto mb-3' }, remove),
      ),
      remove,
      read,
    };
    rows.push(row);
    list.append(row.element);
    renumber();

    return row.element;
  }

  function renumber(): void {
    rows.forEach((row, index) => {
      row.remove.setAttribute('aria-label', `${removeLabel} ${String(index + 1)}`);
    });
  }

  for (const value of initial) {
    addRow(value);
  }

  const element = h('fieldset', { className: 'mb-3' });
  element.append(legendOf(element, legend, { ...(help === undefined ? {} : { help }) }), list, add);

  return { element, read: () => rows.map((row) => row.read()) };
}
