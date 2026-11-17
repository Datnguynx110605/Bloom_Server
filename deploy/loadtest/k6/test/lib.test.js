// Self-test of the pure helpers: run with `k6 run /scripts/test/lib.test.js`.
import { check } from 'k6';
import { totp } from '../lib/totp.js';
import { customerIp, anonIp } from '../lib/ip.js';

export const options = { vus: 1, iterations: 1, thresholds: { checks: ['rate==1'] } };

export default function () {
  check(totp('GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ', 59),         { 'rfc6238 t=59': v => v === '287082' });
  check(totp('GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ', 1111111109), { 'rfc6238 t=1111111109': v => v === '081804' });
  check(customerIp(0),   { 'first customer': v => v === '100.64.0.0' });
  check(customerIp(257), { 'carries into third octet': v => v === '100.64.1.1' });
  check(anonIp(4096),    { 'anon wraps at ANON_IPS': v => v === '100.96.0.0' });
}
