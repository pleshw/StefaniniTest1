import type { Locale } from './money';

const formatters = new Map<Locale, Intl.DateTimeFormat>();

/** A moment from the API (ISO 8601) in the viewer's time zone, such as "26 de out. de 2026 12:00". */
export function formatDateTime(iso: string, locale: Locale = 'pt-BR'): string {
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

const pad = (value: number): string => String(value).padStart(2, '0');

/** An ISO 8601 moment as the value of a datetime-local input (the viewer's time zone); empty if it is not a date. */
export function toLocalInput(iso: string | undefined): string {
  const moment = iso === undefined ? new Date(Number.NaN) : new Date(iso);

  return Number.isNaN(moment.getTime())
    ? ''
    : `${String(moment.getFullYear())}-${pad(moment.getMonth() + 1)}-${pad(moment.getDate())}T${pad(moment.getHours())}:${pad(moment.getMinutes())}`;
}

/** The value of a datetime-local input as an ISO 8601 moment in UTC; undefined if it is empty or not a date. */
export function fromLocalInput(value: string): string | undefined {
  const moment = new Date(value);

  return value === '' || Number.isNaN(moment.getTime()) ? undefined : moment.toISOString();
}
