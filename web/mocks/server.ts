import { setupServer } from 'msw/node';
import { handlers } from './handlers';

/** The BFF stubs for Vitest (Node). */
export const server = setupServer(...handlers);
