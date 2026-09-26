// Mesure : lancement -> interface chargée -> première donnée affichée (3 essais, profil isolé). Lancer : node tests/startup-bench.mjs
import { execFileSync } from 'node:child_process';
import { launch, sleep } from './lib.mjs';

async function once(port) {
  const t0 = performance.now();
  const app = launch(port);
  let tPage = NaN, tData = NaN;
  try {
    let ws, id = 0; const pend = new Map();
    while (performance.now() - t0 < 30000) {
      try {
        const t = (await (await fetch(`http://127.0.0.1:${port}/json`)).json()).find((x) => x.type === 'page' && /systempulse\.local/.test(x.url));
        if (t) { tPage = performance.now() - t0; ws = new WebSocket(t.webSocketDebuggerUrl); await new Promise((r) => (ws.onopen = r)); break; }
      } catch { /* attente */ }
      await sleep(15);
    }
    if (ws) {
      ws.onmessage = (m) => { const d = JSON.parse(m.data); if (d.id && pend.has(d.id)) { pend.get(d.id)(d); pend.delete(d.id); } };
      const ev = (e) => new Promise((r) => { const k = ++id; pend.set(k, r); ws.send(JSON.stringify({ id: k, method: 'Runtime.evaluate', params: { expression: e, returnByValue: true } })); });
      while (performance.now() - t0 < 30000) {
        const v = (await ev(`(document.getElementById('cpu-value')||{}).textContent`)).result?.result?.value;
        if (v && v !== '—') { tData = performance.now() - t0; break; }
        await sleep(15);
      }
      ws.close();
    }
  } finally { await app.stop(); }
  return { tPage, tData };
}
const runs = [];
for (let i = 0; i < 3; i += 1) runs.push(await once(9840 + i));
const med = (a) => a.slice().sort((x, y) => x - y)[1];
console.log(`Interface chargée : ${runs.map((r) => Math.round(r.tPage)).join(' / ')} ms (médiane ${Math.round(med(runs.map((r) => r.tPage)))} ms)`);
console.log(`1re donnée affichée : ${runs.map((r) => Math.round(r.tData)).join(' / ')} ms (médiane ${Math.round(med(runs.map((r) => r.tData)))} ms)`);
