import { describe, expect, it } from 'vitest';
import { formatDateTime, fromLocalInput, toLocalInput } from '../../../src/shared/domain/dates';

describe('formatDateTime', () => {
  it('writes the date and time the way the locale does', () => {
    expect(formatDateTime('2026-10-26T12:00:00Z', 'en-US')).toMatch(
      /^Oct 26, 2026, \d{1,2}:\d{2}\s?[AP]M$/,
    );
    expect(formatDateTime('2026-10-26T12:00:00Z', 'pt-BR')).toMatch(/^26 de out\. de 2026/);
  });

  it('defaults to pt-BR and reuses the formatter', () => {
    expect(formatDateTime('2026-10-26T12:00:00Z')).toBe(
      formatDateTime('2026-10-26T12:00:00Z', 'pt-BR'),
    );
  });

  it('gives back text that is not a date as it is', () => {
    expect(formatDateTime('yesterday')).toBe('yesterday');
  });
});

describe('datetime-local conversion', () => {
  it('goes from a local input value to UTC and back to the same input value', () => {
    const iso = fromLocalInput('2026-10-26T09:30');

    expect(iso).toBe(new Date(2026, 9, 26, 9, 30).toISOString());
    expect(toLocalInput(iso)).toBe('2026-10-26T09:30');
  });

  it('answers nothing for an empty value or one that is not a date', () => {
    expect(fromLocalInput('')).toBeUndefined();
    expect(fromLocalInput('soon')).toBeUndefined();
    expect(toLocalInput(undefined)).toBe('');
    expect(toLocalInput('soon')).toBe('');
  });
});
