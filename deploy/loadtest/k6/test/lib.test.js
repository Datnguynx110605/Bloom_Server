// Self-test of the pure helpers: run with `k6 run /scripts/test/lib.test.js`.
import { check } from 'k6';
import { totp } from '../lib/totp.js';
import { customerIp, anonIp } from '../lib/ip.js';
import { knee } from '../lib/knee.js';

export const options = { vus: 1, iterations: 1, thresholds: { checks: ['rate==1'] } };

const t = { catalog: 150, wr_status: 50 };
const s = (step, cat, st, err = 0) => ({ step, errorRate: err, tags: { catalog: { p95: cat, target: t.catalog }, wr_status: { p95: st, target: t.wr_status } } });

export default function () {
  check(totp('GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ', 59),         { 'rfc6238 t=59': v => v === '287082' });
  check(totp('GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ', 1111111109), { 'rfc6238 t=1111111109': v => v === '081804' });
  check(customerIp(0),   { 'first customer': v => v === '100.64.0.0' });
  check(customerIp(257), { 'carries into third octet': v => v === '100.64.1.1' });
  check(anonIp(4096),    { 'anon wraps at ANON_IPS': v => v === '100.96.0.0' });
  check(knee([s(1,100,30), s(2,140,45), s(3,320,60)], 0.05), { 'knee before latency break': r => r.knee === 2 && r.broke === 3 && r.firstTag === 'catalog' });
  check(knee([s(1,100,30), s(2,200,40), s(3,140,120)], 0.05), { 'over SLO but not broken is not the knee': r => r.knee === 1 && r.broke === 3 && r.firstTag === 'wr_status' });
  check(knee([s(1,100,30, 0.2)], 0.05),                          { 'errors break step 1': r => r.knee === null && r.broke === 1 && r.firstTag === 'errors' });
  check(knee([s(1,100,30), s(2,120,40)], 0.05),                  { 'never broke': r => r.knee === 2 && r.broke === null });
}
