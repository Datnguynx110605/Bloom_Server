// Pure: the last step that met every SLO before the first broken step. `steps` = [{ step, errorRate, tags: { [flow]: { p95, target } } }] in step order.
// A step meets SLO when every p95 < target and errorRate < 0.01; it breaks when any p95 > 2 x target or errorRate > maxErrorRate.
export function knee(steps, maxErrorRate) {
  let last = null;
  for (const s of steps) {
    let worst = { tag: null, ratio: -1 };
    for (const [tag, t] of Object.entries(s.tags)) if (t.p95 / t.target > worst.ratio) worst = { tag, ratio: t.p95 / t.target };
    if (worst.ratio > 2 || s.errorRate > maxErrorRate) return { knee: last, broke: s.step, firstTag: worst.ratio > 2 ? worst.tag : 'errors' };
    if (worst.ratio < 1 && s.errorRate < 0.01) last = s.step;
  }
  return { knee: last, broke: null, firstTag: null };
}
