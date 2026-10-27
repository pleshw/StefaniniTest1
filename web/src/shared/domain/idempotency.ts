/** The part of Storage the keys need. */
export interface KeyStorage {
  getItem(name: string): string | null;
  setItem(name: string, value: string): void;
  removeItem(name: string): void;
}

export interface IdempotencyKeys {
  /**
   * The key for what is being ordered: made the first time, the same while the fingerprint of the cart is the same, a
   * new one once the cart changes. A retry after a timeout therefore sends the key of the first try.
   */
  keyFor(scope: string, fingerprint: string): string;
  /** Forgets the key, after the order was created (201). */
  clear(scope: string): void;
}

export interface IdempotencyOptions {
  /** sessionStorage by default; undefined means there is none, and the keys live in memory. */
  readonly storage?: KeyStorage | undefined;
  readonly randomUUID?: () => string;
}

interface StoredKey {
  readonly key: string;
  readonly fingerprint: string;
}

const prefix = 'orders.idempotency.';

export function createIdempotencyKeys(options: IdempotencyOptions = {}): IdempotencyKeys {
  const storage = 'storage' in options ? options.storage : browserStorage();
  const randomUUID = options.randomUUID ?? (() => crypto.randomUUID());
  // The copy in memory answers when the storage refuses to read or write (private mode, quota).
  const memory = new Map<string, StoredKey>();

  function read(scope: string): StoredKey | undefined {
    try {
      const text = storage?.getItem(prefix + scope);
      const stored = typeof text === 'string' ? parse(text) : undefined;

      if (stored !== undefined) {
        return stored;
      }
    } catch {
      // Falls through to the copy in memory.
    }

    return memory.get(scope);
  }

  function write(scope: string, stored: StoredKey): void {
    memory.set(scope, stored);
    try {
      storage?.setItem(prefix + scope, JSON.stringify(stored));
    } catch {
      // The copy in memory is enough for this page.
    }
  }

  return {
    keyFor(scope, fingerprint) {
      const current = read(scope);

      if (current?.fingerprint === fingerprint) {
        return current.key;
      }

      const created = { key: randomUUID(), fingerprint };
      write(scope, created);

      return created.key;
    },
    clear(scope) {
      memory.delete(scope);
      try {
        storage?.removeItem(prefix + scope);
      } catch {
        // Nothing stored, nothing to forget.
      }
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

function parse(text: string): StoredKey | undefined {
  try {
    const value: unknown = JSON.parse(text);

    if (typeof value === 'object' && value !== null && 'key' in value && 'fingerprint' in value) {
      const { key, fingerprint } = value;

      return typeof key === 'string' && typeof fingerprint === 'string'
        ? { key, fingerprint }
        : undefined;
    }
  } catch {
    // Text that is not JSON is not a key.
  }

  return undefined;
}
