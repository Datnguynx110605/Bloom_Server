// Load test: anonymous catalog browsing, signed-in mixed shoppers, a launch with its waiting room, and WebSocket sessions, all at once.
// Every rate, count and duration is an env var with a default (a placeholder until the machine is measured).
import { num, seconds, str } from './lib/config.js';
import { catalogFlow, mixedFlow, launchFlow, wsFlow } from './lib/flows.js';
import { prepare, checkWebhooks, summaryLines, summaryOutputs } from './lib/run.js';

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

export function setup() {
  return prepare({ delaySeconds: LAUNCH_DELAY, admissionRate: ADMISSION_RATE, purchaseLimit: PURCHASE_LIMIT, allocation: ALLOCATION, testSeconds: TEST_SECONDS });
}

export const catalog = catalogFlow;
export const mixed = mixedFlow;
export const launch = launchFlow;
export const ws = wsFlow;

export function teardown(data) {
  checkWebhooks(data.fakes);
}

export function handleSummary(data) {
  return summaryOutputs('load', summaryLines(data).join('\n'), data);
}
