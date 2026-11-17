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
