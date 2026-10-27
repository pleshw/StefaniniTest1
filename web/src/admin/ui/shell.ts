import { h } from '../../shared/ui/dom';
import { icon } from '../../shared/ui/icons';
import { brandIcon, type NavLink } from '../../shared/ui/layout';

/** One entry of the side menu: a section of this page, with what it is for. */
export interface MenuItem<S extends string> {
  readonly section: S;
  readonly label: string;
  /** A Bootstrap Icons name shown before the label. */
  readonly icon: string;
  /** What the section is for, under the label and as the button's description. */
  readonly hint: string;
  /** Sections that belong to this one, listed under it, indented. */
  readonly children?: readonly MenuItem<S>[];
}

export interface MenuGroup<S extends string> {
  readonly title: string;
  readonly items: readonly MenuItem<S>[];
}

export interface AdminShell<S extends string> {
  /** Where the sections render. */
  readonly content: HTMLElement;
  /** The menu button of each section. */
  readonly buttons: ReadonlyMap<S, HTMLButtonElement>;
  /** Marks the section as the current one in the menu and in the page header. */
  select(section: S): void;
  /** Where the signed-in user's name goes. */
  readonly user: HTMLElement;
  /** The "Sign out" button. */
  readonly signOut: HTMLButtonElement;
}

/**
 * The Admin page frame: a side menu that is always open (a banner with the site name, the sections grouped by what
 * they are for, the other pages and the signed-in user) and one main landmark with the page heading and the current
 * section's purpose. On narrow screens the menu sits above the content instead of beside it, still open.
 */
export function renderAdminShell<S extends string>(
  root: HTMLElement,
  title: string,
  groups: readonly MenuGroup<S>[],
  links: readonly NavLink[],
): AdminShell<S> {
  const skip = h('a', {
    className: 'visually-hidden-focusable',
    text: 'Pular para o conteúdo',
    attrs: { href: '#content' },
  });

  const brand = h(
    'a',
    { className: 'admin-brand', attrs: { href: '/' } },
    icon(`${brandIcon} admin-brand-icon`, ''),
    h(
      'span',
      { className: 'admin-brand-text' },
      h('span', { className: 'admin-brand-name', text: 'Pedidos e Catálogo' }),
      h('span', { className: 'admin-brand-role', text: 'Painel de administração' }),
    ),
  );

  const buttons = new Map<S, HTMLButtonElement>();
  const items = new Map<S, MenuItem<S>>();
  /** The parent of each sub-item, marked while one of its sub-items is the current section. */
  const parents = new Map<S, S>();
  const menuButton = (item: MenuItem<S>, className: string): HTMLButtonElement => {
    const hintId = `admin-menu-hint-${item.section}`;
    const button = h(
      'button',
      { className, attrs: { type: 'button', 'aria-describedby': hintId } },
      icon(`${item.icon} admin-menu-icon`, ''),
      h(
        'span',
        { className: 'admin-menu-text' },
        h('span', { className: 'admin-menu-label', text: item.label }),
        // Hidden from the button's name, which stays the label; read as its description instead.
        h('span', {
          className: 'admin-menu-hint',
          text: item.hint,
          attrs: { id: hintId, 'aria-hidden': 'true' },
        }),
      ),
    );
    buttons.set(item.section, button);
    items.set(item.section, item);

    return button;
  };
  const menu = h('nav', {
    className: 'admin-menu',
    attrs: { 'aria-label': 'Menu da administração' },
  });
  groups.forEach((group, index) => {
    const headingId = `admin-menu-group-${String(index)}`;
    const list = h('ul', { className: 'admin-menu-list', attrs: { 'aria-labelledby': headingId } });
    for (const item of group.items) {
      const entry = h('li', {}, menuButton(item, 'admin-menu-item'));
      const children = item.children ?? [];
      if (children.length > 0) {
        entry.append(
          h(
            'ul',
            { className: 'admin-submenu', attrs: { 'aria-label': `Dentro de ${item.label}` } },
            ...children.map((child) => {
              parents.set(child.section, item.section);
              return h('li', {}, menuButton(child, 'admin-menu-item admin-submenu-item'));
            }),
          ),
        );
      }
      list.append(entry);
    }
    menu.append(
      h('p', { className: 'admin-menu-heading', text: group.title, attrs: { id: headingId } }),
      list,
    );
  });

  const pagesList = h(
    'ul',
    { className: 'admin-menu-list', attrs: { 'aria-labelledby': 'admin-menu-pages' } },
    ...links
      .filter((link) => link.label !== title)
      .map((link) =>
        h(
          'li',
          {},
          h(
            'a',
            { className: 'admin-menu-item', attrs: { href: link.href } },
            icon(`${link.icon} admin-menu-icon`, ''),
            h('span', { className: 'admin-menu-label', text: `Ir para ${link.label}` }),
          ),
        ),
      ),
  );
  menu.append(
    h('p', {
      className: 'admin-menu-heading',
      text: 'Outras páginas',
      attrs: { id: 'admin-menu-pages' },
    }),
    pagesList,
  );

  const user = h('span', { className: 'admin-user-name', text: '…' });
  const signOut = h(
    'button',
    { className: 'btn btn-outline-dark btn-sm', attrs: { type: 'button' } },
    icon('bi-box-arrow-right', 'me-1'),
    'Sair',
  );
  const account = h(
    'div',
    { className: 'admin-account' },
    h(
      'p',
      { className: 'admin-user mb-0' },
      icon('bi-person-circle', 'me-2'),
      h('span', { className: 'visually-hidden', text: 'Conectado como ' }),
      user,
    ),
    signOut,
  );

  const header = h('header', { className: 'admin-sidebar' }, brand, menu, account);

  const sectionIcon = h('span', {
    className: 'admin-current-icon',
    attrs: { 'aria-hidden': 'true' },
  });
  const sectionName = h('span', { className: 'admin-current-name' });
  const sectionHint = h('span', { className: 'admin-current-hint' });
  const current = h(
    'p',
    { className: 'admin-current' },
    sectionIcon,
    h('span', {}, sectionName, sectionHint),
  );
  const content = h('div', { className: 'admin-content' });
  const main = h(
    'main',
    { className: 'admin-main', attrs: { id: 'content', tabindex: '-1' } },
    h('h1', { className: 'admin-title', text: title }),
    current,
    content,
  );

  root.replaceChildren(skip, h('div', { className: 'admin-layout' }, header, main));

  return {
    content,
    buttons,
    user,
    signOut,
    select(section) {
      for (const [name, button] of buttons) {
        const selected = name === section;
        button.classList.toggle('active', selected);
        button.classList.toggle('has-current', parents.get(section) === name);
        button.setAttribute('aria-current', String(selected));
      }
      const item = items.get(section);
      if (item !== undefined) {
        sectionIcon.replaceChildren(icon(item.icon, ''));
        sectionName.textContent = item.label;
        sectionHint.textContent = item.hint;
      }
    },
  };
}
