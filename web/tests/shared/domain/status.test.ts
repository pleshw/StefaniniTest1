import { describe, expect, it } from 'vitest';
import { isOrderStatus, orderStatuses, statusView } from '../../../src/shared/domain/status';

describe('statusView', () => {
  it.each([
    ['Pending', 'Pending', 'text-bg-secondary', 'bi-hourglass-split'],
    ['OnHold', 'On hold', 'text-bg-warning', 'bi-pause-circle'],
    ['Confirmed', 'Confirmed', 'text-bg-success', 'bi-check-circle'],
    ['Blocked', 'Blocked', 'text-bg-danger', 'bi-slash-circle'],
    ['Cancelled', 'Cancelled', 'text-bg-dark', 'bi-x-circle'],
    ['Failed', 'Failed', 'text-bg-danger', 'bi-exclamation-triangle'],
  ])('describes %s', (status, label, badgeClass, icon) => {
    expect(statusView(status)).toEqual({ label, badgeClass, icon });
  });

  it('describes a status it does not know by its own name, so a new status never breaks a page', () => {
    expect(statusView('Shipped')).toEqual({
      label: 'Shipped',
      badgeClass: 'text-bg-light',
      icon: 'bi-question-circle',
    });
  });

  it('describes every known status', () => {
    for (const status of orderStatuses) {
      expect(statusView(status).icon).not.toBe('bi-question-circle');
    }
  });
});

describe('isOrderStatus', () => {
  it('tells the known statuses from other text', () => {
    expect(isOrderStatus('OnHold')).toBe(true);
    expect(isOrderStatus('onhold')).toBe(false);
    expect(isOrderStatus('')).toBe(false);
  });
});
