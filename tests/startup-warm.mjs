// Compare le démarrage à froid (profil neuf) et à chaud (profil déjà utilisé, cas réel). Lancer : node tests/startup-warm.mjs
import { execFileSync } from 'node:child_process';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { launch, sleep } from './lib.mjs';

async function once(port, dataDir) {
  const t0 = performance.now();
  const app = launch(port, dataDir ? { SYSTEMPULSE_DATA: dataDir } : {});
  let tData = NaN, tBridge = NaN, ws, id = 0; const pend = new Map();
  try {
    while (performance.now() - t0 < 30000) {
      try {
        const t = (await (await fetch(`http://127.0.0.1:${port}/json`)).json()).find((x) => x.type === 'page' && /systempulse\.local/.test(x.url));
        if (t) { ws = new WebSocket(t.webSocketDebuggerUrl); await new Promise((r) => (ws.onopen = r)); break; }
      } catch { /* attente */ }
      await sleep(10);
    }
    ws.onmessage = (m) => { const d = JSON.parse(m.data); if (d.id && pend.has(d.id)) { pend.get(d.id)(d); pend.delete(d.id); } };
    const ev = (e) => new Promise((r) => { const k = ++id; pend.set(k, r); ws.send(JSON.stringify({ id: k, method: 'Runtime.evaluate', params: { expression: e, returnByValue: true } })); });
    while (performance.now() - t0 < 15000) {
      const s = (await ev(`JSON.stringify({api: !!window.systemPulse, cpu: (document.getElementById('cpu-value')||{}).textContent})`)).result?.result?.value;
      if (s) { const o = JSON.parse(s); if (o.api && Number.isNaN(tBridge)) tBridge = performance.now() - t0; if (o.cpu && o.cpu !== '—') { tData = performance.now() - t0; break; } }
      await sleep(10);
    }
    ws.close();
  } finally {
    try { execFileSync('taskkill.exe', ['/PID', String(app.child.pid), '/T', '/F'], { stdio: 'ignore' }); } catch { /* fermé */ }
    await sleep(2000);
    if (!dataDir) fs.rmSync(app.dataDir, { recursive: true, force: true });
  }
  return { tBridge, tData };
}
const fmt = (r) => `interface ${Math.round(r.tBridge)} ms, 1re donnée ${Math.round(r.tData)} ms`;
const cold = await once(9890, null);
console.log('Profil NEUF (1er lancement)      :', fmt(cold));
const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'sp-warm-'));
await once(9891, dir); // prépare le profil
const warm = [];
for (let i = 0; i < 3; i += 1) warm.push(await once(9892 + i, dir));
console.log('Profil DÉJÀ UTILISÉ (lancements suivants) :');
warm.forEach((r, i) => console.log(`  essai ${i + 1} :`, fmt(r)));
fs.rmSync(dir, { recursive: true, force: true });
