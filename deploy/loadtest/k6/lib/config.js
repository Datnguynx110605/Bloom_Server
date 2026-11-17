// Every tunable comes from __ENV with a default; no machine number lives outside a default.
export function num(name, def) {
  const raw = __ENV[name];
  if (raw === undefined || raw === '') return def;
  const v = Number(raw);
  if (Number.isNaN(v)) throw new Error(`${name}=${raw} is not a number`);
  return v;
}

export function str(name, def) {
  const raw = __ENV[name];
  return raw === undefined || raw === '' ? def : raw;
}

export const BASE_URL = str('BASE_URL', 'http://gateway:8080');
export const FAKES_URL = str('FAKES_URL', 'http://fakes:8080');
export const ORIGIN = str('ORIGIN', 'http://loadtest.bloom.test');

// The p95 targets in ms (design thresholds): the one copy every script builds its thresholds from.
export const SLO = { catalog: 150, checkout_launch: 300, wr_status: 50 };

const UNIT_S = { s: 1, m: 60, h: 3600 };
// A duration like 30s, 1m or 1h, in seconds.
export function seconds(d) {
  const m = /^(\d+)([smh])$/.exec(d);
  if (!m) throw new Error(`duration ${d} must look like 30s, 1m or 1h`);
  return Number(m[1]) * UNIT_S[m[2]];
}
