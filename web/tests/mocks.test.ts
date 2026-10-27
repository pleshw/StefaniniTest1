import { describe, expect, it } from 'vitest';

describe('BFF stubs', () => {
  it('answers /bff/user like the BFF does', async () => {
    const response = await fetch(new URL('/bff/user', window.location.href));

    expect(await response.json()).toMatchObject({ name: 'admin', roles: ['Admin'] });
  });
});
