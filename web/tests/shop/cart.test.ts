import { describe, expect, it, vi } from 'vitest';
import type { KeyStorage } from '../../src/shared/domain/idempotency';
import { createCart, maxQuantity } from '../../src/shop/domain/cart';

function memoryStorage(): KeyStorage & { readonly data: Map<string, string> } {
  const data = new Map<string, string>();

  return {
    data,
    getItem: (name) => data.get(name) ?? null,
    setItem: (name, value) => {
      data.set(name, value);
    },
    removeItem: (name) => {
      data.delete(name);
    },
  };
}

const desk = { sku: 'DESK-1', name: 'Standing desk' };
const mug = { sku: 'MUG-1', name: 'Coffee mug' };

describe('cart', () => {
  it('starts empty', () => {
    expect(createCart({ storage: memoryStorage() }).lines()).toEqual([]);
  });

  it('adds a product, and adding it again raises its quantity', () => {
    const cart = createCart({ storage: memoryStorage() });

    cart.add(desk);
    cart.add(mug);
    cart.add(desk);

    expect(cart.lines()).toEqual([
      { sku: 'DESK-1', name: 'Standing desk', quantity: 2 },
      { sku: 'MUG-1', name: 'Coffee mug', quantity: 1 },
    ]);
  });

  it('does not go past the largest quantity', () => {
    const cart = createCart({ storage: memoryStorage() });
    cart.add(desk);

    cart.setQuantity('DESK-1', maxQuantity);
    cart.add(desk);

    expect(cart.lines()[0]?.quantity).toBe(maxQuantity);
  });

  it('removes a line and ignores a line it does not have', () => {
    const cart = createCart({ storage: memoryStorage() });
    cart.add(desk);
    cart.add(mug);

    cart.remove('DESK-1');
    cart.remove('NOPE');

    expect(cart.lines().map((line) => line.sku)).toEqual(['MUG-1']);
  });

  it('sets a quantity within the limits, rounding down, and ignores what is not a number', () => {
    const cart = createCart({ storage: memoryStorage() });
    cart.add(desk);

    cart.setQuantity('DESK-1', 5.9);
    expect(cart.lines()[0]?.quantity).toBe(5);
    cart.setQuantity('DESK-1', 0);
    expect(cart.lines()[0]?.quantity).toBe(1);
    cart.setQuantity('DESK-1', -3);
    expect(cart.lines()[0]?.quantity).toBe(1);
    cart.setQuantity('DESK-1', maxQuantity + 50);
    expect(cart.lines()[0]?.quantity).toBe(maxQuantity);
    cart.setQuantity('DESK-1', Number.NaN);
    expect(cart.lines()[0]?.quantity).toBe(maxQuantity);
    cart.setQuantity('NOPE', 4);
    expect(cart.lines()).toHaveLength(1);
  });

  it('empties the cart', () => {
    const cart = createCart({ storage: memoryStorage() });
    cart.add(desk);

    cart.clear();

    expect(cart.lines()).toEqual([]);
  });

  it('tells its listeners about each change, and stops after unsubscribe', () => {
    const cart = createCart({ storage: memoryStorage() });
    const listener = vi.fn();
    const unsubscribe = cart.subscribe(listener);

    cart.add(desk);
    cart.setQuantity('DESK-1', 2);
    cart.remove('DESK-1');
    cart.clear();
    unsubscribe();
    cart.add(mug);

    expect(listener).toHaveBeenCalledTimes(4);
  });

  it('does not notify when nothing changed', () => {
    const cart = createCart({ storage: memoryStorage() });
    const listener = vi.fn();
    cart.add(desk);
    cart.subscribe(listener);

    cart.remove('NOPE');
    cart.setQuantity('NOPE', 3);
    cart.setQuantity('DESK-1', 1);

    expect(listener).not.toHaveBeenCalled();
  });

  it('keeps the cart for the tab: a reload finds it again', () => {
    const storage = memoryStorage();
    createCart({ storage }).add(desk);

    expect(createCart({ storage }).lines()).toEqual([{ ...desk, quantity: 1 }]);
  });

  it.each([
    'not json',
    'null',
    '{}',
    '[1]',
    '[{"sku":"A"}]',
    '[{"sku":"A","name":"A","quantity":0}]',
    '[{"sku":"A","name":"A","quantity":"2"}]',
  ])('starts empty when the stored cart is not a cart: %s', (bad) => {
    const storage = memoryStorage();
    storage.data.set('orders.cart', bad);

    expect(createCart({ storage }).lines()).toEqual([]);
  });

  it('works in memory when sessionStorage is unavailable', () => {
    const blocked: KeyStorage = {
      getItem: () => {
        throw new DOMException('denied', 'SecurityError');
      },
      setItem: () => {
        throw new DOMException('full', 'QuotaExceededError');
      },
      removeItem: () => {
        throw new DOMException('denied', 'SecurityError');
      },
    };
    const cart = createCart({ storage: blocked });

    cart.add(desk);
    cart.add(desk);
    cart.clear();
    cart.add(mug);

    expect(cart.lines()).toEqual([{ ...mug, quantity: 1 }]);
  });

  it('works in memory when there is no sessionStorage, and uses the browser by default', () => {
    const cart = createCart({ storage: undefined });
    cart.add(desk);
    expect(cart.lines()).toHaveLength(1);

    const browser = createCart();
    browser.add(mug);
    expect(createCart().lines()).toEqual([{ ...mug, quantity: 1 }]);
    browser.clear();
  });
});

describe('cart fingerprint', () => {
  it('is the same for the same cart in any order, and changes with a line, a quantity or the currency', () => {
    const first = createCart({ storage: memoryStorage() });
    first.add(desk);
    first.add(mug);
    const second = createCart({ storage: memoryStorage() });
    second.add(mug);
    second.add(desk);

    expect(first.fingerprint('BRL')).toBe(second.fingerprint('BRL'));
    expect(first.fingerprint('BRL')).not.toBe(first.fingerprint('USD'));
    second.setQuantity('DESK-1', 2);
    expect(first.fingerprint('BRL')).not.toBe(second.fingerprint('BRL'));
    second.remove('DESK-1');
    expect(first.fingerprint('BRL')).not.toBe(second.fingerprint('BRL'));
  });
});
