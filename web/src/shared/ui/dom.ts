/** The element a page mounts into; a missing one is a broken page, so it fails loudly. */
export function requireElement(id: string): HTMLElement {
  const element = document.getElementById(id);

  if (element === null) {
    throw new Error(`The page has no #${id} element.`);
  }

  return element;
}
