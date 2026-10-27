import { h } from './dom';

/** A decorative Bootstrap Icons glyph: hidden from assistive technology, so the text next to it carries the meaning. */
export function icon(name: string, className = 'me-2'): HTMLElement {
  return h('i', { className: `bi ${name} ${className}`.trim(), attrs: { 'aria-hidden': 'true' } });
}

/** Puts a decorative icon before the element's content; the element's text and accessible name stay the same. */
export function withIcon<T extends HTMLElement>(element: T, name: string, className = 'me-2'): T {
  element.prepend(icon(name, className));
  return element;
}

/** Replaces the element's content with an icon and a text, for buttons whose label changes. */
export function setIconText(element: HTMLElement, name: string, text: string): void {
  element.replaceChildren(icon(name, 'me-1'), text);
}

/** The icon each action label carries on every page, so the same action always looks the same. */
export const actionIcons: Readonly<Record<string, string>> = {
  Buscar: 'bi-search',
  'Carregar mais': 'bi-chevron-double-down',
  'Tentar novamente': 'bi-arrow-clockwise',
  Recarregar: 'bi-arrow-clockwise',
  Atualizar: 'bi-arrow-clockwise',
  Editar: 'bi-pencil-square',
  Desativar: 'bi-toggle-off',
  'Nova regra': 'bi-plus-lg',
  'Adicionar valor': 'bi-plus-lg',
  'Adicionar faixa': 'bi-plus-lg',
  'Adicionar item de exemplo': 'bi-plus-lg',
  'Salvar regra': 'bi-floppy',
  'Salvar produto': 'bi-floppy',
  'Simular regra': 'bi-calculator',
  Simular: 'bi-calculator',
  Reprocessar: 'bi-arrow-repeat',
  Remover: 'bi-trash',
  Fechar: 'bi-x-lg',
  Cancelar: 'bi-x-lg',
};
