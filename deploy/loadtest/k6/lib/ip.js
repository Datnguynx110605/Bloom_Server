import { num } from './config.js';

// X-Forwarded-For addresses (gateway trusts the compose subnet). Customers: 100.64.0.0 + i; anonymous: 100.96.0.0 + n % ANON_IPS.
const CUSTOMER_BASE = (100 << 24) + (64 << 16);
const ANON_BASE = (100 << 24) + (96 << 16);
const ANON_IPS = num('ANON_IPS', 4096);

function dotted(n) {
  return [n >>> 24, (n >>> 16) & 255, (n >>> 8) & 255, n & 255].join('.');
}

export const customerIp = (i) => dotted(CUSTOMER_BASE + i);
export const anonIp = (n) => dotted(ANON_BASE + (n % ANON_IPS));
export const ADMIN_IP = '100.127.255.1';
