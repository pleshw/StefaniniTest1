export type Locale = 'pt-BR' | 'en-US';

const formatters = new Map<string, Intl.NumberFormat>();

/** An amount in a currency, written the way the locale does ("R$ 1.234,50" in pt-BR, "R$1,234.50" in en-US). */
export function formatMoney(amount: number, currency: string, locale: Locale = 'en-US'): string {
  const key = `${locale}|${currency}`;
  let formatter = formatters.get(key);

  if (formatter === undefined) {
    try {
      formatter = new Intl.NumberFormat(locale, { style: 'currency', currency });
    } catch (error) {
      // Only a malformed code throws; the page still shows the amount instead of failing to render.
      if (!(error instanceof RangeError)) {
        throw error;
      }

      return `${amount.toFixed(2)} ${currency}`;
    }
    formatters.set(key, formatter);
  }

  return formatter.format(amount);
}
