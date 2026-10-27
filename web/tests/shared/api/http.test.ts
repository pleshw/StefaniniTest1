import { http, HttpResponse } from 'msw';
import { describe, expect, it, vi } from 'vitest';
import { server } from '../../../mocks/server';
import {
  ApiError,
  ConflictError,
  ForbiddenError,
  NetworkError,
  NotFoundError,
  PreconditionFailedError,
  ServerError,
  ServiceUnavailableError,
  UnauthorizedError,
  ValidationError,
} from '../../../src/shared/api/errors';
import {
  createHttpClient,
  entityTag,
  loginUrl,
  maxRetries,
  type HttpClientOptions,
} from '../../../src/shared/api/http';

const problem = (status: number, extra: object = {}) =>
  HttpResponse.json({ title: 'Problem', status, ...extra }, { status });

function client(options: HttpClientOptions = {}) {
  const sleep = vi.fn(() => Promise.resolve());
  const redirectToLogin = vi.fn();

  return {
    sleep,
    redirectToLogin,
    http: createHttpClient({
      baseUrl: 'http://localhost:8080/shop.html',
      currentPage: () => '/shop.html?x=1',
      sleep,
      redirectToLogin,
      ...options,
    }),
  };
}

describe('requests', () => {
  it('reads JSON, sends no X-CSRF on a GET and leaves out empty query values', async () => {
    let seen: Request | undefined;
    server.use(
      http.get('*/bff/shop/products', ({ request }) => {
        seen = request;
        return HttpResponse.json({ items: [] });
      }),
    );

    const page = await client().http.get<{ items: unknown[] }>('/bff/shop/products', {
      q: 'desk',
      category: '',
      after: undefined,
      limit: 12,
    });

    expect(page).toEqual({ items: [] });
    expect(seen?.headers.get('x-csrf')).toBeNull();
    expect(new URL(seen?.url ?? '').search).toBe('?q=desk&limit=12');
  });

  it('sends a JSON body with X-CSRF 1 on unsafe methods, plus the key and the quoted version', async () => {
    let seen: Request | undefined;
    let body: unknown;
    server.use(
      http.post('*/bff/admin/orders/1/hold', async ({ request }) => {
        seen = request.clone();
        body = await request.json();
        return HttpResponse.json({ ok: true }, { status: 201, headers: { Location: '/x' } });
      }),
    );

    const response = await client().http.send<{ ok: boolean }>('/bff/admin/orders/1/hold', {
      body: { reason: 'check' },
      idempotencyKey: 'key-1',
      ifMatch: 3,
    });

    expect(response.status).toBe(201);
    expect(response.headers.get('Location')).toBe('/x');
    expect(response.data).toEqual({ ok: true });
    expect(body).toEqual({ reason: 'check' });
    expect(seen?.headers.get('x-csrf')).toBe('1');
    expect(seen?.headers.get('content-type')).toBe('application/json');
    expect(seen?.headers.get('idempotency-key')).toBe('key-1');
    expect(seen?.headers.get('if-match')).toBe('"3"');
  });

  it('passes a string If-Match as it is and answers an empty body with undefined', async () => {
    let ifMatch: string | null = null;
    server.use(
      http.put('*/bff/admin/x', ({ request }) => {
        ifMatch = request.headers.get('if-match');
        return new HttpResponse(null, { status: 204 });
      }),
    );

    const response = await client().http.send('/bff/admin/x', {
      method: 'put',
      ifMatch: 'W/"4"',
    });

    expect(response.data).toBeUndefined();
    expect(ifMatch).toBe('W/"4"');
  });

  it('formats an ETag and the sign-in address', () => {
    expect(entityTag(7)).toBe('"7"');
    expect(loginUrl('/admin.html?a=b c')).toBe('/bff/login?returnUrl=%2Fadmin.html%3Fa%3Db%20c');
  });
});

describe('error mapping', () => {
  it.each([
    [400, ValidationError],
    [422, ValidationError],
    [403, ForbiddenError],
    [404, NotFoundError],
    [409, ConflictError],
    [412, PreconditionFailedError],
    [428, PreconditionFailedError],
    [502, ServiceUnavailableError],
    [504, ServiceUnavailableError],
    [500, ServerError],
    [418, ApiError],
  ])('maps %i to the right error type', async (status, type) => {
    server.use(http.get('*/bff/x', () => problem(status, { detail: 'Detail text' })));

    const error = await client()
      .http.get('/bff/x')
      .catch((caught: unknown) => caught);

    expect(error).toBeInstanceOf(type);
    expect(error).toMatchObject({ status, message: 'Detail text' });
    expect((error as ApiError).problem).toMatchObject({ title: 'Problem', status });
  });

  it('keeps the field errors of a validation failure as messages', async () => {
    server.use(
      http.post('*/bff/x', () =>
        problem(400, { errors: { Items: ['Items is required.'], Currency: ['Bad currency.'] } }),
      ),
    );

    const error = await client()
      .http.send('/bff/x', { method: 'POST' })
      .catch((caught: unknown) => caught);

    expect((error as ApiError).messages).toEqual(['Items is required.', 'Bad currency.']);
  });

  it('falls back to the title, then to the status, when there is no detail', async () => {
    server.use(
      http.get('*/bff/titled', () => problem(404)),
      http.get('*/bff/bare', () => new HttpResponse(null, { status: 404 })),
    );

    const titled = await client()
      .http.get('/bff/titled')
      .catch((caught: unknown) => caught);
    const bare = await client()
      .http.get('/bff/bare')
      .catch((caught: unknown) => caught);

    expect((titled as ApiError).messages).toEqual(['Problem']);
    expect((bare as ApiError).messages).toEqual(['A requisição falhou com o status 404.']);
  });

  it('keeps the text of an answer that is not JSON', async () => {
    server.use(http.get('*/bff/x', () => new HttpResponse('Bad gateway page', { status: 502 })));

    const error = await client()
      .http.get('/bff/x')
      .catch((caught: unknown) => caught);

    expect(error).toBeInstanceOf(ServiceUnavailableError);
    expect((error as ApiError).message).toBe('Bad gateway page');
  });

  it('treats a JSON answer that is not an object as an empty problem', async () => {
    server.use(http.get('*/bff/x', () => HttpResponse.json([1, 2], { status: 500 })));

    const error = await client()
      .http.get('/bff/x')
      .catch((caught: unknown) => caught);

    expect(error).toBeInstanceOf(ServerError);
    expect((error as ApiError).problem).toEqual({});
  });
});

