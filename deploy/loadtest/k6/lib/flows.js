// The four user flows. Each takes `data` from load.js setup(): { launchId, launchProductId, launchSkuId, pool: [{productId, skuId, slug}], endsAt (epoch ms) }.
import { check, fail, sleep } from 'k6';
import exec from 'k6/execution';
import { Counter, Rate, Trend } from 'k6/metrics';
import { WebSocket } from 'k6/experimental/websockets';
import { BASE_URL, ORIGIN, num } from './config.js';
import { anonIp } from './ip.js';
import { api, must, uuid } from './http.js';
import { customer, fresh } from './auth.js';

const CUSTOMERS = num('CUSTOMERS', 5000);
const CHECKOUT_RATIO = num('CHECKOUT_RATIO', 0.05);
const THINK_SECONDS = num('MIXED_THINK_SECONDS', 1.5);
const WS_HOLD_SECONDS = num('WS_HOLD_SECONDS', 60);
const WS_HANDSHAKE_TIMEOUT_S = num('WS_HANDSHAKE_TIMEOUT_S', 10);

export const checkoutIdempotent = new Rate('checkout_idempotent');
export const wsConnectOk = new Rate('ws_connect_ok');
export const wsConnectMs = new Trend('ws_connect_ms', true);
export const wsSessions = new Counter('ws_sessions');
export const sessionRefreshes = new Counter('session_refreshes');

const SHIP_TO = { name: 'Load Test', lines: ['1 Test Street'], city: 'Reno', state: 'NV', zip: '89501', phone: '+17755550100' };
const RS = '\u001e'; // SignalR record separator

const ok = (res, name = 'status ok') => check(res, { [name]: (r) => r.status >= 200 && r.status < 300 });
const pick = (list) => list[Math.floor(Math.random() * list.length)];
const secondsLeft = (data) => (data.endsAt - Date.now()) / 1000;

// This VU's customer, signed in on its first iteration (P4) and refreshed in place when old (never a 401).
let cached;
function session() {
  if (!cached) {
    const i = exec.vu.idInTest - 1;
    if (i >= CUSTOMERS) fail(`VU ${exec.vu.idInTest} has no seeded customer: raise CUSTOMERS (${CUSTOMERS})`);
    cached = customer(i);
  }
  const before = cached.token;
  fresh(cached);
  if (cached.token !== before) sessionRefreshes.add(1);
  return cached;
}

// The order id of a checkout response; undefined when the body is empty or not JSON.
function orderId(res) {
  try {
    return res.json('orderId');
  } catch (e) {
    return undefined;
  }
}

function checkout(s, body, flow, key, expect) {
  return api('POST', '/api/checkout', { ...body, shipTo: SHIP_TO, shippingOption: 'standard' }, { token: s.token, ip: s.ip, flow, expect, headers: { 'Idempotency-Key': key } });
}

// Run once in setup(): a cold service answers its first calls in seconds (JIT, first DB connection), which would swamp the p95 of a short run.
// The last seeded customer (no VU reaches it) walks the browse -> cart -> checkout -> ticket path once; its webhook is part of the run's webhook count.
export function warmUp(pool) {
  const s = customer(CUSTOMERS - 1);
  const o = { token: s.token, ip: s.ip, flow: 'warmup', expect: [200, 201, 204, 409, 429, 503, 504] };
  // A cold call may hit a 5 s timeout (504, or 503 when payment is not up yet) or empty the checkout bucket (429): repeat it (same Idempotency-Key for the checkout).
  const hit = (call) => {
    let res;
    for (let n = 0; n < 5; n++) {
      if (![429, 503, 504].includes((res = call()).status)) break;
      if (res.status === 429) sleep(Number(res.headers['Retry-After']) || 10); // the checkout bucket (3 burst, 6/min) drains while a cold payment retries
    }
    return res;
  };
  const cart = must(hit(() => api('GET', '/api/cart', null, o)), 'warm-up get cart');
  // 409: a timed-out first try had already stored the line.
  const put = hit(() => api('PUT', '/api/cart/lines', { kind: 'product', skuId: pool[0].skuId, quantity: 1, version: cart.version ?? 0 }, o));
  if (put.status !== 409) must(put, 'warm-up upsert');
  must(hit(() => api('GET', '/api/orders', null, o)), 'warm-up orders');
  const key = uuid();
  must(hit(() => checkout(s, { cart: {} }, 'warmup', key, o.expect)), 'warm-up checkout');
  must(hit(() => api('GET', '/api/notifications/ws-ticket', null, o)), 'warm-up ws ticket');
}

export function catalogFlow(data) {
  const o = { ip: anonIp(exec.scenario.iterationInTest), flow: 'catalog' };
  const p = pick(data.pool);
  ok(api('GET', '/api/catalog/categories', null, o));
  ok(api('GET', '/api/catalog/products?q=lt-pool', null, o));
  ok(api('GET', `/api/catalog/products/${p.productId}`, null, o));
  ok(api('GET', `/api/catalog/products/by-slug/${p.slug}`, null, o));
  ok(api('GET', `/api/catalog/products/${p.productId}/reviews`, null, o));
}

