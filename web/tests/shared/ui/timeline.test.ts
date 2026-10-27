import { getAllByRole, getByRole } from '@testing-library/dom';
import { describe, expect, it } from 'vitest';
import { renderTimeline } from '../../../src/shared/ui/timeline';
import { expectAccessible } from '../../helpers';

describe('timeline', () => {
  it('lists each entry with its badge, label, time and note, and leaves out what is missing', async () => {
    const list = renderTimeline([
      { status: 'Pending', label: 'Placed', at: '2026-10-26T12:00:00Z' },
      { status: 'OnHold', note: 'Waiting for a check' },
      { status: 'Confirmed' },
    ]);
    document.body.append(list);

    const items = getAllByRole(list, 'listitem');
    expect(getByRole(document.body, 'list', { name: 'Status timeline' })).toBe(list);
    expect(items[0]?.textContent).toContain('PendingPlaced');
    expect(items[0]?.textContent).toContain('Oct 26, 2026');
    expect(items[1]?.textContent).toBe('On holdWaiting for a check');
    expect(items[2]?.textContent).toBe('Confirmed');
    await expectAccessible(list);
  });
});
