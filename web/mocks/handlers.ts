import { http, HttpResponse } from 'msw';

/**
 * BFF stubs, so pages and tests can run without the back end. Each front-end step adds the routes it
 * uses; the shapes follow the BFF's answers.
 */
export const handlers = [
  http.get('*/bff/user', () =>
    HttpResponse.json({
      sub: 'a1d3e5f7-2b4c-4d6e-8f01-3c5e7a9b0d22',
      name: 'admin',
      roles: ['Admin'],
    }),
  ),
];
