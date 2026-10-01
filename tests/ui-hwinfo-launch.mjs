// Test de l'intégration HWiNFO64 : lancement au démarrage (préférence, par défaut activée, enregistrée),
// et le cas « détecté mais illisible » (bouton « Relancer en administrateur » sur la carte Températures).
// Lancer : node tests/ui-hwinfo-launch.mjs [dossier-des-captures]   (nécessite : dotnet build -c Test -p:Platform=x64 -o out-test/SystemPulse)
import { execFileSync } from 'node:child_process';
import path from 'node:path';
import { launch, connect, sleep, reporter } from './lib.mjs';

const shots = process.argv[2] || '.';
const { check, done } = reporter();
const click = (ui, sel) => ui.ev(`document.querySelector(${JSON.stringify(sel)}).click()`);

// 1. Préférence par défaut activée, décochable, enregistrée puis retrouvée au redémarrage ------------------
let app = launch(9850, {});
const prefsDir = app.dataDir;
try {
  const ui = await connect(9850);
  await sleep(3000);
  await click(ui, '#display-toggle');
  await sleep(500);
  check('case « Lancer HWiNFO64 » cochée par défaut', (await ui.ev(`document.getElementById('launch-hwinfo-toggle').checked`)) === true);
  await click(ui, '#launch-hwinfo-toggle');
  await sleep(600);
  const fs = await import('node:fs');
  const file = path.join(prefsDir, 'preferences.json');
  const saved = JSON.parse(fs.readFileSync(file, 'utf8'));
  check('préférence enregistrée (décochée)', saved.launchHwInfo === false, JSON.stringify(saved.launchHwInfo));
  ui.close();
  try { execFileSync('taskkill.exe', ['/PID', String(app.child.pid), '/T', '/F'], { stdio: 'ignore' }); } catch { /* déjà arrêté */ }
  await sleep(1500);
} finally { /* le dossier est réutilisé juste en dessous avant d'être nettoyé */ }

app = launch(9851, { SYSTEMPULSE_DATA: prefsDir });
try {
  const ui = await connect(9851);
  await sleep(3000);
  await click(ui, '#display-toggle');
  await sleep(500);
  check('après redémarrage : case toujours décochée', (await ui.ev(`document.getElementById('launch-hwinfo-toggle').checked`)) === false);
} finally { await app.stop(); }

// 2. HWiNFO détecté mais illisible (droits insuffisants) : bouton d'administration + note précise -----------
app = launch(9852, { SYSTEMPULSE_CPU_TEMP_MODE: 'hwinfo-denied' });
try {
  const ui = await connect(9852);
  await sleep(3500);
  const m = await ui.ev('window.systemPulse.getMetrics()');
  check('statut transmis à l\'interface', m && m.temperatures.cpuHwInfoStatus === 'accessDenied', JSON.stringify(m && m.temperatures));
  check('CPU affiché N/D (rien d\'inventé)', (await ui.ev(`document.getElementById('cpu-temp').textContent`)) === 'N/D');
  const visible = (await ui.ev(`!document.getElementById('temp-admin').hidden`)) === true;
  check('bouton « Relancer en administrateur » affiché', visible);
  const note = await ui.ev(`document.getElementById('temperature-note').textContent`);
  check('note précise (HWiNFO + administrateur)', note.includes('HWiNFO64') && note.includes('administrateur'), note);
  await ui.shot(path.join(shots, 'nat-hwinfo-refuse.png'));
} finally { await app.stop(); }

// 3. Cas normal (pas de statut forcé) : le bouton reste caché, aucune fausse alerte --------------------------
app = launch(9853, { SYSTEMPULSE_CPU_TEMP_C: '45' });
try {
  const ui = await connect(9853);
  await sleep(3000);
  check('bouton absent quand tout va bien', (await ui.ev(`document.getElementById('temp-admin').hidden`)) === true);
} finally { await app.stop(); }

done();
