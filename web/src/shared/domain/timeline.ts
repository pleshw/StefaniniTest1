export interface TimelineEntry {
  readonly status: string;
  /** What happened, when the status name alone does not say it ("Placed"). */
  readonly label?: string;
  /** ISO 8601; an entry without a time is shown without one. */
  readonly at?: string;
  readonly note?: string;
}
