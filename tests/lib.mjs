// Outils communs des tests de la version native (WebView2). Pilotage de l'interface par le protocole de débogage
// de WebView2 (port local, réservé à la version de test compilée avec -Test), profil de données isolé.
import { spawn, execFileSync } from 'node:child_process';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

export const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
export const testExe = path.join(root, 'out-test', 'SystemPulse', 'SystemPulse.exe');
export const fakePresentMon = path.join(root, 'tests', 'fake-presentmon.mjs');
export const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

export function reporter() {
  const results = [];
  return {
    check(name, ok, extra = '') { results.push(!!ok); console.log(`${ok ? 'OK   ' : 'ECHEC'} ${name} ${extra}`); },
    done() { console.log(`\n${results.filter(Boolean).length}/${results.length} vérifications réussies`); process.exit(results.every(Boolean) ? 0 : 1); }
  };
}

export function launch(port, extraEnv = {}) {
  const dataDir = fs.mkdtempSync(path.join(os.tmpdir(), 'sp-native-'));
  const env = { ...process.env, SYSTEMPULSE_DATA: dataDir, SYSTEMPULSE_DEBUG_PORT: String(port), ...extraEnv };
  const child = spawn(testExe, [], { cwd: path.dirname(testExe), stdio: 'ignore', env });
  return {
    dataDir, child, port,
    async stop() {
      try { execFileSync('taskkill.exe', ['/PID', String(child.pid), '/T', '/F'], { stdio: 'ignore' }); } catch { /* déjà arrêté */ }
      await sleep(1500);
      fs.rmSync(dataDir, { recursive: true, force: true });
    }
  };
}

export async function connect(port, pick = (t) => t.type === 'page' && /systempulse\.local/.test(t.url)) {
  for (let i = 0; i < 100; i += 1) {
    try {
      const t = (await (await fetch(`http://127.0.0.1:${port}/json`)).json()).find(pick);
      if (t) {
        const ws = new WebSocket(t.webSocketDebuggerUrl);
        await new Promise((r) => (ws.onopen = r));
        let id = 0; const pend = new Map();
        ws.onmessage = (m) => { const d = JSON.parse(m.data); if (d.id && pend.has(d.id)) { pend.get(d.id)(d); pend.delete(d.id); } };
        const send = (method, params = {}) => new Promise((r) => { const k = ++id; pend.set(k, r); ws.send(JSON.stringify({ id: k, method, params })); });
        return {
          send,
          ev: async (expression) => (await send('Runtime.evaluate', { expression, returnByValue: true, awaitPromise: true })).result?.result?.value,
          shot: async (file) => { const s = await send('Page.captureScreenshot', { format: 'png' }); fs.writeFileSync(file, Buffer.from(s.result.data, 'base64')); },
          close: () => ws.close()
        };
      }
    } catch { /* attente */ }
    await sleep(300);
  }
  throw new Error('interface introuvable (port ' + port + ')');
}
