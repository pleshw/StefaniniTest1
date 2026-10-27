import { describe, expect, it } from 'vitest';
import { formatMoney } from '../../../src/shared/domain/money';

/** Intl separates the symbol from the digits with a no-break space in some locales. */
const plain = (text: string): string => text.replaceAll(' ', ' ');

describe('formatMoney', () => {
  it.each([
    [1234.5, 'BRL', 'en-US', 'R$1,234.50'],
    [1234.5, 'BRL', 'pt-BR', 'R$ 1.234,50'],
    [1234.5, 'USD', 'en-US', '$1,234.50'],
    [1234.5, 'USD', 'pt-BR', 'US$ 1.234,50'],
    [0, 'BRL', 'en-US', 'R$0.00'],
    [-5.1, 'USD', 'en-US', '-$5.10'],
  ] as const)('formats %d %s for %s', (amount, currency, locale, expected) => {
    expect(plain(formatMoney(amount, currency, locale))).toBe(expected);
  });

  it('defaults to en-US, the language of the pages', () => {
    expect(formatMoney(10, 'USD')).toBe('$10.00');
  });

  it('rounds to the cents of the currency', () => {
    expect(formatMoney(0.005, 'USD')).toBe('$0.01');
    expect(formatMoney(10.999, 'USD')).toBe('$11.00');
  });

  it('shows the amount with its code when the currency is not a valid code', () => {
    expect(formatMoney(12.5, 'not a code')).toBe('12.50 not a code');
  });
});
