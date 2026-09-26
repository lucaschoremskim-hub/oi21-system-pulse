// Compare le démarrage de l'interface selon les arguments du moteur WebView2 (3 essais chacun). Lancer : node tests/timeline-args.mjs
import { launch, sleep } from './lib.mjs';
const variants = {
  'aucun réglage': '',
  'sans SmartScreen': '--disable-features=msSmartScreenProtection',
  'sans SmartScreen + sans GPU': '--disable-features=msSmartScreenProtection --disable-gpu'
};
async function once(port, args) {
  const t0 = performance.now();
  const app = launch(port, { SYSTEMPULSE_WV2_ARGS: args });
  let tBridge = NaN, tData = NaN, ws, id = 0; const pend = new Map();
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
  } finally { await app.stop(); }
  return { tBridge, tData };
}
let port = 9860;
for (const [name, args] of Object.entries(variants)) {
  const runs = [];
  for (let i = 0; i < 3; i += 1) runs.push(await once(port++, args));
  const med = (a) => Math.round(a.slice().sort((x, y) => x - y)[1]);
  console.log(`${name.padEnd(40)} interface: ${runs.map((r) => Math.round(r.tBridge)).join('/')} ms (méd. ${med(runs.map((r) => r.tBridge))})   1re donnée: ${runs.map((r) => Math.round(r.tData)).join('/')} ms (méd. ${med(runs.map((r) => r.tData))})`);
}
