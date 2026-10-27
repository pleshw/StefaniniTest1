/** The element a page mounts into; a missing one is a broken page, so it fails loudly. */
export function requireElement(id: string): HTMLElement {
  const element = document.getElementById(id);

  if (element === null) {
    throw new Error(`The page has no #${id} element.`);
  }

  return element;
}

export interface ElementProps {
  readonly className?: string;
  /** Set as text, never parsed as markup. */
  readonly text?: string;
  /** Every other attribute: id, type, href, aria-*. */
  readonly attrs?: Readonly<Record<string, string>>;
}

/** Builds an element with DOM calls, so no text is ever parsed as markup. */
export function h<K extends keyof HTMLElementTagNameMap>(
  tag: K,
  props: ElementProps = {},
  ...children: readonly (Node | string)[]
): HTMLElementTagNameMap[K] {
  const node = document.createElement(tag);

  if (props.className !== undefined) {
    node.className = props.className;
  }
  if (props.text !== undefined) {
    node.textContent = props.text;
  }
  for (const [name, value] of Object.entries(props.attrs ?? {})) {
    node.setAttribute(name, value);
  }
  node.append(...children);

  return node;
}
