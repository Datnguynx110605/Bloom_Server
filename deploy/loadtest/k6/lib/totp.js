import { hmac } from 'k6/crypto';

const ALPHABET = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ234567';

function base32(s) {
  const out = [];
  let bits = 0, acc = 0;
  for (const c of s.replace(/=+$/, '').toUpperCase()) {
    const v = ALPHABET.indexOf(c);
    if (v < 0) throw new Error('invalid base32 secret');
    acc = (acc << 5) | v;
    bits += 5;
    if (bits >= 8) { bits -= 8; out.push((acc >> bits) & 255); }
  }
  return new Uint8Array(out).buffer;
}

// RFC 6238: HMAC-SHA1, 30 s step, 6 digits.
export function totp(base32Secret, unixSeconds) {
  const step = Math.floor(unixSeconds / 30);
  const msg = new DataView(new ArrayBuffer(8));
  msg.setUint32(0, Math.floor(step / 2 ** 32));
  msg.setUint32(4, step >>> 0);
  const h = new Uint8Array(hmac('sha1', base32(base32Secret), msg.buffer, 'binary'));
  const o = h[19] & 15;
  const code = (((h[o] & 127) << 24) | (h[o + 1] << 16) | (h[o + 2] << 8) | h[o + 3]) % 1000000;
  return String(code).padStart(6, '0');
}
