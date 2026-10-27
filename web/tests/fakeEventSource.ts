import type { EventSourceLike, MessageLike } from '../src/shared/api/sse';

/** Stands in for the browser's EventSource: a test decides when it opens, emits events and fails. */
export class FakeSource implements EventSourceLike {
  static readonly connecting = 0;
  static readonly closed = 2;
  readyState = 0;
  onopen: ((event: Event) => void) | null = null;
  onerror: ((event: Event) => void) | null = null;
  closeCalls = 0;
  private readonly listeners = new Map<string, (event: MessageLike) => void>();

  constructor(readonly url: string) {}

  addEventListener(name: string, listener: (event: MessageLike) => void): void {
    this.listeners.set(name, listener);
  }

  close(): void {
    this.closeCalls++;
    this.readyState = FakeSource.closed;
  }

  emit(name: string, data: string, lastEventId: string): void {
    this.listeners.get(name)?.({ data, lastEventId });
  }

  listening(): string[] {
    return [...this.listeners.keys()];
  }
}
