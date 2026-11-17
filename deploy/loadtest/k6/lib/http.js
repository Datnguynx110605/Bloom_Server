import http from 'k6/http';
import { randomBytes } from 'k6/crypto';
import { Rate } from 'k6/metrics';
import { BASE_URL, ORIGIN } from './config.js';

// Share of calls answered 5xx (the spike's "no 5xx storm" threshold).
export const serverErrors = new Rate('server_errors');

// One JSON call through the gateway. A fresh empty cookie jar per call: sessions are carried by hand (Cookie header), never by the VU jar.
export function api(method, path, body, { token, ip, flow, headers, expect } = {}) {
  const h = { Origin: ORIGIN, ...headers };
  if (body !== undefined && body !== null) h['Content-Type'] = 'application/json';
  if (ip) h['X-Forwarded-For'] = ip;
  if (token) h.Authorization = `Bearer ${token}`;
  const res = http.request(method, BASE_URL + path, body === undefined || body === null ? null : JSON.stringify(body), {
    headers: h,
    tags: flow ? { flow } : {},
    jar: new http.CookieJar(),
    responseCallback: http.expectedStatuses(...(expect ?? [200, 201, 204])),
  });
  serverErrors.add(res.status >= 500);
  return res;
}

// The JSON body of a 2xx response; anything else throws with the status and body.
export function must(res, what) {
  if (res.status < 200 || res.status > 299) throw new Error(`${what}: ${res.status} ${String(res.body).slice(0, 300)}`);
  return res.body ? res.json() : {};
}

// A v4 UUID (Idempotency-Key).
export function uuid() {
  const b = new Uint8Array(randomBytes(16));
  b[6] = (b[6] & 15) | 64;
  b[8] = (b[8] & 63) | 128;
  const x = Array.from(b, (v) => v.toString(16).padStart(2, '0')).join('');
  return `${x.slice(0, 8)}-${x.slice(8, 12)}-${x.slice(12, 16)}-${x.slice(16, 20)}-${x.slice(20)}`;
}
