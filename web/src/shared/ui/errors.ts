import { ApiError, NetworkError } from '../api/errors';

/** What to tell the person about a failed request: the service's own words when it gave some. */
export function errorMessage(error: unknown, fallback: string): string {
  return error instanceof ApiError || error instanceof NetworkError ? error.message : fallback;
}
