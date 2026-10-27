// k6 load test (C55). Run it with the Compose profile "load" (see README): docker compose --profile load run --rm k6
//
// Peak profile of docs/pt-br/04-tech-dimensioning.md, for 10 minutes: 50 orders/min, 20 price queries/s and
// listings by up to 100 virtual users in total. The thresholds are the p95 latency targets of that document, so the
// run fails (exit code 99) when a target is missed.
//
//   Create an order   < 300 ms  through nginx and the BFF (the sizing table states the target for Orders API alone)
//   Price query batch <  50 ms  straight at Catalog API, with a service token signed here
//   List orders       < 200 ms  through nginx and the BFF
//
// Settings (environment variables, all optional; the compose service fills them from .env):
//   BASE_URL, CATALOG_URL, BFF_SHOPPER_PASSWORD, BFF_ADMIN_PASSWORD, SERVICE_TOKEN_SIGNING_KEY,
//   DURATION (default 10m; use 30s for a quick check of the script, the thresholds still apply).
import crypto from 'k6/crypto';
import encoding from 'k6/encoding';
import exec from 'k6/execution';
import http from 'k6/http';
import { check, fail, sleep } from 'k6';

const baseUrl = (__ENV.BASE_URL || 'http://nginx:8080').replace(/\/$/, '');
const catalogUrl = (__ENV.CATALOG_URL || 'http://catalog-api:8080').replace(/\/$/, '');
const duration = __ENV.DURATION || '10m';
const signingKey = __ENV.SERVICE_TOKEN_SIGNING_KEY || '';

// 30 + 40 + 30 virtual users at most: the 100 users of the peak profile.
export const options = {
  scenarios: {
    create_orders: {
      executor: 'constant-arrival-rate',
      exec: 'createOrder',
      rate: 50,
      timeUnit: '1m',
      duration,
      preAllocatedVUs: 10,
      maxVUs: 30,
    },
    price_queries: {
      executor: 'constant-arrival-rate',
      exec: 'queryPrices',
      rate: 20,
      timeUnit: '1s',
      duration,
      preAllocatedVUs: 10,
      maxVUs: 40,
    },
    list_orders: {
      executor: 'constant-vus',
      exec: 'listOrders',
      vus: 30,
      duration,
    },
  },
  thresholds: {
    'http_req_duration{scenario:create_orders}': ['p(95)<300'],
    'http_req_duration{scenario:price_queries}': ['p(95)<50'],
    'http_req_duration{scenario:list_orders}': ['p(95)<200'],
    'http_req_failed{scenario:create_orders}': ['rate<0.01'],
    'http_req_failed{scenario:price_queries}': ['rate<0.01'],
    'http_req_failed{scenario:list_orders}': ['rate<0.01'],
    // An arrival-rate scenario that cannot keep up drops iterations: that is a missed target too.
    dropped_iterations: ['count==0'],
    checks: ['rate>0.99'],
  },
};

// The BFF's session cookie is Secure and the stack is reached over plain http, so cookies are replayed by hand
// (as scripts/smoke.sh does) instead of relying on k6's jar.
function cookieHeader(cookies) {
  return Object.entries(cookies)
    .map(([name, values]) => `${name}=${values[0].value}`)
    .join('; ');
}

function mergeCookies(into, response) {
  for (const [name, values] of Object.entries(response.cookies)) {
    into[name] = values;
  }
}

