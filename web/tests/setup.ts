import { afterAll, afterEach, beforeAll } from 'vitest';
import { server } from '../mocks/server';

// A request no stub answers fails the test instead of reaching the network.
beforeAll(() => {
  server.listen({ onUnhandledFrame: 'error' });
});
afterEach(() => {
  server.resetHandlers();
  document.body.replaceChildren();
});
afterAll(() => {
  server.close();
});
