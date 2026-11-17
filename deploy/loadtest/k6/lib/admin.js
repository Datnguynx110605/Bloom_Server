import { sleep } from 'k6';
import { ADMIN_IP } from './ip.js';
import { api, must, uuid } from './http.js';

// Admin calls come from one IP and one user, so they share one token bucket (120/min, burst 60): a 429 is waited out, not failed.
// A GET (or a POST carrying an Idempotency-Key, so a repeat is safe) that times out at the gateway (504, a cold service) is retried the same way.
function adm(method, path, body, token, headers) {
  for (let attempt = 0; ; attempt++) {
    const res = api(method, path, body, { token, ip: ADMIN_IP, flow: 'admin', headers, expect: [200, 201, 204, 429, 504] });
    const retry = res.status === 429 || (res.status === 504 && (method === 'GET' || headers?.['Idempotency-Key']));
    if (!retry || attempt >= 60) return must(res, `${method} ${path}`);
    sleep(Number(res.headers['Retry-After']) || 1);
  }
}

let poolCategoryId;

// The category `lt-pool`, created on first use (idempotent by slug).
export function poolCategory(token) {
  if (poolCategoryId) return poolCategoryId;
  const found = (adm('GET', '/api/catalog/categories', null, token).categories ?? []).find((c) => c.slug === 'lt-pool');
  poolCategoryId = found ? found.id : adm('POST', '/api/admin/catalog/categories', { name: 'lt-pool', slug: 'lt-pool' }, token).id;
  return poolCategoryId;
}

// A physical product with one priced SKU, published unless it is a launch product (the launch's stage drives its visibility).
function createProduct(token, name, { publish }) {
  const product = adm('POST', '/api/admin/catalog/products',
    { categoryId: poolCategory(token), kind: 'physical', name, slug: name, descriptionMd: 'Load-test product.' }, token);
  return finishProduct(token, product, name.toUpperCase(), publish);
}

// Adds the SKU if the product has none and publishes if asked; safe to repeat on a half-built product.
function finishProduct(token, loaded, code, publish) {
  let product = loaded;
  if (!product.skus?.length) {
    adm('POST', `/api/admin/catalog/products/${product.id}/skus`, { code, priceCents: 19900 }, token);
    product = adm('GET', `/api/admin/catalog/products/${product.id}`, null, token);
  }
  if (publish && product.status !== 'active') adm('POST', `/api/admin/catalog/products/${product.id}/publish`, { version: product.version }, token);
  return { productId: product.id, skuId: product.skus[0].id };
}

// Product `lt-pool-<n>`: found by name or created. `created` tells the seed what it did.
export function poolProduct(token, n) {
  const name = `lt-pool-${n}`;
  // shortcut: first page (100) of the contains-match only, so n=1 is ambiguous once POOL > ~110; the slug-taken error would then fail loudly.
  const list = adm('GET', `/api/admin/catalog/products?q=${name}&pageSize=100`, null, token);
  const found = (list.items ?? []).find((p) => p.name === name);
  if (!found) return { ...createProduct(token, name, { publish: true }), created: true };
  return finishProduct(token, adm('GET', `/api/admin/catalog/products/${found.id}`, null, token), name.toUpperCase(), true);
}

// Stock on hand available per SKU id (a SKU never received is absent -> 0). At most 100 ids per call.
export function available(token, skuIds) {
  const q = skuIds.map((id) => `skuIds=${id}`).join('&');
  const items = adm('GET', `/api/admin/inventory/stock?${q}&pageSize=100`, null, token).items ?? [];
  return Object.fromEntries(items.map((s) => [s.skuId, s.available ?? 0]));
}

export function receive(token, skuId, qty) {
  return adm('POST', '/api/admin/inventory/receipts', { note: 'loadtest', lines: [{ skuId, quantity: qty }] }, token, { 'Idempotency-Key': uuid() });
}

// A new launch of one new product/SKU: opens in delaySeconds, then admission runs at admissionRate.
export function freshLaunch(token, { delaySeconds, admissionRate, purchaseLimit, allocation, stock }) {
  const stamp = Date.now();
  const { productId, skuId } = createProduct(token, `lt-launch-${stamp}`, { publish: false });
  receive(token, skuId, stock);
  const at = Date.now(); // the delay counts from here, after the product and stock exist
  const launchTime = new Date(at + delaySeconds * 1000).toISOString();
  const releaseDate = new Date(at + (delaySeconds + 86400) * 1000).toISOString(); // must be after launch_time (01-launch §4.1)
  let l = adm('POST', '/api/admin/launches', { name: `lt-launch-${stamp}`, launchTime, releaseDate, admissionRate }, token);
  const id = l.id;
  l = adm('POST', `/api/admin/launches/${id}/products`, { productId, purchaseLimit }, token);
  l = adm('POST', `/api/admin/launches/${id}/skus`, { skuId, productId, allocation }, token);
  adm('POST', `/api/admin/launches/${id}/publish`, { version: l.version }, token);
  return { launchId: id, productId, skuId, launchTime };
}

export function launch(token, launchId) {
  return adm('GET', `/api/admin/launches/${launchId}`, null, token);
}

// A cheap read that wakes launch-api (its first call after a start can outlast the gateway's 5 s), so the POSTs of freshLaunch are not the cold ones.
export function listLaunches(token) {
  return adm('GET', '/api/admin/launches', null, token);
}
