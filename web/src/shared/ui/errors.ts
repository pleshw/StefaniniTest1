import { ApiError, NetworkError } from '../api/errors';

/** What to tell the person about a failed request: the service's own words (every field message) when it gave some. */
export function errorMessage(error: unknown, fallback: string): string {
  if (error instanceof ApiError) {
    return error.messages.join(' ');
  }

  return error instanceof NetworkError ? error.message : fallback;
}
