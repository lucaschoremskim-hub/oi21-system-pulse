// Tests de la fenêtre principale native : FPS (faux PresentMon), tenue dans la fenêtre, panneau, préférences.
// Lancer : node tests/ui-native.mjs [dossier-des-captures]   (nécessite : dotnet build -c Test -p:Platform=x64 -o out-test/SystemPulse)
import { execFileSync } from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';
import { launch, connect, sleep, reporter, fakePresentMon } from './lib.mjs';

const shots = process.argv[2] || '.';
const { check, done } = reporter();
const fakeEnv = (mode) => ({ FAKE_MODE: mode, SYSTEMPULSE_FPS_CMD: 'node.exe', SYSTEMPULSE_FPS_ARGS: `"${fakePresentMon}"` });
const fakeCount = () => Number(execFileSync('powershell.exe', ['-NoProfile', '-Command', "@(Get-CimInstance Win32_Process | Where-Object { $_.Name -eq 'node.exe' -and $_.CommandLine -match 'fake-presentmon' }).Count"]).toString().trim());
const scroll = `(() => { const c = document.querySelector('.dashboard-content'); return { over: c.scrollHeight - c.clientHeight, z: document.querySelector('.metrics-grid').style.zoom || '1' }; })()`;
const cards = `[...document.querySelectorAll('[data-card]:not([hidden])')].map(c => c.dataset.card).join(',')`;
const click = (ui, sel) => ui.ev(`document.querySelector(${JSON.stringify(sel)}).click()`);

// ---- 1. Jeu détecté ------------------------------------------------------------------------
let app = launch(9811, fakeEnv('game'));
try {
  const ui = await connect(9811);
  await sleep(3500);
  check('FPS décoché par défaut (carte masquée)', (await ui.ev(`document.querySelector('[data-card=fps]').hidden`)) === true);
  check('aucune mesure lancée par défaut', fakeCount() === 0);
  check('6 cartes visibles au départ', (await ui.ev(`document.querySelectorAll('[data-card]:not([hidden])').length`)) === 6);
  const size = await ui.ev(`({w: innerWidth, h: innerHeight})`);
  check('zone d\'affichage 590 x 360', Math.abs(size.w - 590) <= 2 && Math.abs(size.h - 360) <= 2, JSON.stringify(size));
  const s0 = await ui.ev(scroll);
  check('sans FPS : tout tient, aucun zoom', s0.over <= 1 && s0.z === '1', JSON.stringify(s0));
  await click(ui, 'input[data-group=cards][data-key=fps]');
  let value = NaN;
  for (let i = 0; i < 24 && !(value > 100); i += 1) { await sleep(500); value = Number(await ui.ev(`document.getElementById('fps-value').textContent`)); }
  check('cocher FPS : ~120 FPS affichés', value > 110 && value < 130, `(${value})`);
  check('application suivie affichée', ((await ui.ev(`document.getElementById('fps-note').textContent`)) || '').includes('Game.exe'));
  check('pastille LIVE', (await ui.ev(`document.getElementById('fps-chip').textContent`)) === 'LIVE');
  check('processus de mesure actif', fakeCount() >= 1);
  const s1 = await ui.ev(scroll);
  check('7 cartes : tout tient sans défilement', s1.over <= 1, JSON.stringify(s1));
  await ui.shot(path.join(shots, 'nat-fps-jeu.png'));
  await click(ui, 'input[data-group=cards][data-key=fps]'); await sleep(2500);
  check('décocher FPS : carte masquée', (await ui.ev(`document.querySelector('[data-card=fps]').hidden`)) === true);
  check('décocher FPS : mesure arrêtée', fakeCount() === 0);
  ui.close();
} finally { await app.stop(); }

