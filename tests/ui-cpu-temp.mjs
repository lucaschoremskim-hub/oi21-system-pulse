// Test de la température CPU (HWiNFO64 si présent, sinon zones ACPI root\WMI MSAcpi_ThermalZoneTemperature) :
// valeur forcée pour un résultat déterministe (le test tourne aussi sur des cartes mères qui n'exposent rien,
// comme celle-ci), niveaux d'alerte par ligne (CPU et GPU distincts), carte au pire des deux, cas honnête
// « non exposée » (N/D, pas de fausse valeur), et les deux chemins réels (ACPI seul, puis sans rien forcer).
// Lancer : node tests/ui-cpu-temp.mjs [dossier-des-captures]   (nécessite : dotnet build -c Test -p:Platform=x64 -o out-test/SystemPulse)
import path from 'node:path';
import { launch, connect, sleep, reporter } from './lib.mjs';

const shots = process.argv[2] || '.';
const { check, done } = reporter();
const rowLevel = (ui, sel) => ui.ev(`document.querySelector(${JSON.stringify(sel)})?.dataset.level`);
const cardLevel = (ui) => ui.ev(`document.querySelector('[data-card=temperatures]')?.dataset.level`);

// 1. Valeur normale (ok) -------------------------------------------------------------------
let app = launch(9840, { SYSTEMPULSE_CPU_TEMP_C: '52' });
try {
  const ui = await connect(9840);
  let m = null;
  for (let i = 0; i < 20 && !(m && m.temperatures && m.temperatures.cpu !== null); i += 1) { await sleep(500); m = await ui.ev('window.systemPulse.getMetrics()'); }
  check('température CPU forcée lue (52 °C)', m && m.temperatures.cpu === 52, JSON.stringify(m && m.temperatures));
  let shown = '';
  for (let i = 0; i < 10 && shown !== '52 °C'; i += 1) { await sleep(300); shown = await ui.ev(`document.getElementById('cpu-temp').textContent`); }
  check('affichée dans la carte', shown === '52 °C', shown);
  check('niveau CPU ok', (await rowLevel(ui, '.temperature-row--cpu')) === 'ok');
  check('carte au niveau ok', (await cardLevel(ui)) === 'ok');
  await ui.shot(path.join(shots, 'nat-cpu-temp-ok.png'));
} finally { await app.stop(); }

// 2. Seuil d'alerte (warn 75, crit 88) ------------------------------------------------------
app = launch(9840, { SYSTEMPULSE_CPU_TEMP_C: '80' });
try {
  const ui = await connect(9840);
  await sleep(3500);
  check('80 °C CPU : texte correct', (await ui.ev(`document.getElementById('cpu-temp').textContent`)) === '80 °C');
  check('80 °C CPU : niveau warn', (await rowLevel(ui, '.temperature-row--cpu')) === 'warn');
  check('la carte reflète le pire des deux (warn)', (await cardLevel(ui)) === 'warn');
  check('la ligne GPU reste indépendante (ok, pas de nvidia à 80 °C ici)', (await rowLevel(ui, '.temperature-row--gpu')) !== 'crit');
} finally { await app.stop(); }

app = launch(9840, { SYSTEMPULSE_CPU_TEMP_C: '95' });
try {
  const ui = await connect(9840);
  await sleep(3500);
  check('95 °C CPU : niveau critique', (await rowLevel(ui, '.temperature-row--cpu')) === 'crit');
  check('la carte reflète le critique', (await cardLevel(ui)) === 'crit');
  await ui.shot(path.join(shots, 'nat-cpu-temp-crit.png'));
} finally { await app.stop(); }

// 3. Carte mère qui n'expose rien (cas réel de ce PC) : N/D honnête, pas de valeur inventée --
app = launch(9840, { SYSTEMPULSE_CPU_TEMP_MODE: 'unsupported' });
try {
  const ui = await connect(9840);
  await sleep(3500);
  const m = await ui.ev('window.systemPulse.getMetrics()');
  check('température CPU absente (null)', m && m.temperatures.cpu === null, JSON.stringify(m && m.temperatures));
  check('affichage N/D', (await ui.ev(`document.getElementById('cpu-temp').textContent`)) === 'N/D');
  check('niveau ok (pas d\'alerte sur une valeur absente)', (await rowLevel(ui, '.temperature-row--cpu')) === 'ok');
  const note = await ui.ev(`document.getElementById('temperature-note').textContent`);
  check('note honnête (CPU non exposé mentionné)', note.includes('CPU') && note.includes('non exposé'), note);
} finally { await app.stop(); }

// 4. Chemin ACPI réel (sans la mémoire partagée de HWiNFO), sans valeur forcée : doit s'exécuter sans erreur,
// quel que soit le résultat (N/D sur une carte mère qui n'expose rien, un nombre plausible sinon) -------------
app = launch(9840, { SYSTEMPULSE_CPU_TEMP_MODE: 'no-hwinfo' });
try {
  const ui = await connect(9840);
  await sleep(3500);
  const text = await ui.ev(`document.getElementById('cpu-temp').textContent`);
  check('zones ACPI lues sans erreur (N/D ou un nombre plausible)', text === 'N/D' || /^\d{1,3} °C$/.test(text), text);
} finally { await app.stop(); }

// 5. Environnement réel, sans rien forcer : HWiNFO (si lancé à côté) n'est lisible qu'en administrateur, donc
// absent ici (l'appli de test tourne sans droits élevés, comme un lancement normal) ; repli sur les zones ACPI.
app = launch(9840, {});
try {
  const ui = await connect(9840);
  await sleep(3500);
  const text = await ui.ev(`document.getElementById('cpu-temp').textContent`);
  check('environnement réel : lecture cohérente (N/D ou un nombre plausible)', text === 'N/D' || /^\d{1,3} °C$/.test(text), text);
} finally { await app.stop(); }

done();
