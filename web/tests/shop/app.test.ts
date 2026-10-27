import { findByRole, fireEvent, getByRole, queryByRole } from '@testing-library/dom';
import { http, HttpResponse } from 'msw';
import { describe, expect, it, vi } from 'vitest';
import { server } from '../../mocks/server';
import { mountShop } from '../../src/shop/ui/app';
import { expectAccessible } from '../helpers';

function mountPage() {
  const root = document.createElement('div');
  document.body.append(root);
  mountShop(root);

  return root;
}

describe('Shop page sections', () => {
  it('opens on the shop and loads the orders only when "My orders" is opened', async () => {
    let orderCalls = 0;
    server.use(
      http.get('*/bff/shop/orders', () => {
        orderCalls++;
        return HttpResponse.json({ items: [], next: null });
      }),
    );
    const root = mountPage();
    await findByRole(root, 'heading', { level: 3, name: 'Standing desk' });

    expect(orderCalls).toBe(0);
    expect(getByRole(root, 'button', { name: 'Loja' }).getAttribute('aria-current')).toBe('true');
    expect(queryByRole(root, 'heading', { name: 'Meus pedidos' })).toBeNull();

    fireEvent.click(getByRole(root, 'button', { name: 'Meus pedidos' }));

    expect(await findByRole(root, 'heading', { level: 2, name: 'Meus pedidos' })).toBeTruthy();
    expect(getByRole(root, 'button', { name: 'Meus pedidos' }).getAttribute('aria-current')).toBe(
      'true',
    );
    expect(queryByRole(root, 'heading', { name: 'Catálogo' })).toBeNull();
    await vi.waitFor(() => {
      expect(orderCalls).toBe(1);
    });

    fireEvent.click(getByRole(root, 'button', { name: 'Loja' }));
    expect(await findByRole(root, 'heading', { name: 'Catálogo' })).toBeTruthy();
    await expectAccessible(root);
  });
});