describe('401', () => {
  it('sends the browser to the sign-in page with the current page and fails with UnauthorizedError', async () => {
    server.use(http.get('*/bff/user', () => problem(401)));
    const { http: api, redirectToLogin } = client();

    await expect(api.get('/bff/user')).rejects.toBeInstanceOf(UnauthorizedError);

    expect(redirectToLogin).toHaveBeenCalledExactlyOnceWith('/shop.html?x=1');
  });

  it('is not retried', async () => {
    let calls = 0;
    server.use(
      http.get('*/bff/user', () => {
        calls++;
        return problem(401);
      }),
    );

    await client()
      .http.get('/bff/user')
      .catch(() => undefined);

    expect(calls).toBe(1);
  });
});

describe('retry', () => {
  it('tries a 503 again with the same Idempotency-Key and succeeds on the last attempt', async () => {
    const keys: (string | null)[] = [];
    server.use(
      http.post('*/bff/shop/orders', ({ request }) => {
        keys.push(request.headers.get('idempotency-key'));
        return keys.length <= maxRetries
          ? problem(503)
          : HttpResponse.json({ number: 'ORD-1' }, { status: 201 });
      }),
    );
    const { http: api, sleep } = client();

    const response = await api.send<{ number: string }>('/bff/shop/orders', {
      body: { items: [] },
      idempotencyKey: 'cart-key',
    });

    expect(response.data.number).toBe('ORD-1');
    expect(keys).toEqual(['cart-key', 'cart-key', 'cart-key']);
    expect(sleep.mock.calls).toEqual([[200], [400]]);
  });

  it('gives up after two retries on 503 and reports the unavailable service', async () => {
    let calls = 0;
    server.use(
      http.get('*/bff/x', () => {
        calls++;
        return problem(503);
      }),
    );

    await expect(client().http.get('/bff/x')).rejects.toBeInstanceOf(ServiceUnavailableError);

    expect(calls).toBe(1 + maxRetries);
  });

  it('tries a network error again with the same key, then reports a NetworkError', async () => {
    const keys: (string | null)[] = [];
    server.use(
      http.post('*/bff/shop/orders', ({ request }) => {
        keys.push(request.headers.get('idempotency-key'));
        return HttpResponse.error();
      }),
    );

    const error = await client()
      .http.send('/bff/shop/orders', { body: {}, idempotencyKey: 'k' })
      .catch((caught: unknown) => caught);

    expect(error).toBeInstanceOf(NetworkError);
    expect(keys).toEqual(['k', 'k', 'k']);
  });

  it.each([400, 409, 412, 422, 500, 502])('does not retry %i', async (status) => {
    let calls = 0;
    server.use(
      http.get('*/bff/x', () => {
        calls++;
        return problem(status);
      }),
    );

    await client()
      .http.get('/bff/x')
      .catch(() => undefined);

    expect(calls).toBe(1);
  });

  it('does not repeat a change that has neither an Idempotency-Key nor an If-Match', async () => {
    let calls = 0;
    server.use(
      http.post('*/bff/admin/dead-letters/1/replay', () => {
        calls++;
        return HttpResponse.error();
      }),
    );

    await expect(
      client().http.send('/bff/admin/dead-letters/1/replay', { method: 'POST' }),
    ).rejects.toBeInstanceOf(NetworkError);

    expect(calls).toBe(1);
  });

  it('does not retry when the caller aborts', async () => {
    let calls = 0;
    const controller = new AbortController();
    server.use(
      http.get('*/bff/x', () => {
        calls++;
        controller.abort();
        return HttpResponse.error();
      }),
    );

    await expect(client().http.get('/bff/x', undefined, controller.signal)).rejects.toThrow();

    expect(calls).toBe(1);
  });

  it('turns a timeout into a retried NetworkError', async () => {
    let calls = 0;
    server.use(
      http.get('*/bff/x', async () => {
        calls++;
        await new Promise((resolve) => setTimeout(resolve, 200));
        return HttpResponse.json({});
      }),
    );

    await expect(client({ timeoutMilliseconds: 20 }).http.get('/bff/x')).rejects.toBeInstanceOf(
      NetworkError,
    );

    expect(calls).toBe(1 + maxRetries);
  });
});
