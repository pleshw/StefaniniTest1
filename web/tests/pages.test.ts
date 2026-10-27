import { getByRole } from '@testing-library/dom';
import axe from 'axe-core';
import { describe, expect, it } from 'vitest';
import adminHtml from '../admin.html?raw';
import shopHtml from '../shop.html?raw';
import { mountAdmin } from '../src/admin/ui/app';
import { mountShop } from '../src/shop/ui/app';
import { requireElement } from '../src/shared/ui/dom';

/** Loads a page's HTML into the test document, as the browser would before running its script. */
function load(html: string): void {
  const page = new DOMParser().parseFromString(html, 'text/html');
  document.documentElement.lang = page.documentElement.lang;
  document.title = page.title;
  document.body.replaceChildren(
    ...Array.from(page.body.children).filter((node) => node.tagName !== 'SCRIPT'),
  );
}

const pages = [
  { name: 'Shop', html: shopHtml, mount: mountShop },
  { name: 'Admin', html: adminHtml, mount: mountAdmin },
];

describe.each(pages)('$name page', ({ name, html, mount }) => {
  it('mounts with its landmarks and heading', () => {
    load(html);

    mount(requireElement('app'));

    expect(document.documentElement.lang).toBe('en');
    expect(document.title).toContain(name);
    expect(getByRole(document.body, 'banner')).toBeTruthy();
    expect(getByRole(document.body, 'navigation')).toBeTruthy();
    expect(getByRole(document.body, 'main')).toBeTruthy();
    expect(getByRole(document.body, 'heading', { level: 1, name })).toBeTruthy();
  });

  it('has no axe violations', async () => {
    load(html);
    mount(requireElement('app'));

    const results = await axe.run(document);

    expect(results.violations.map((violation) => violation.id)).toEqual([]);
  });
});

describe('requireElement', () => {
  it('fails loudly when the page has no such element', () => {
    expect(() => requireElement('missing')).toThrow('#missing');
  });
});
