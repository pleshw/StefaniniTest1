import { withIcon } from './icons';

/** The icon next to the site's name. */
export const brandIcon = 'bi-box-seam';

export interface NavLink {
  readonly label: string;
  readonly href: string;
  /** A Bootstrap Icons name shown before the label. */
  readonly icon: string;
}

export interface PageShell {
  /** Where each page renders its views. */
  readonly content: HTMLElement;
}

/**
 * The frame every page shares: a skip link, a banner with the navigation and one main landmark with
 * the page heading. Built with DOM calls, never HTML strings, so no text is ever parsed as markup.
 */
export function renderShell(
  root: HTMLElement,
  title: string,
  links: readonly NavLink[],
): PageShell {
  const skip = element('a', 'visually-hidden-focusable', 'Pular para o conteúdo');
  skip.href = '#content';

  const brand = withIcon(element('span', 'navbar-brand', 'Pedidos e Catálogo'), brandIcon);
  const list = element('ul', 'navbar-nav');
  for (const link of links) {
    const item = element('li', 'nav-item');
    const anchor = withIcon(element('a', 'nav-link', link.label), link.icon, 'me-1');
    anchor.href = link.href;
    if (link.label === title) {
      anchor.setAttribute('aria-current', 'page');
    }
    item.append(anchor);
    list.append(item);
  }

  const nav = element('nav', 'container d-flex align-items-center gap-3');
  nav.setAttribute('aria-label', 'Páginas');
  nav.append(brand, list);
  const header = element('header', 'navbar navbar-expand bg-body-tertiary border-bottom');
  header.append(nav);

  const main = element('main', 'container py-4');
  main.id = 'content';
  main.tabIndex = -1;
  const content = element('div');
  main.append(element('h1', 'h3 mb-3', title), content);

  root.replaceChildren(skip, header, main);

  return { content };
}

function element<K extends keyof HTMLElementTagNameMap>(
  tag: K,
  className?: string,
  text?: string,
): HTMLElementTagNameMap[K] {
  const node = document.createElement(tag);
  if (className !== undefined) {
    node.className = className;
  }
  if (text !== undefined) {
    node.textContent = text;
  }
  return node;
}
