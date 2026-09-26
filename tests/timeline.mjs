import { launch, sleep } from './lib.mjs';
const t0 = performance.now();
const app = launch(9850);
const marks = {};
const mark = (k) => { if (marks[k] === undefined) marks[k] = Math.round(performance.now() - t0); };
try {
  let ws, id = 0; const pend = new Map();
  while (performance.now() - t0 < 30000) {
    try {
      const t = (await (await fetch('http://127.0.0.1:9850/json')).json()).find((x) => x.type === 'page' && /systempulse\.local/.test(x.url));
      if (t) { mark('cible CDP visible'); ws = new WebSocket(t.webSocketDebuggerUrl); await new Promise((r) => (ws.onopen = r)); break; }
    } catch { /* attente */ }
    await sleep(10);
  }
  ws.onmessage = (m) => { const d = JSON.parse(m.data); if (d.id && pend.has(d.id)) { pend.get(d.id)(d); pend.delete(d.id); } };
  const ev = (e) => new Promise((r) => { const k = ++id; pend.set(k, r); ws.send(JSON.stringify({ id: k, method: 'Runtime.evaluate', params: { expression: e, returnByValue: true } })); });
  while (performance.now() - t0 < 12000) {
    const s = (await ev(`JSON.stringify({rs: document.readyState, api: !!(window.systemPulse), cpu: (document.getElementById('cpu-value')||{}).textContent, st: (document.getElementById('global-status')||{}).textContent, drv: document.querySelectorAll('#storage-drives .drive-name').length, gpu: (document.getElementById('gpu-detail')||{}).textContent})`)).result?.result?.value;
    if (s) {
      const o = JSON.parse(s);
      if (o.rs === 'interactive' || o.rs === 'complete') mark('page prête (' + o.rs + ')');
      if (o.api) mark('pont chargé');
      if (o.cpu && o.cpu !== '—') mark('CPU affiché');
      if (o.st && !/Connexion/.test(o.st)) mark('statut « connecté »');
      if (o.drv > 0) mark('disques affichés');
      if (o.gpu && !/Détection/.test(o.gpu)) mark('GPU affiché');
    }
    await sleep(10);
  }
  ws.close();
} finally { await app.stop(); }
console.log(Object.entries(marks).map(([k, v]) => `${String(v).padStart(5)} ms  ${k}`).join('\n'));
