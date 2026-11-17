import http from 'k6/http';
import { FAKES_URL, num, str } from './config.js';
import { ADMIN_IP, customerIp } from './ip.js';
import { api, must } from './http.js';
import { totp } from './totp.js';

const REFRESH_AFTER_S = num('REFRESH_AFTER_S', 600);
const now = () => Date.now() / 1000;

// Staff sign-in: password, then the TOTP code of the seeded admin (seed-staff.sql).
export function adminToken() {
  const password = str('ADMIN_PASSWORD', '');
  const secret = str('ADMIN_TOTP_BASE32', '');
  if (!password || !secret) throw new Error('ADMIN_PASSWORD and ADMIN_TOTP_BASE32 must be set');
  const opts = { ip: ADMIN_IP, flow: 'auth' };
  const step1 = must(api('POST', '/api/auth/sign-in', { email: str('ADMIN_EMAIL', 'admin@loadtest.bloom.test'), password }, opts), 'admin sign-in');
  const step2 = must(api('POST', '/api/auth/staff/mfa', { mfaChallengeId: step1.mfaChallengeId, code: totp(secret, now()) }, opts), 'admin mfa');
  return step2.accessToken;
}

// The customer's own session: Google fake ID token -> real /api/auth/google; the refresh cookie is kept for fresh().
export function customer(i) {
  const email = `customer${i}@loadtest.bloom.test`;
  const fake = http.post(`${FAKES_URL}/google/id-token`, JSON.stringify({ email, name: `Customer ${i}` }),
    { headers: { 'Content-Type': 'application/json' }, tags: { flow: 'auth' } });
  const ip = customerIp(i);
  const res = api('POST', '/api/auth/google', { idToken: must(fake, 'google id-token').idToken }, { ip, flow: 'auth' });
  return { i, ip, token: must(res, `sign-in customer ${i}`).accessToken, cookie: res.cookies.bloom_rt[0].value, issuedAt: now() };
}

// Refreshes the session in place when it is older than REFRESH_AFTER_S (one refresh per session at a time: the caller is one VU).
export function fresh(session) {
  if (now() - session.issuedAt <= REFRESH_AFTER_S) return session;
  const res = api('POST', '/auth/refresh', null, { ip: session.ip, flow: 'auth', headers: { Cookie: `bloom_rt=${session.cookie}` } });
  session.token = must(res, `refresh customer ${session.i}`).accessToken;
  session.cookie = res.cookies.bloom_rt?.[0]?.value ?? session.cookie;
  session.issuedAt = now();
  return session;
}
