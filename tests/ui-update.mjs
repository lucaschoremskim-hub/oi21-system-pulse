// Test de la vérification de mise à jour : faux version.json local, aucun accès à Internet.
// Lancer : node tests/ui-update.mjs [dossier-des-captures]   (nécessite : dotnet build -c Test -p:Platform=x64 -o out-test/SystemPulse)
import http from 'node:http';
import path from 'node:path';
import { launch, connect, sleep, reporter } from './lib.mjs';

const shots = process.argv[2] || '.';
const { check, done } = reporter();
const good = 'https://github.com/lucaschoremskim-hub/system-pulse/releases/latest/download/SystemPulse.zip';
let payload = { version: '99.0.0', url: good, notes: 'Test' };
const server = http.createServer((req, res) => { res.setHeader('content-type', 'application/json'); res.end(JSON.stringify(payload)); });
await new Promise((r) => server.listen(9820, '127.0.0.1', r));
const url = 'http://127.0.0.1:9820/version.json';
const linkState = (ui) => ui.ev(`(() => { const l = document.getElementById('update-link'); return { hidden: l.hidden, text: l.textContent }; })()`);

async function scenario(name, expectVisible, env) {
  const port = 9821;
  const app = launch(port, env);
  try {
    const ui = await connect(port);
    await sleep(3000);
    const s = await linkState(ui);
    check(`${name} : lien ${expectVisible ? 'affiché' : 'absent'}`, s.hidden === !expectVisible, JSON.stringify(s));
    return { ui, s };
  } finally { await app.stop(); }
}

// 1. version plus récente
let app = launch(9821, { SYSTEMPULSE_UPDATE_URL: url });
try {
  const ui = await connect(9821);
  await sleep(3000);
  const s = await linkState(ui);
  check('version plus récente : lien affiché', s.hidden === false, JSON.stringify(s));
  check('texte du lien', s.text === 'Version 99.0.0 disponible', s.text);
  await ui.shot(path.join(shots, 'nat-mise-a-jour.png'));
} finally { await app.stop(); }

// 2. même version
payload = { version: '2.1.0', url: good, notes: '' };
await scenario('même version', false, { SYSTEMPULSE_UPDATE_URL: url });

// 3. lien non autorisé (hôte inconnu)
payload = { version: '99.0.0', url: 'https://exemple.invalid/x.zip', notes: '' };
await scenario('lien hors liste autorisée', false, { SYSTEMPULSE_UPDATE_URL: url });

// 4. serveur injoignable
server.close();
await scenario('hors ligne', false, { SYSTEMPULSE_UPDATE_URL: url });

done();