function signIn(user, password) {
  if (!password) {
    fail(`no password for ${user}: set BFF_${user.toUpperCase()}_PASSWORD`);
  }

  const cookies = {};
  const form = http.get(`${baseUrl}/bff/login`, { tags: { name: 'sign-in' } });
  mergeCookies(cookies, form);

  const field = /<input name="(__[^"]*)" type="hidden" value="([^"]*)"/.exec(form.body);
  if (form.status !== 200 || !field) {
    fail(`GET /bff/login answered ${form.status} with no antiforgery token`);
  }

  const answer = http.post(
    `${baseUrl}/bff/login`,
    { [field[1]]: field[2], returnUrl: '/', username: user, password },
    { redirects: 0, headers: { Cookie: cookieHeader(cookies) }, tags: { name: 'sign-in' } },
  );
  mergeCookies(cookies, answer);

  if (answer.status !== 302) {
    fail(`signing in as ${user} answered ${answer.status} instead of 302`);
  }

  return cookieHeader(cookies);
}

// One session per virtual user, signed in on its first iteration and kept for the whole run.
let session = null;

function sessionFor(user, password) {
  session ??= signIn(user, password);

  return session;
}

function uuid() {
  const bytes = crypto.randomBytes(16);
  const view = new Uint8Array(bytes);
  view[6] = (view[6] & 0x0f) | 0x40;
  view[8] = (view[8] & 0x3f) | 0x80;
  const hex = Array.from(view, (byte) => byte.toString(16).padStart(2, '0')).join('');

  return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`;
}

// The same token Orders.Bff signs (ServiceTokenIssuer): HS256, issuer orders-bff, audience catalog-api, 5 minutes.
function serviceToken(subject, name) {
  const now = Math.floor(Date.now() / 1000);
  const part = (value) => encoding.b64encode(JSON.stringify(value), 'rawurl');
  const unsigned = `${part({ alg: 'HS256', typ: 'JWT' })}.${part({
    iss: 'orders-bff',
    aud: 'catalog-api',
    sub: subject,
    name,
    role: [],
    iat: now,
    nbf: now,
    exp: now + 300,
  })}`;

  return `${unsigned}.${crypto.hmac('sha256', signingKey, unsigned, 'base64rawurl')}`;
}

// A few products from the shop's catalog, read once with their prices, used by every scenario.
export function setup() {
  const page = http.get(`${baseUrl}/bff/shop/products?limit=20&currency=BRL`, { tags: { name: 'setup' } });
  const skus = page.status === 200 ? page.json('items').map((item) => item.sku) : [];

  if (skus.length < 3) {
    fail(`the catalog returned ${skus.length} products (status ${page.status}); is the seed on (Catalog__Seed__Enabled)?`);
  }

  return { skus };
}

export function createOrder(data) {
  const cookie = sessionFor('shopper', __ENV.BFF_SHOPPER_PASSWORD);
  const sku = data.skus[exec.scenario.iterationInTest % data.skus.length];
  const answer = http.post(
    `${baseUrl}/bff/shop/orders`,
    JSON.stringify({ currency: 'BRL', items: [{ sku, quantity: 1 }] }),
    {
      headers: {
        'Content-Type': 'application/json',
        'X-CSRF': '1',
        'Idempotency-Key': uuid(),
        Cookie: cookie,
      },
      tags: { name: 'POST /bff/shop/orders' },
    },
  );

  check(answer, { 'order created (201)': (response) => response.status === 201 });
}

export function queryPrices(data) {
  if (!signingKey) {
    fail('SERVICE_TOKEN_SIGNING_KEY is not set');
  }

  const skus = [0, 1, 2].map((offset) => data.skus[(exec.scenario.iterationInTest + offset) % data.skus.length]);
  const answer = http.post(
    `${catalogUrl}/api/v1/products/prices:query`,
    JSON.stringify({ currency: 'BRL', skus }),
    {
      headers: {
        'Content-Type': 'application/json',
        Authorization: `Bearer ${serviceToken('load-test', 'k6')}`,
      },
      tags: { name: 'POST /api/v1/products/prices:query' },
    },
  );

  check(answer, {
    'prices answered (200)': (response) => response.status === 200,
    'every price found': (response) => response.status === 200 && response.json('missing').length === 0,
  });
}

// Odd virtual users list as the admin (every order, newest first), even ones as the shopper (their own).
export function listOrders() {
  const admin = exec.vu.idInTest % 2 === 1;
  const cookie = admin
    ? sessionFor('admin', __ENV.BFF_ADMIN_PASSWORD)
    : sessionFor('shopper', __ENV.BFF_SHOPPER_PASSWORD);
  const path = admin ? '/bff/admin/orders?limit=20' : '/bff/shop/orders?limit=20';
  const answer = http.get(`${baseUrl}${path}`, {
    headers: { Cookie: cookie },
    tags: { name: `GET ${admin ? '/bff/admin/orders' : '/bff/shop/orders'}` },
  });

  check(answer, { 'orders listed (200)': (response) => response.status === 200 });
  sleep(1);
}
