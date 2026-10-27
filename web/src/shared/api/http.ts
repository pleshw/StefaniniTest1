import { NetworkError, toApiError, type ApiError, type ProblemDetails } from './errors';

export type QueryValue = string | number | boolean | undefined;

export interface RequestOptions {
  readonly method?: string;
  readonly query?: Readonly<Record<string, QueryValue>>;
  readonly body?: unknown;
  readonly headers?: Readonly<Record<string, string>>;
  /** Sent as Idempotency-Key, and the same on every retry. */
  readonly idempotencyKey?: string;
  /** The version being changed: a number becomes the quoted ETag the services expect. */
  readonly ifMatch?: number | string;
  readonly signal?: AbortSignal;
}

export interface HttpResponse<T> {
  readonly status: number;
  readonly headers: Headers;
  readonly data: T;
}

export interface HttpClient {
  send<T>(path: string, options?: RequestOptions): Promise<HttpResponse<T>>;
  get<T>(
    path: string,
    query?: Readonly<Record<string, QueryValue>>,
    signal?: AbortSignal,
  ): Promise<T>;
}

export interface HttpClientOptions {
  readonly fetch?: typeof fetch;
  /** Where relative paths resolve; the current page by default. */
  readonly baseUrl?: string;
  /** Called on a 401 with the page to come back to. */
  readonly redirectToLogin?: (returnUrl: string) => void;
  /** The page to come back to after signing in. */
  readonly currentPage?: () => string;
  readonly sleep?: (milliseconds: number) => Promise<void>;
  readonly timeoutMilliseconds?: number;
}

/** A network error or a 503 is tried again at most twice, so three attempts in all. */
export const maxRetries = 2;
export const csrfHeaderValue = '1';

const safeMethods = new Set(['GET', 'HEAD', 'OPTIONS']);
const repeatableMethods = new Set([...safeMethods, 'PUT', 'DELETE']);

/** The ETag of a version, as Orders and Catalog read it in If-Match. */
export function entityTag(version: number): string {
  return `"${String(version)}"`;
}

export function loginUrl(returnUrl: string): string {
  return `/bff/login?returnUrl=${encodeURIComponent(returnUrl)}`;
}

/**
 * The page's only way to the BFF: JSON in and out, X-CSRF on every unsafe request, ProblemDetails turned into typed
 * errors, a 401 sent to the sign-in page and a bounded retry for network errors and 503.
 */
export function createHttpClient(options: HttpClientOptions = {}): HttpClient {
  const doFetch = options.fetch ?? ((input, init) => fetch(input, init));
  const redirectToLogin =
    options.redirectToLogin ??
    ((returnUrl) => {
      window.location.assign(loginUrl(returnUrl));
    });
  const currentPage =
    options.currentPage ?? (() => window.location.pathname + window.location.search);
  const sleep =
    options.sleep ??
    ((milliseconds) => new Promise((resolve) => setTimeout(resolve, milliseconds)));
  const timeout = options.timeoutMilliseconds ?? 30_000;

  async function send<T>(path: string, request: RequestOptions = {}): Promise<HttpResponse<T>> {
    const method = (request.method ?? (request.body === undefined ? 'GET' : 'POST')).toUpperCase();
    const init = buildInit(method, request);
    const url = buildUrl(path, request.query, options.baseUrl ?? window.location.href);
    // Without a key a change could be applied twice, so only requests that are safe to repeat are retried.
    const retries =
      repeatableMethods.has(method) ||
      request.idempotencyKey !== undefined ||
      request.ifMatch !== undefined
        ? maxRetries
        : 0;

    for (let attempt = 0; ; attempt++) {
      const outcome = await attemptOnce<T>(doFetch, url, init, request.signal, timeout);

      if (outcome.kind === 'response') {
        return outcome.response;
      }
      if (outcome.kind === 'error' && outcome.error.status === 401) {
        redirectToLogin(currentPage());
        throw outcome.error;
      }
      if (attempt >= retries || !outcome.retryable) {
        throw outcome.error;
      }

      await sleep(200 * 2 ** attempt);
    }
  }

  return {
    send,
    get: async <T>(
      path: string,
      query?: Readonly<Record<string, QueryValue>>,
      signal?: AbortSignal,
    ) => {
      const response = await send<T>(path, {
        ...(query === undefined ? {} : { query }),
        ...(signal === undefined ? {} : { signal }),
      });

      return response.data;
    },
  };
}

function buildInit(method: string, request: RequestOptions): RequestInit {
  const headers = new Headers({ Accept: 'application/json, application/problem+json' });

  for (const [name, value] of Object.entries(request.headers ?? {})) {
    headers.set(name, value);
  }
  if (!safeMethods.has(method)) {
    headers.set('X-CSRF', csrfHeaderValue);
  }
  if (request.idempotencyKey !== undefined) {
    headers.set('Idempotency-Key', request.idempotencyKey);
  }
  if (request.ifMatch !== undefined) {
    headers.set(
      'If-Match',
      typeof request.ifMatch === 'number' ? entityTag(request.ifMatch) : request.ifMatch,
    );
  }

  let body: string | undefined;
  if (request.body !== undefined) {
    headers.set('Content-Type', 'application/json');
    body = JSON.stringify(request.body);
  }

  return { method, headers, credentials: 'same-origin', ...(body === undefined ? {} : { body }) };
}

function buildUrl(
  path: string,
  query: Readonly<Record<string, QueryValue>> | undefined,
  base: string,
): string {
  const url = new URL(path, base);

  for (const [name, value] of Object.entries(query ?? {})) {
    if (value !== undefined && value !== '') {
      url.searchParams.set(name, String(value));
    }
  }

  return url.toString();
}

type Outcome<T> =
  | { readonly kind: 'response'; readonly response: HttpResponse<T> }
  | {
      readonly kind: 'error';
      readonly error: ApiError;
      readonly retryable: boolean;
    }
  | { readonly kind: 'network'; readonly error: NetworkError; readonly retryable: true };

async function attemptOnce<T>(
  doFetch: typeof fetch,
  url: string,
  init: RequestInit,
  signal: AbortSignal | undefined,
  timeout: number,
): Promise<Outcome<T>> {
  const limit = AbortSignal.timeout(timeout);
  let response: Response;

  try {
    response = await doFetch(url, {
      ...init,
      signal: signal === undefined ? limit : AbortSignal.any([signal, limit]),
    });
  } catch (error) {
    // The caller giving up is not a network failure and is never retried.
    if (signal?.aborted === true) {
      throw error;
    }

    return { kind: 'network', error: new NetworkError({ cause: error }), retryable: true };
  }

  const data = await readBody(response);

  if (response.ok) {
    return {
      kind: 'response',
      response: { status: response.status, headers: response.headers, data: data as T },
    };
  }

  return {
    kind: 'error',
    error: toApiError(response.status, asProblem(data)),
    retryable: response.status === 503,
  };
}

async function readBody(response: Response): Promise<unknown> {
  const text = await response.text();

  if (text === '') {
    return undefined;
  }

  try {
    return JSON.parse(text) as unknown;
  } catch {
    // An error page that is not JSON (a proxy, say) still carries its text.
    return { detail: text.slice(0, 500) };
  }
}

function asProblem(data: unknown): ProblemDetails {
  return typeof data === 'object' && data !== null && !Array.isArray(data) ? data : {};
}
