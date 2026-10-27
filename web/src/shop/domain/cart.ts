import type { KeyStorage } from '../../shared/domain/idempotency';

export const maxQuantity = 99;

export interface CartLine {
  readonly sku: string;
  readonly name: string;
  readonly quantity: number;
}

export interface Cart {
  lines(): readonly CartLine[];
  /** A product already in the cart gets one more. */
  add(product: { readonly sku: string; readonly name: string }): void;
  remove(sku: string): void;
  /** Rounded down and kept between 1 and the largest quantity; anything that is not a number is ignored. */
  setQuantity(sku: string, quantity: number): void;
  clear(): void;
  /** Tells the same cart from a changed one: the currency and the lines, in any order. */
  fingerprint(currency: string): string;
  /** Called after every change; returns the function that stops it. */
  subscribe(listener: () => void): () => void;
}

export interface CartOptions {
  /** sessionStorage by default (a cart per tab); undefined means there is none, and the cart lives in memory. */
  readonly storage?: KeyStorage | undefined;
  /** The name the cart is kept under, so the Shop cart and the admin's cart for a client are two carts. */
  readonly storageKey?: string;
}

const defaultStorageKey = 'orders.cart';

export function createCart(options: CartOptions = {}): Cart {
  const storage = 'storage' in options ? options.storage : browserStorage();
  const storageKey = options.storageKey ?? defaultStorageKey;
  const listeners = new Set<() => void>();
  let current: readonly CartLine[] = load(storage, storageKey);

  function update(next: readonly CartLine[]): void {
    current = next;
    try {
      storage?.setItem(storageKey, JSON.stringify(next));
    } catch {
      // The cart in memory is enough for this page.
    }
    for (const listener of [...listeners]) {
      listener();
    }
  }

  return {
    lines: () => current,
    add(product) {
      const existing = current.find((line) => line.sku === product.sku);

      update(
        existing === undefined
          ? [...current, { sku: product.sku, name: product.name, quantity: 1 }]
          : current.map((line) =>
              line === existing
                ? { ...line, quantity: Math.min(line.quantity + 1, maxQuantity) }
                : line,
            ),
      );
    },
    remove(sku) {
      if (current.some((line) => line.sku === sku)) {
        update(current.filter((line) => line.sku !== sku));
      }
    },
    setQuantity(sku, quantity) {
      if (!Number.isFinite(quantity)) {
        return;
      }
      const clamped = Math.min(Math.max(Math.floor(quantity), 1), maxQuantity);
      const existing = current.find((line) => line.sku === sku);

      if (existing !== undefined && existing.quantity !== clamped) {
        update(current.map((line) => (line === existing ? { ...line, quantity: clamped } : line)));
      }
    },
    clear() {
      update([]);
    },
    fingerprint(currency) {
      const lines = [...current]
        .sort((a, b) => a.sku.localeCompare(b.sku))
        .map((line) => `${line.sku}:${String(line.quantity)}`);

      return `${currency}|${lines.join(',')}`;
    },
    subscribe(listener) {
      listeners.add(listener);

      return () => {
        listeners.delete(listener);
      };
    },
  };
}

function browserStorage(): KeyStorage | undefined {
  try {
    return window.sessionStorage;
  } catch {
    // Reading the property itself throws when the browser blocks storage.
    return undefined;
  }
}

function load(storage: KeyStorage | undefined, storageKey: string): readonly CartLine[] {
  try {
    const text = storage?.getItem(storageKey);
    const value: unknown = text === null || text === undefined ? [] : JSON.parse(text);

    return Array.isArray(value) ? value.filter(isLine) : [];
  } catch {
    // Unreadable storage or text that is not JSON: start with an empty cart.
    return [];
  }
}

function isLine(value: unknown): value is CartLine {
  return (
    typeof value === 'object' &&
    value !== null &&
    'sku' in value &&
    'name' in value &&
    'quantity' in value &&
    typeof value.sku === 'string' &&
    typeof value.name === 'string' &&
    typeof value.quantity === 'number' &&
    Number.isInteger(value.quantity) &&
    value.quantity >= 1
  );
}
