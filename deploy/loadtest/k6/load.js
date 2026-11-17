// Load test: anonymous catalog browsing, signed-in mixed shoppers, a launch with its waiting room, and WebSocket sessions, all at once.
// Every rate, count and duration is an env var with a default (a placeholder until the machine is measured).
import http from 'k6/http';
import { fail, sleep } from 'k6';
import { FAKES_URL, num, str } from './lib/config.js';
import { anonIp } from './lib/ip.js';
import { adminToken } from './lib/auth.js';
import { api, must } from './lib/http.js';
import { freshLaunch, listLaunches } from './lib/admin.js';
import { catalogFlow, mixedFlow, launchFlow, wsFlow, warmUp } from './lib/flows.js';

const CATALOG_RATE = num('CATALOG_RATE', 50);
const MIXED_RATE = num('MIXED_RATE', 10);
const WS_CONNECTIONS = num('WS_CONNECTIONS', 200);
const LAUNCH_USERS = num('LAUNCH_USERS', 300);
const LAUNCH_DELAY = num('LAUNCH_DELAY', 120);
const ALLOCATION = num('ALLOCATION', 150);
const PURCHASE_LIMIT = num('PURCHASE_LIMIT', 1);
const ADMISSION_RATE = num('ADMISSION_RATE', 20);
const RAMP = str('RAMP', '1m');
const HOLD = str('HOLD', '8m');
const WEBHOOK_WAIT_S = num('WEBHOOK_WAIT_S', 30);

const UNIT_S = { s: 1, m: 60, h: 3600 };
function seconds(d) {
  const m = /^(\d+)([smh])$/.exec(d);
  if (!m) throw new Error(`duration ${d} must look like 30s, 1m or 1h`);
  return Number(m[1]) * UNIT_S[m[2]];
}
const TEST_SECONDS = 2 * seconds(RAMP) + seconds(HOLD);

// Open-model scenario: ramp up, hold, ramp down. A mixed iteration lasts several seconds (think time), so it needs more VUs per iteration/s.
function arrival(exec, rate, preVus, maxVus) {
  return {
    executor: 'ramping-arrival-rate', exec, startRate: 0, timeUnit: '1s', preAllocatedVUs: preVus, maxVUs: maxVus,
    stages: [{ target: rate, duration: RAMP }, { target: rate, duration: HOLD }, { target: 0, duration: RAMP }],
  };
}
const catalogVus = num('CATALOG_VUS', Math.ceil(CATALOG_RATE));
const mixedVus = num('MIXED_VUS', Math.ceil(MIXED_RATE * 10));

export const options = {
  setupTimeout: str('SETUP_TIMEOUT', '5m'),
  scenarios: {
    catalog: arrival('catalog', CATALOG_RATE, catalogVus, num('CATALOG_MAX_VUS', catalogVus * 4)),
    mixed: arrival('mixed', MIXED_RATE, mixedVus, num('MIXED_MAX_VUS', mixedVus * 2)),
    ws: { executor: 'constant-vus', exec: 'ws', vus: WS_CONNECTIONS, duration: `${TEST_SECONDS}s` },
    launch: { executor: 'per-vu-iterations', exec: 'launch', vus: LAUNCH_USERS, iterations: 1, maxDuration: `${TEST_SECONDS}s` },
  },
  thresholds: {
    'http_req_duration{flow:catalog}': ['p(95)<150'],
    'http_req_duration{flow:checkout_launch}': ['p(95)<300'],
    'http_req_duration{flow:wr_status}': ['p(95)<50'],
    http_req_failed: ['rate<0.01'],
    ws_connect_ok: ['rate>0.99'],
    ws_sessions: ['count>0'], // ws_connect_ok with no samples would pass; this fails a WebSocket outage that records nothing
    checks: ['rate>0.99'],
    checkout_idempotent: ['rate==1'],
    dropped_iterations: ['count==0'],
    // Shown in the summary, and a 401 or 429 means a refresh bug or a per-IP/per-user limit being hit.
    'http_reqs{status:401}': ['count==0'],
    'http_reqs{status:429}': ['count==0'],
    'http_reqs{flow:checkout_launch}': ['count>0'],
  },
};

const stats = () => http.get(`${FAKES_URL}/_stats`).json();

export function setup() {
  const fakes = stats(); // baseline: the fake counts since it started, this run's share is the difference
  const token = adminToken();
  const found = must(api('GET', '/api/catalog/products?q=lt-pool&pageSize=48', null, { ip: anonIp(0), flow: 'setup' }), 'list pool').items ?? [];
  if (!found.length) fail('no lt-pool products: run seed.js first');
  const pool = found.map((c, n) => {
    const view = must(api('GET', `/api/catalog/products/${c.id}`, null, { ip: anonIp(n + 1), flow: 'setup' }), 'get pool product');
    return { productId: c.id, skuId: view.skus[0].id, slug: c.slug };
  });
  warmUp(pool);
  listLaunches(token);
  const l = freshLaunch(token, { delaySeconds: LAUNCH_DELAY, admissionRate: ADMISSION_RATE, purchaseLimit: PURCHASE_LIMIT, allocation: ALLOCATION, stock: ALLOCATION });
  return { launchId: l.launchId, launchProductId: l.productId, launchSkuId: l.skuId, pool, endsAt: Date.now() + TEST_SECONDS * 1000, fakes };
}

export const catalog = catalogFlow;
export const mixed = mixedFlow;
export const launch = launchFlow;
export const ws = wsFlow;

// P5: every Stripe session of this run must end in a delivered webhook. Deliveries lag the checkout by the fake's pay delay plus its retries, so wait for them first.
export function teardown(data) {
  let s;
  for (let waited = 0; ; waited++) {
    s = stats();
    if (s.webhooksDelivered + s.webhooksFailed - data.fakes.webhooksDelivered - data.fakes.webhooksFailed >= s.sessionsCreated - data.fakes.sessionsCreated) break;
    if (waited >= WEBHOOK_WAIT_S) fail(`webhooks still in flight after ${WEBHOOK_WAIT_S}s: before ${JSON.stringify(data.fakes)}, now ${JSON.stringify(s)}`);
    sleep(1);
  }
  if (s.webhooksFailed > data.fakes.webhooksFailed) fail(`${s.webhooksFailed - data.fakes.webhooksFailed} webhook(s) never delivered: before ${JSON.stringify(data.fakes)}, now ${JSON.stringify(s)}`);
  console.log(`webhooks: ${s.sessionsCreated - data.fakes.sessionsCreated} sessions, ${s.webhooksDelivered - data.fakes.webhooksDelivered} delivered, 0 failed`);
}

// k6 prints nothing of its own once handleSummary is defined: one line per metric (sub-metrics included), then the JSON file.
export function handleSummary(data) {
  const lines = Object.entries(data.metrics).map(([name, m]) => {
    const values = Object.entries(m.values).map(([k, v]) => `${k}=${Number.isInteger(v) ? v : v.toFixed(3)}`).join(' ');
    const th = Object.entries(m.thresholds ?? {}).map(([expr, r]) => ` ${(r.ok ?? r) ? 'PASS' : 'FAIL'}[${expr}]`).join('');
    return `${name}: ${values}${th}`;
  });
  return {
    stdout: `${lines.join('\n')}\n`,
    [`/results/load-${new Date().toISOString().replace(/[:.]/g, '-')}.json`]: JSON.stringify(data, null, 2),
  };
}
