import { launch, connect, sleep } from './lib.mjs';
const app = launch(9880, process.env.NOTIMER ? { SYSTEMPULSE_NO_TIMER: "1" } : {});
try {
  const ui = await connect(9880);
  await sleep(5000);
  console.log(await ui.ev(`JSON.stringify({
    nav: (() => { const n = performance.getEntriesByType('navigation')[0]; return { type: n.type, start: Math.round(n.startTime), fetch: Math.round(n.fetchStart), req: Math.round(n.requestStart), resp: Math.round(n.responseStart), respEnd: Math.round(n.responseEnd), dcl: Math.round(n.domContentLoadedEventEnd), load: Math.round(n.loadEventEnd) }; })(),
    res: performance.getEntriesByType('resource').map(r => ({ n: r.name.split('/').pop(), start: Math.round(r.startTime), req: Math.round(r.requestStart), end: Math.round(r.responseEnd) })),
    paint: performance.getEntriesByType('paint').map(p => ({ n: p.name, t: Math.round(p.startTime) })),
    url: location.href
  }, null, 1)`));
  ui.close();
} finally { await app.stop(); }
