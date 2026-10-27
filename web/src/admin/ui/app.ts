import { renderShell } from '../../shared/ui/layout';
import { pageLinks } from '../../shared/ui/pages';

/** The Admin page. Live events, order actions, rules, dead letters and the catalog arrive as views next. */
export function mountAdmin(root: HTMLElement): void {
  const { content } = renderShell(root, 'Admin', pageLinks);
  const intro = document.createElement('p');
  intro.textContent = 'Watch orders arrive, act on them and manage rules and the catalog.';
  content.append(intro);
}
