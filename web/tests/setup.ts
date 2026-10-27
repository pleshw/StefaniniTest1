import { afterAll, afterEach, beforeAll } from 'vitest';
import { server } from '../mocks/server';

// jsdom has no showModal/close on <dialog>; this stands in for the browser's behaviour that the views rely on.
HTMLDialogElement.prototype.showModal = function showModal(this: HTMLDialogElement): void {
  this.setAttribute('open', '');
};
HTMLDialogElement.prototype.close = function close(this: HTMLDialogElement): void {
  this.removeAttribute('open');
  this.dispatchEvent(new Event('close'));
};

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
