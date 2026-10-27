import { describe, expect, it } from 'vitest';
import { createIdempotencyKeys, type KeyStorage } from '../../../src/shared/domain/idempotency';

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

function counter() {
  let next = 0;
  return () => `key-${String(++next)}`;
}

const unavailable: KeyStorage = {
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

describe('idempotency keys', () => {
  it('creates a key the first time and reuses it while the cart is the same', () => {
    const keys = createIdempotencyKeys({ storage: memoryStorage(), randomUUID: counter() });

    expect(keys.keyFor('shop', 'cart-a')).toBe('key-1');
    expect(keys.keyFor('shop', 'cart-a')).toBe('key-1');
  });

  it('replaces the key when the cart changes', () => {
    const keys = createIdempotencyKeys({ storage: memoryStorage(), randomUUID: counter() });

    keys.keyFor('shop', 'cart-a');

    expect(keys.keyFor('shop', 'cart-b')).toBe('key-2');
    expect(keys.keyFor('shop', 'cart-b')).toBe('key-2');
  });

  it('forgets the key on clear, so the next order gets a new one', () => {
    const keys = createIdempotencyKeys({ storage: memoryStorage(), randomUUID: counter() });

    keys.keyFor('shop', 'cart-a');
    keys.clear('shop');

    expect(keys.keyFor('shop', 'cart-a')).toBe('key-2');
  });

  it('keeps a key per scope', () => {
    const keys = createIdempotencyKeys({ storage: memoryStorage(), randomUUID: counter() });

    expect(keys.keyFor('shop', 'cart')).toBe('key-1');
    expect(keys.keyFor('admin:client-1', 'cart')).toBe('key-2');
    expect(keys.keyFor('shop', 'cart')).toBe('key-1');
  });

  it('survives a page reload through sessionStorage', () => {
    const storage = memoryStorage();

    createIdempotencyKeys({ storage, randomUUID: counter() }).keyFor('shop', 'cart-a');
    const reloaded = createIdempotencyKeys({ storage, randomUUID: counter() });

    expect(reloaded.keyFor('shop', 'cart-a')).toBe('key-1');
  });

  it.each([
    'not json',
    '{"key":1,"fingerprint":"cart-a"}',
    'null',
    '[]',
    '{"fingerprint":"cart-a"}',
  ])('ignores stored text that is not a key: %s', (bad) => {
    const storage = memoryStorage();
    storage.data.set('orders.idempotency.shop', bad);
    const keys = createIdempotencyKeys({ storage, randomUUID: counter() });

    expect(keys.keyFor('shop', 'cart-a')).toBe('key-1');
  });

  it('falls back to memory when sessionStorage is unavailable', () => {
    const keys = createIdempotencyKeys({ storage: unavailable, randomUUID: counter() });

    expect(keys.keyFor('shop', 'cart-a')).toBe('key-1');
    expect(keys.keyFor('shop', 'cart-a')).toBe('key-1');
    expect(keys.keyFor('shop', 'cart-b')).toBe('key-2');
    keys.clear('shop');
    expect(keys.keyFor('shop', 'cart-b')).toBe('key-3');
  });

  it('keeps the key in memory when the storage reads fine but refuses to write', () => {
    const storage = memoryStorage();
    const full: KeyStorage = {
      ...storage,
      setItem: () => {
        throw new DOMException('full', 'QuotaExceededError');
      },
    };
    const keys = createIdempotencyKeys({ storage: full, randomUUID: counter() });

    expect(keys.keyFor('shop', 'cart-a')).toBe('key-1');
    expect(keys.keyFor('shop', 'cart-a')).toBe('key-1');
  });

  it('falls back to memory when the browser has no sessionStorage at all', () => {
    const keys = createIdempotencyKeys({ storage: undefined, randomUUID: counter() });

    expect(keys.keyFor('shop', 'cart')).toBe('key-1');
    expect(keys.keyFor('shop', 'cart')).toBe('key-1');
  });

  it('uses the browser by default and makes a real UUID', () => {
    const keys = createIdempotencyKeys();
    const key = keys.keyFor('default-test', 'cart');

    expect(key).toMatch(/^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/);
    expect(keys.keyFor('default-test', 'cart')).toBe(key);
    keys.clear('default-test');
  });
});