// ---- 2. Droits insuffisants (cas le plus haut) + panneau ------------------------------------
app = launch(9812, fakeEnv('no-rights'));
try {
  const ui = await connect(9812);
  await sleep(3500);
  await click(ui, 'input[data-group=cards][data-key=fps]');
  let visible = false;
  for (let i = 0; i < 24 && !visible; i += 1) { await sleep(500); visible = (await ui.ev(`!document.getElementById('fps-admin').hidden`)) === true; }
  check('droits insuffisants : bouton « Relancer en administrateur »', visible);
  check('droits insuffisants : message explicatif', ((await ui.ev(`document.getElementById('fps-note').textContent`)) || '').includes('droits administrateur'));
  check('droits insuffisants : valeur N/D', (await ui.ev(`document.getElementById('fps-value').textContent`)) === 'N/D');
  const s = await ui.ev(scroll);
  check('cas le plus haut : tout tient sans défilement', s.over <= 1, JSON.stringify(s));
  check('zoom lisible (>= 0,65)', Number(s.z) >= 0.65, `(${s.z})`);
  await ui.shot(path.join(shots, 'nat-fps-admin.png'));
  await click(ui, '#display-toggle'); await sleep(600);
  const p = await ui.ev(`(() => { const c = document.querySelector('.dashboard-content'); return { over: c.scrollHeight - c.clientHeight, gridHidden: getComputedStyle(document.querySelector('.metrics-grid')).display === 'none' }; })()`);
  check('panneau ouvert : tout tient, cartes masquées', p.over <= 1 && p.gridHidden === true, JSON.stringify(p));
  await ui.shot(path.join(shots, 'nat-panneau.png'));
  await click(ui, '#display-close'); await sleep(600);
  const b = await ui.ev(scroll);
  check('panneau refermé : cartes de retour sans défilement', b.over <= 1, JSON.stringify(b));
  ui.close();
} finally { await app.stop(); }

// ---- 3. Format inattendu ---------------------------------------------------------------------
app = launch(9813, fakeEnv('bad-format'));
try {
  const ui = await connect(9813);
  await sleep(3500);
  await click(ui, 'input[data-group=cards][data-key=fps]');
  await sleep(4000);
  check('format inattendu signalé', ((await ui.ev(`document.getElementById('fps-note').textContent`)) || '').includes('inattendu'));
  ui.close();
} finally { await app.stop(); }

// ---- 4. Préférences enregistrées puis retrouvées ----------------------------------------------
app = launch(9814, {});
let prefsDir = app.dataDir;
try {
  const ui = await connect(9814);
  await sleep(3500);
  await click(ui, 'input[data-group=cards][data-key=cpu]');
  await click(ui, 'input[data-group=drives][data-key="C:"]');
  await sleep(800);
  check('carte CPU masquée', (await ui.ev(`document.querySelector('[data-card=cpu]').hidden`)) === true);
  check('disque C: retiré de la carte Stockage', (await ui.ev(`[...document.querySelectorAll('#storage-drives .drive-name')].map(e=>e.textContent).join(',')`)) === 'A:,D:');
  const file = path.join(prefsDir, 'preferences.json');
  check('preferences.json écrit', fs.existsSync(file));
  const saved = fs.existsSync(file) ? JSON.parse(fs.readFileSync(file, 'utf8')) : {};
  check('contenu enregistré', saved.visibility?.cards?.cpu === false && saved.visibility?.drives?.['C:'] === false);
  ui.close();
  // redémarrage sur le même dossier de données
  try { execFileSync('taskkill.exe', ['/PID', String(app.child.pid), '/T', '/F'], { stdio: 'ignore' }); } catch { /* fermé */ }
  await sleep(2000);
  const again = launch(9815, { SYSTEMPULSE_DATA: prefsDir });
  try {
    const ui2 = await connect(9815);
    await sleep(3500);
    check('après redémarrage : carte CPU toujours masquée', (await ui2.ev(`document.querySelector('[data-card=cpu]').hidden`)) === true);
    check('après redémarrage : disque C: toujours masqué', (await ui2.ev(`[...document.querySelectorAll('#storage-drives .drive-name')].map(e=>e.textContent).join(',')`)) === 'A:,D:');
    check('après redémarrage : case CPU décochée', (await ui2.ev(`document.querySelector('input[data-group=cards][data-key=cpu]').checked`)) === false);
    ui2.close();
  } finally {
    try { execFileSync('taskkill.exe', ['/PID', String(again.child.pid), '/T', '/F'], { stdio: 'ignore' }); } catch { /* fermé */ }
    await sleep(1500);
  }
} finally { await app.stop(); }

done();
