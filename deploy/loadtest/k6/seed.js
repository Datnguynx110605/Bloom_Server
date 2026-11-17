// Idempotent seed: category + POOL published products with POOL_STOCK stock, and CUSTOMERS signed-in accounts.
// Admin work runs in setup() (one VU, paced by the admin rate limit); customers sign in in parallel, once each (P4).
import { check } from 'k6';
import exec from 'k6/execution';
import { num, str } from './lib/config.js';
import { adminToken, customer } from './lib/auth.js';
import { available, poolCategory, poolProduct, receive } from './lib/admin.js';

const CUSTOMERS = num('CUSTOMERS', 2000);
const POOL = num('POOL', 100);
const POOL_STOCK = num('POOL_STOCK', 1000000);

export const options = {
  setupTimeout: str('SEED_SETUP_TIMEOUT', '30m'),
  scenarios: {
    customers: { executor: 'shared-iterations', vus: Math.min(num('SEED_VUS', 50), CUSTOMERS), iterations: CUSTOMERS, maxDuration: str('SEED_MAX_DURATION', '30m') },
  },
  thresholds: { checks: ['rate==1'] },
};

export function setup() {
  const token = adminToken();
  poolCategory(token);
  let created = 0;
  const skuIds = [];
  for (let n = 1; n <= POOL; n++) {
    const p = poolProduct(token, n);
    if (p.created) created++;
    skuIds.push(p.skuId);
  }
  let topped = 0;
  for (let i = 0; i < skuIds.length; i += 100) {
    const chunk = skuIds.slice(i, i + 100);
    const have = available(token, chunk);
    for (const id of chunk) {
      const missing = POOL_STOCK - (have[id] ?? 0);
      if (missing > 0) { receive(token, id, missing); topped++; }
    }
  }
  console.log(`seed: ${created} created, ${POOL - created} existing products, ${topped} SKUs topped up to ${POOL_STOCK}`);
}

export default function () {
  let ok = false;
  try {
    ok = !!customer(exec.scenario.iterationInTest).token;
  } catch (e) {
    console.error(e.message);
  }
  check(ok, { 'customer signed in': (v) => v });
}