export function mixedFlow(data) {
  const s = session();
  const o = (flow) => ({ token: s.token, ip: s.ip, flow });
  const p = data.pool[s.i % data.pool.length]; // one fixed line per customer: the cart never grows
  ok(api('GET', '/api/catalog/products?q=lt-pool', null, o('catalog')));
  sleep(THINK_SECONDS);
  ok(api('GET', `/api/catalog/products/${p.productId}`, null, o('catalog')));
  sleep(THINK_SECONDS);
  const cart = must(api('GET', '/api/cart', null, o('cart')), 'get cart');
  // 409 = the cart changed under us (paid order removing lines): fine, the next iteration reads the new version.
  const put = api('PUT', '/api/cart/lines', { kind: 'product', skuId: p.skuId, quantity: 1 + Math.floor(Math.random() * 3), version: cart.version ?? 0 },
    { ...o('cart'), expect: [200, 409] });
  check(put, { 'cart upsert ok': (r) => r.status === 200 || r.status === 409 });
  sleep(THINK_SECONDS);
  ok(api('GET', '/api/orders', null, o('orders')));
  if (put.status === 200 && Math.random() < CHECKOUT_RATIO) ok(checkout(s, { cart: {} }, 'checkout_regular', uuid()), 'regular checkout ok');
}

export function launchFlow(data) {
  const s = session();
  const o = (flow) => ({ token: s.token, ip: s.ip, flow });
  const path = `/api/launches/${data.launchId}/waiting-room`;
  let wr = must(api('POST', path, null, o('wr_join')), 'join waiting room');
  while (!['admitted', 'sold_out', 'closed'].includes(wr.state)) {
    if (secondsLeft(data) < (wr.pollAfterSeconds || 5) + 1) return; // the test ends before the room does
    sleep(wr.pollAfterSeconds || 5);
    fresh(s);
    wr = must(api('GET', path, null, o('wr_status')), 'waiting room status');
  }
  if (wr.state !== 'admitted') return;
  const body = { launch: { launchId: data.launchId, passToken: wr.passToken, lines: [{ kind: 'product', skuId: data.launchSkuId, quantity: 1 }] } };
  const key = uuid();
  const first = checkout(s, body, 'checkout_launch', key);
  if (!ok(first, 'launch checkout ok')) return console.error(`launch checkout: ${first.status} ${String(first.body).slice(0, 300)}`);
  // The same key must resume the same order, never open a second one (00-reliability §9). Tagged apart: a cached replay (3-9 ms) must not dilute the checkout_launch p95.
  const again = checkout(s, body, 'checkout_replay', key);
  const id = orderId(first);
  checkoutIdempotent.add(again.status === first.status && id !== undefined && orderId(again) === id);
}

let wsErrorLogged = false; // one line per VU: a wholesale outage must not flood stdout

// One hub connection: handshake (within WS_HANDSHAKE_TIMEOUT_S, else a failed sample), answer pings, close after `holdMs`. Resolves when it is over.
function hub(ticket, ip, holdMs) {
  return new Promise((resolve) => {
    const started = Date.now();
    let up = false;
    let done = false;
    const ws = new WebSocket(`${BASE_URL.replace(/^http/, 'ws')}/api/notifications/hub?ticket=${ticket}`, null, { headers: { Origin: ORIGIN, 'X-Forwarded-For': ip } });
    const finish = () => {
      if (done) return;
      done = true;
      if (!up) wsConnectOk.add(false);
      resolve();
    };
    const giveUp = setTimeout(() => { if (!up) { ws.close(); finish(); } }, WS_HANDSHAKE_TIMEOUT_S * 1000);
    ws.onopen = () => ws.send(`{"protocol":"json","version":1}${RS}`);
    ws.onmessage = (e) => {
      for (const rec of String(e.data).split(RS)) {
        if (!rec) continue;
        const m = JSON.parse(rec);
        if (m.type === 6) ws.send(`{"type":6}${RS}`);
        else if (m.error) {
          console.error(`ws handshake refused: ${m.error}`);
          ws.close();
        } else if (m.type === undefined && !up) { // the handshake answer `{}`
          up = true;
          clearTimeout(giveUp);
          wsConnectOk.add(true);
          wsConnectMs.add(Date.now() - started);
          wsSessions.add(1);
          setTimeout(() => ws.close(), holdMs);
        }
      }
    };
    ws.onerror = (e) => {
      if (!wsErrorLogged) console.error(`ws error: ${e.error ?? e.message ?? JSON.stringify(e)}`);
      wsErrorLogged = true;
    };
    ws.onclose = () => {
      clearTimeout(giveUp);
      finish();
    };
  });
}

const jitter = () => new Promise((r) => setTimeout(r, 1000 + Math.random() * 4000));

export async function wsFlow(data) {
  if (secondsLeft(data) <= 0) return sleep(1); // the scenario is over: don't spin until k6 stops it
  const s = session();
  const holdEnd = Date.now() + Math.min(WS_HOLD_SECONDS, secondsLeft(data)) * 1000;
  while (Date.now() < holdEnd) {
    const t = api('GET', '/api/notifications/ws-ticket', null, { token: fresh(s).token, ip: s.ip, flow: 'ws_ticket' });
    if (ok(t, 'ws ticket ok')) await hub(t.json('ticket'), s.ip, holdEnd - Date.now());
    else wsConnectOk.add(false); // no ticket, no connection: a failed attempt, retried after the jitter like a reconnect
    // A failed or early-closed connection is retried with jitter (1-5 s) while the hold lasts.
    if (Date.now() < holdEnd) await jitter();
  }
}
