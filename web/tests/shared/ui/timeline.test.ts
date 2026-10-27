import { getAllByRole, getByRole } from '@testing-library/dom';
import { describe, expect, it } from 'vitest';
import { renderTimeline } from '../../../src/shared/ui/timeline';
import { expectAccessible } from '../../helpers';

describe('timeline', () => {
  it('lists each entry with its badge, label, time and note, and leaves out what is missing', async () => {
    const list = renderTimeline([
      { status: 'Pending', label: 'Pedido realizado', at: '2026-10-26T12:00:00Z' },
      { status: 'OnHold', note: 'Aguardando uma verificação' },
      { status: 'Confirmed' },
    ]);
    document.body.append(list);

    const items = getAllByRole(list, 'listitem');
    expect(getByRole(document.body, 'list', { name: 'Linha do tempo do status' })).toBe(list);
    expect(items[0]?.textContent).toContain('PendentePedido realizado');
    expect(items[0]?.textContent).toContain('26 de out. de 2026');
    expect(items[1]?.textContent).toBe('Em esperaAguardando uma verificação');
    expect(items[2]?.textContent).toBe('Confirmado');
    await expectAccessible(list);
  });
});
