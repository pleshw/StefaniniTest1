import axe from 'axe-core';
import { expect } from 'vitest';

/** Fails with the ids of the axe rules a rendered view breaks. */
export async function expectAccessible(root: Element = document.body): Promise<void> {
  const results = await axe.run(root);

  expect(
    results.violations.map((violation) => `${violation.id}: ${violation.nodes[0]?.html ?? ''}`),
  ).toEqual([]);
}
