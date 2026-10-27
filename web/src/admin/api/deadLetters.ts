import type { HttpClient } from '../../shared/api/http';

/** A message parked in the dead-letter queue, with the failure that parked it. */
export interface DeadLetter {
  readonly messageId: string;
  readonly messageType: string | null;
  readonly originalDestination: string | null;
  readonly exceptionType: string | null;
  readonly exceptionMessage: string | null;
  readonly attempts: number | null;
  /** The message body as JSON text. */
  readonly payload: string;
  readonly headers: Readonly<Record<string, string>>;
}

export interface DeadLetterReplay {
  readonly letter: DeadLetter;
  readonly newMessageId: string;
  readonly exchange: string;
  readonly routingKey: string;
  readonly replayedBy: string;
}

/** The parked messages, oldest first (up to 100). They stay in the queue. */
export function listDeadLetters(
  http: HttpClient,
  signal?: AbortSignal,
): Promise<readonly DeadLetter[]> {
  return http.get<readonly DeadLetter[]>('/bff/admin/dead-letters', undefined, signal);
}

/** Sends the message back through the exchange it came through, under a new message id, and removes it from the queue. */
export async function replayDeadLetter(
  http: HttpClient,
  messageId: string,
): Promise<DeadLetterReplay> {
  const response = await http.send<DeadLetterReplay>(
    `/bff/admin/dead-letters/${encodeURIComponent(messageId)}/replay`,
    { method: 'POST' },
  );

  return response.data;
}
