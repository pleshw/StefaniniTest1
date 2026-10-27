import { describe, expect, it } from 'vitest';
import { formatDateTime } from '../../../src/shared/domain/dates';

describe('formatDateTime', () => {
  it('writes the date and time the way the locale does', () => {
    expect(formatDateTime('2026-10-26T12:00:00Z', 'en-US')).toMatch(
      /^Oct 26, 2026, \d{1,2}:\d{2}\s?[AP]M$/,
    );
    expect(formatDateTime('2026-10-26T12:00:00Z', 'pt-BR')).toMatch(/^26 de out\. de 2026/);
  });

  it('defaults to en-US and reuses the formatter', () => {
    expect(formatDateTime('2026-10-26T12:00:00Z')).toBe(
      formatDateTime('2026-10-26T12:00:00Z', 'en-US'),
    );
  });

  it('gives back text that is not a date as it is', () => {
    expect(formatDateTime('yesterday')).toBe('yesterday');
  });
});
