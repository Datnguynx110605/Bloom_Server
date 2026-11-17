// Stress test: the combined profile (catalog + mixed + waiting-room polling + WebSocket) grows by one increment per step until a step breaks.
// Step i (1-based) offers i x the *_STEP amount of each flow; k6 aborts on the first broken step and the summary names the knee.
// Every rate, count and duration is an env var with a default (calibrated for the machine in deploy/loadtest/README.md).
// A run that finds a broken step exits 99 by design (abortOnFail): chain verify with `;`, not `&&`.
import { num, seconds, str } from './lib/config.js';
import { catalogFlow, mixedFlow, launchFlow, wsFlow } from './lib/flows.js';
import { knee } from './lib/knee.js';
import { prepare, checkWebhooks, summaryLines, summaryOutputs } from './lib/run.js';

const STEPS = num('STEPS', 10);
const D = seconds(str('STEP_DURATION', '2m'));
const CATALOG_STEP = num('CATALOG_STEP', 5); // iterations/s added per step
const MIXED_STEP = num('MIXED_STEP', 1);      // iterations/s added per step
const WS_STEP = num('WS_STEP', 25);           // WebSocket sessions added per step
const STATUS_STEP = num('STATUS_STEP', 25);   // waiting-room users added per step
const MAX_ERROR_RATE = num('MAX_ERROR_RATE', 0.05);
const ALLOCATION = num('ALLOCATION', 100000); // large, with a slow admission: most users stay queued and poll
const ADMISSION_RATE = num('ADMISSION_RATE', 1);
const LAUNCH_DELAY = num('LAUNCH_DELAY', 10);
const PURCHASE_LIMIT = num('PURCHASE_LIMIT', 1);
const SLO = { catalog: 150, checkout_launch: 300, wr_status: 50 }; // p95 targets in ms

const scenarios = {};
const thresholds = {};
for (let i = 1; i <= STEPS; i++) {
  const at = { startTime: `${(i - 1) * D}s`, gracefulStop: '0s', tags: { step: String(i) } };
  const arrival = (exec, rate, vus, maxVus) => ({ executor: 'constant-arrival-rate', exec, rate, timeUnit: '1s', preAllocatedVUs: vus, maxVUs: maxVus, duration: `${D}s`, ...at });
  scenarios[`catalog_${i}`] = arrival('catalog', i * CATALOG_STEP, Math.ceil(i * CATALOG_STEP), Math.ceil(i * CATALOG_STEP) * 4);
  scenarios[`mixed_${i}`] = arrival('mixed', i * MIXED_STEP, Math.ceil(i * MIXED_STEP * 10), Math.ceil(i * MIXED_STEP * 10) * 2);
  scenarios[`ws_${i}`] = { executor: 'constant-vus', exec: 'ws', vus: i * WS_STEP, duration: `${D}s`, ...at };
  scenarios[`status_${i}`] = { executor: 'per-vu-iterations', exec: 'launch', vus: i * STATUS_STEP, iterations: 1, maxDuration: `${D}s`, ...at };
  // delayAbortEval counts from the start of the test: wait 30 s into this step so its first samples (cold connections) cannot abort the run.
  const abort = { abortOnFail: true, delayAbortEval: `${(i - 1) * D + 30}s` };
  for (const [tag, target] of Object.entries(SLO)) thresholds[`http_req_duration{flow:${tag},step:${i}}`] = [{ threshold: `p(95)<${2 * target}`, ...abort }];
  thresholds[`http_req_failed{step:${i}}`] = [{ threshold: `rate<${MAX_ERROR_RATE}`, ...abort }];
  thresholds[`dropped_iterations{step:${i}}`] = ['count>=0']; // never fails: makes the step's dropped count appear in the summary (a saturated generator would fake a higher knee)
}

export const options = { setupTimeout: str('SETUP_TIMEOUT', '5m'), scenarios, thresholds };

export function setup() {
  return prepare({ delaySeconds: LAUNCH_DELAY, admissionRate: ADMISSION_RATE, purchaseLimit: PURCHASE_LIMIT, allocation: ALLOCATION, testSeconds: STEPS * D });
}

export const catalog = catalogFlow;
export const mixed = mixedFlow;
export const launch = launchFlow;
export const ws = wsFlow;

export function teardown(data) {
  checkWebhooks(data.fakes);
}

// The per-step view of the summary: only steps that ran, and only flows that have samples in them.
function stepsOf(data) {
  const steps = [];
  for (let i = 1; i <= STEPS; i++) {
    const failed = data.metrics[`http_req_failed{step:${i}}`]?.values;
    if (!failed || failed.passes + failed.fails === 0) continue;
    const tags = {};
    for (const [tag, target] of Object.entries(SLO)) {
      const p95 = data.metrics[`http_req_duration{flow:${tag},step:${i}}`]?.values['p(95)'];
      if (p95 > 0) tags[tag] = { p95, target };
    }
    steps.push({ step: i, errorRate: failed.rate, dropped: data.metrics[`dropped_iterations{step:${i}}`]?.values.count ?? 0, tags });
  }
  return steps;
}

export function handleSummary(data) {
  const steps = stepsOf(data);
  const r = knee(steps, MAX_ERROR_RATE);
  const lines = steps.map((s) => `step ${s.step}: errors=${(s.errorRate * 100).toFixed(2)}% dropped=${s.dropped} ${Object.entries(s.tags).map(([t, v]) => `${t}=${v.p95.toFixed(1)}/${v.target}ms`).join(' ')}`);
  const text = [`KNEE step=${r.knee ?? 'none'} BROKE step=${r.broke ?? 'none'} FIRST=${r.firstTag ?? 'none'}`, ...lines, ...summaryLines(data)].join('\n');
  return summaryOutputs('stress', text, { ...data, knee: r, steps });
}
