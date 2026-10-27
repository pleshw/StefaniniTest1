import type { Locale } from './money';

const formatters = new Map<Locale, Intl.DateTimeFormat>();

/** A moment from the API (ISO 8601) in the viewer's time zone, such as "Oct 26, 2026, 12:00 PM". */
export function formatDateTime(iso: string, locale: Locale = 'en-US'): string {
  const moment = new Date(iso);

  if (Number.isNaN(moment.getTime())) {
    return iso;
  }

  let formatter = formatters.get(locale);
  if (formatter === undefined) {
    formatter = new Intl.DateTimeFormat(locale, { dateStyle: 'medium', timeStyle: 'short' });
    formatters.set(locale, formatter);
  }

  return formatter.format(moment);
}
