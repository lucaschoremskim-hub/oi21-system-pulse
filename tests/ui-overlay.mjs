// Tests de l'overlay natif : affichage, style (clic traversant / déplaçable), glissement à la vraie souris, position mémorisée.
// Lancer : node tests/ui-overlay.mjs [dossier-des-captures]   (fenêtres visibles quelques secondes ; le curseur bouge brièvement)
import { execFileSync } from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { launch, connect, sleep, reporter, fakePresentMon } from './lib.mjs';

const here = path.dirname(fileURLToPath(import.meta.url));
const ps1 = path.join(here, 'win32.ps1');
const shots = process.argv[2] || '.';
const { check, done } = reporter();
const win = (...args) => execFileSync('powershell.exe', ['-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', ps1, ...args.map(String)]).toString().trim();
const rect = () => JSON.parse(win('rect'));
const click = (ui, sel) => ui.ev(`document.querySelector(${JSON.stringify(sel)}).click()`);
const fakeEnv = { FAKE_MODE: 'game', SYSTEMPULSE_FPS_CMD: 'node.exe', SYSTEMPULSE_FPS_ARGS: `"${fakePresentMon}"` };

const app = launch(9821, fakeEnv);
let moved = null;
try {
  const ui = await connect(9821);
  await sleep(3500);
  check('overlay absent au départ', rect().found === false || rect().visible === false);
  await click(ui, '#overlay-toggle');
  await sleep(2500);
  let r = rect();
  check('overlay affiché', r.found && r.visible, JSON.stringify(r));
  check('fenêtre superposée, au premier plan, sans focus', r.layered && r.topmost && r.noActivate);
  check('verrouillé : la souris traverse (clic traversant)', r.clickThrough === true);
  check('taille ajustée au contenu (~241 x 220)', r.w >= 200 && r.w <= 320 && r.h >= 150 && r.h <= 330, `(${r.w} x ${r.h})`);
  check('placé en haut à droite par défaut', r.x > 1200 && r.y < 60, `(${r.x}, ${r.y})`);
  win('shot', path.join(shots, 'nat-overlay.png'));

  // FPS dans l'overlay
  await click(ui, 'input[data-group=cards][data-key=fps]'); // démarre la mesure (faux)
  await click(ui, 'input[data-group=overlay][data-key=fps]');
  await sleep(4500);
  const r2 = rect();
  check('ligne FPS ajoutée à l\'overlay (fenêtre plus haute)', r2.h > r.h, `(${r.h} -> ${r2.h})`);
  win('shot', path.join(shots, 'nat-overlay-fps.png'));

  // mode déplacement
  check('bouton « Déplacer » actif', (await ui.ev(`document.getElementById('overlay-move').disabled`)) === false);
  await click(ui, '#overlay-move');
  await sleep(900);
  r = rect();
  check('mode déplacement : la souris ne traverse plus', r.clickThrough === false);
  check('libellé « Verrouiller »', ((await ui.ev(`document.getElementById('overlay-move-label').textContent`)) || '').includes('Verrouiller'));
  win('shot', path.join(shots, 'nat-overlay-deplacer.png'));
  const before = { x: r.x, y: r.y };
  win('drag', before.x + 60, before.y + 40, before.x + 60 - 150, before.y + 40 + 90);
  await sleep(800);
  const after = rect();
  moved = { x: after.x, y: after.y };
  check('glissement à la vraie souris exact (-150, +90)', after.x - before.x === -150 && after.y - before.y === 90, `(${after.x - before.x}, ${after.y - before.y})`);

  await click(ui, '#overlay-move');
  await sleep(900);
  check('verrouillage : la souris retraverse', rect().clickThrough === true);
  check('verrouillage : la fenêtre n\'a pas bougé', rect().x === moved.x && rect().y === moved.y);
  const file = path.join(app.dataDir, 'preferences.json');
  const prefs = fs.existsSync(file) ? JSON.parse(fs.readFileSync(file, 'utf8')) : {};
  check('position enregistrée', prefs.overlayPosition && prefs.overlayPosition.x === moved.x && prefs.overlayPosition.y === moved.y, JSON.stringify(prefs.overlayPosition));

  // extinction
  await click(ui, '#overlay-toggle');
  await sleep(1200);
  const off = rect();
  check('overlay éteint : fenêtre masquée', off.found === false || off.visible === false);
  ui.close();
} finally {
  try { execFileSync('taskkill.exe', ['/PID', String(app.child.pid), '/T', '/F'], { stdio: 'ignore' }); } catch { /* fermé */ }
  await sleep(1500);
}

// redémarrage : la position choisie est retrouvée
const again = launch(9822, { ...fakeEnv, SYSTEMPULSE_DATA: app.dataDir });
try {
  const ui2 = await connect(9822);
  await sleep(3500);
  await click(ui2, '#overlay-toggle');
  await sleep(2500);
  const r3 = rect();
  check('après redémarrage : l\'overlay revient à la position choisie', moved && r3.x === moved.x && r3.y === moved.y, `(${r3.x}, ${r3.y})`);
  ui2.close();
} finally {
  try { execFileSync('taskkill.exe', ['/PID', String(again.child.pid), '/T', '/F'], { stdio: 'ignore' }); } catch { /* fermé */ }
  await sleep(1500);
  fs.rmSync(app.dataDir, { recursive: true, force: true });
}
done();
