// Spike test: baseline traffic, then a launch opens with SPIKE_USERS in the waiting room and SPIKE_WS WebSocket sessions, then traffic falls back.
// Phases (seconds from the start): baseline + spike run until LAUNCH_DELAY + SPIKE_HOLD, then recovery runs RECOVERY seconds.
// Every rate, count and duration is an env var with a default (a placeholder until the machine is measured).
import { sleep, fail } from 'k6';
import exec from 'k6/execution';
import { num, str } from './lib/config.js';
import { adminToken } from './lib/auth.js';
import { launch as getLaunch } from './lib/admin.js';
import { catalogFlow, mixedFlow, launchFlow, wsFlow, WS_HOLD_SECONDS } from './lib/flows.js';
import { prepare, checkWebhooks, summaryLines, summaryOutputs } from './lib/run.js';

const BASE_CATALOG_RATE = num('BASE_CATALOG_RATE', 50);
const BASE_MIXED_RATE = num('BASE_MIXED_RATE', 10);
const SPIKE_USERS = num('SPIKE_USERS', 2000);
const SPIKE_WS = num('SPIKE_WS', 1000);
const SPIKE_RAMP = num('SPIKE_RAMP', 60);
const LAUNCH_DELAY = num('LAUNCH_DELAY', 180);
const SPIKE_HOLD = num('SPIKE_HOLD', 180);
const RECOVERY = num('RECOVERY', 180);
const ALLOCATION = num('ALLOCATION', 1000);
const PURCHASE_LIMIT = num('PURCHASE_LIMIT', 1);
const ADMISSION_RATE = num('ADMISSION_RATE', 20);
const SPIKE_END = LAUNCH_DELAY + SPIKE_HOLD;
const TEST_SECONDS = SPIKE_END + RECOVERY;

// Constant arrival rate (open model); a mixed iteration lasts several seconds (think time), so it needs more VUs per iteration/s.
function arrival(exec, rate, vus, maxVus, startTime, duration, phase) {
  return { executor: 'constant-arrival-rate', exec, rate, timeUnit: '1s', preAllocatedVUs: vus, maxVUs: maxVus, startTime: `${startTime}s`, duration: `${duration}s`, tags: { phase } };
}
const catalogVus = num('CATALOG_VUS', Math.ceil(BASE_CATALOG_RATE));
const mixedVus = num('MIXED_VUS', Math.ceil(BASE_MIXED_RATE * 10));
const catalogMax = num('CATALOG_MAX_VUS', catalogVus * 4);
const mixedMax = num('MIXED_MAX_VUS', mixedVus * 2);

export const options = {
  setupTimeout: str('SETUP_TIMEOUT', '5m'),
  scenarios: {
    baseline_catalog: arrival('catalog', BASE_CATALOG_RATE, catalogVus, catalogMax, 0, SPIKE_END, 'spike'),
    baseline_mixed: arrival('mixed', BASE_MIXED_RATE, mixedVus, mixedMax, 0, SPIKE_END, 'spike'),
    // Everyone joins the waiting room within the first SPIKE_RAMP seconds (spread by VU number), well before the launch opens.
    spike_launch: { executor: 'per-vu-iterations', exec: 'spikeLaunch', vus: SPIKE_USERS, iterations: 1, maxDuration: `${TEST_SECONDS}s` },
    spike_ws: {
      executor: 'ramping-vus', exec: 'ws', startVUs: 0, gracefulRampDown: `${WS_HOLD_SECONDS + 5}s`,
      stages: [{ target: SPIKE_WS, duration: `${SPIKE_RAMP}s` }, { target: SPIKE_WS, duration: `${Math.max(SPIKE_END - SPIKE_RAMP, 1)}s` }, { target: 0, duration: '1s' }],
    },
    recovery_catalog: arrival('catalog', BASE_CATALOG_RATE, catalogVus, catalogMax, SPIKE_END, RECOVERY, 'recovery'),
    recovery_mixed: arrival('mixed', BASE_MIXED_RATE, mixedVus, mixedMax, SPIKE_END, RECOVERY, 'recovery'),
  },
  thresholds: {
    http_req_failed: ['rate<0.05'],
    server_errors: ['rate<0.01'],
    // Recovery: the load test's latency targets again, on the traffic that is back at baseline.
    'http_req_duration{flow:catalog,phase:recovery}': ['p(95)<150'],
    'http_req_duration{flow:checkout_launch,phase:recovery}': ['p(95)<300'],
    'http_req_duration{flow:wr_status,phase:recovery}': ['p(95)<50'],
    'http_req_failed{phase:recovery}': ['rate<0.01'],
    'dropped_iterations{phase:recovery}': ['count==0'], // a generator that cannot keep the rate would flatter the latency
  },
};

export function setup() {
  return prepare({ delaySeconds: LAUNCH_DELAY, admissionRate: ADMISSION_RATE, purchaseLimit: PURCHASE_LIMIT, allocation: ALLOCATION, testSeconds: TEST_SECONDS });
}

export const catalog = catalogFlow;
export const mixed = mixedFlow;
export const ws = wsFlow;
export function spikeLaunch(data) {
  sleep(((exec.vu.idInScenario - 1) / SPIKE_USERS) * SPIKE_RAMP);
  launchFlow(data);
}

export function teardown(data) {
  // BI-01 in numbers: the launch is sold out, or its holds (held + sold; expired holds are back in the allocation) account for the whole allocation.
  const sku = (getLaunch(adminToken(), data.launchId).skus ?? []).find((s) => s.skuId === data.launchSkuId);
  if (!sku) fail(`launch ${data.launchId} has no SKU ${data.launchSkuId}`);
  if (sku.held + sku.sold !== sku.allocation) fail(`allocation not fully accounted for (SPIKE_USERS must exceed ALLOCATION): ${JSON.stringify(sku)}`);
  console.log(`launch: ${sku.sold} sold + ${sku.held} held of ${sku.allocation}`);
  checkWebhooks(data.fakes);
}

export function handleSummary(data) {
  return summaryOutputs('spike', summaryLines(data).join('\n'), data);
}
