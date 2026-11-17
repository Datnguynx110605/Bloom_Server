// Pieces every test script shares: the setup (pool, warm-up, a fresh launch), the webhook check (P5) and the summary printout.
import http from 'k6/http';
import { fail, sleep } from 'k6';
import { FAKES_URL, num } from './config.js';
import { anonIp } from './ip.js';
import { adminToken } from './auth.js';
import { api, must } from './http.js';
import { freshLaunch, listLaunches } from './admin.js';
import { warmUp } from './flows.js';

const WEBHOOK_WAIT_S = num('WEBHOOK_WAIT_S', 30);

const stats = () => http.get(`${FAKES_URL}/_stats`).json();

// The data every flow takes: the pool, a fresh launch, the end of the test, and the fake's counters before the run (a run's share of the webhooks is the difference).
export function prepare({ testSeconds, ...launch }) {
  const fakes = stats();
  const token = adminToken();
  const found = must(api('GET', '/api/catalog/products?q=lt-pool&pageSize=48', null, { ip: anonIp(0), flow: 'setup' }), 'list pool').items ?? [];
  if (!found.length) fail('no lt-pool products: run seed.js first');
  const pool = found.map((c, n) => {
    const view = must(api('GET', `/api/catalog/products/${c.id}`, null, { ip: anonIp(n + 1), flow: 'setup' }), 'get pool product');
    return { productId: c.id, skuId: view.skus[0].id, slug: c.slug };
  });
  warmUp(pool);
  listLaunches(token);
  const l = freshLaunch(token, { ...launch, stock: launch.allocation });
  return { launchId: l.launchId, launchProductId: l.productId, launchSkuId: l.skuId, pool, endsAt: Date.now() + testSeconds * 1000, fakes };
}

// P5: every Stripe session of this run must end in a delivered webhook. Deliveries lag the checkout by the fake's pay delay plus its retries, so wait for them first.
export function checkWebhooks(before) {
  let s;
  for (let waited = 0; ; waited++) {
    s = stats();
    if (s.webhooksDelivered + s.webhooksFailed - before.webhooksDelivered - before.webhooksFailed >= s.sessionsCreated - before.sessionsCreated) break;
    if (waited >= WEBHOOK_WAIT_S) fail(`webhooks still in flight after ${WEBHOOK_WAIT_S}s: before ${JSON.stringify(before)}, now ${JSON.stringify(s)}`);
    sleep(1);
  }
  if (s.webhooksFailed > before.webhooksFailed) fail(`${s.webhooksFailed - before.webhooksFailed} webhook(s) never delivered: before ${JSON.stringify(before)}, now ${JSON.stringify(s)}`);
  console.log(`webhooks: ${s.sessionsCreated - before.sessionsCreated} sessions, ${s.webhooksDelivered - before.webhooksDelivered} delivered, 0 failed`);
}

// One line per metric (sub-metrics included), with its thresholds.
export function summaryLines(data) {
  return Object.entries(data.metrics).map(([name, m]) => {
    const values = Object.entries(m.values).map(([k, v]) => `${k}=${Number.isInteger(v) ? v : v.toFixed(3)}`).join(' ');
    const th = Object.entries(m.thresholds ?? {}).map(([expr, r]) => ` ${(r.ok ?? r) ? 'PASS' : 'FAIL'}[${expr}]`).join('');
    return `${name}: ${values}${th}`;
  });
}

// k6 prints nothing of its own once handleSummary is defined: `text` to stdout, the raw summary to /results.
export function summaryOutputs(prefix, text, data) {
  return { stdout: `${text}\n`, [`/results/${prefix}-${new Date().toISOString().replace(/[:.]/g, '-')}.json`]: JSON.stringify(data, null, 2) };
}
