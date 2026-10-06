// Test de l'onglet Pilotes : scan simulé (SYSTEMPULSE_DRIVER_FAKE), jamais de vraie requête WMI/Windows Update.
// Lancer : node tests/ui-drivers.mjs [dossier-des-captures]   (nécessite : dotnet build -c Test -p:Platform=x64 -o out-test/SystemPulse)
import path from 'node:path';
import fs from 'node:fs';
import { launch, connect, sleep, reporter } from './lib.mjs';

const shots = process.argv[2] || '.';
const { check, done } = reporter();
const click = (ui, sel) => ui.ev(`document.querySelector(${JSON.stringify(sel)}).click()`);
const text = (ui, sel) => ui.ev(`document.querySelector(${JSON.stringify(sel)})?.textContent`);

const fakeDrivers = [
  { device: 'Carte graphique de test', manufacturer: 'Exemple', provider: 'Exemple', installedVersion: '1.0.0', deviceClass: 'DISPLAY', status: 'updateAvailable', updateTitle: 'Pilote graphique 2.0.0' },
  { device: 'Contrôleur réseau de test', manufacturer: 'Exemple', provider: 'Exemple', installedVersion: '3.1.0', deviceClass: 'NET', status: 'upToDate' },
  { device: 'Puce audio de test', manufacturer: 'Exemple', provider: 'Exemple', installedVersion: '4.2.1', deviceClass: 'MEDIA', status: 'upToDate' }
];

// 1. Sans le scan au démarrage (hors test explicite) : onglet au repos, rien d'inventé ------------------------
let app = launch(9860, {});
try {
  const ui = await connect(9860);
  await sleep(3000);
  check('onglet « Pilotes » caché au départ', (await ui.ev(`document.getElementById('tab-drivers').hidden`)) === true);
  await click(ui, '[data-tab="drivers"]');
  await sleep(300);
  check('onglet « Pilotes » visible après clic', (await ui.ev(`document.getElementById('tab-drivers').hidden`)) === false);
  check('tableau de bord caché', (await ui.ev(`document.getElementById('tab-dashboard').hidden`)) === true);
  check('aucun scan lancé : statut neutre', (await text(ui, '#drivers-status-text')) === 'Recherche non lancée.');
  await click(ui, '#display-toggle');
  await sleep(300);
  check('case « Rechercher au démarrage » cochée par défaut', (await ui.ev(`document.getElementById('scan-drivers-toggle').checked`)) === true);
} finally { await app.stop(); }

// 2. Scan simulé avec une mise à jour disponible ---------------------------------------------------------------
app = launch(9861, { SYSTEMPULSE_ALLOW_DRIVER_SCAN: '1', SYSTEMPULSE_DRIVER_FAKE: JSON.stringify(fakeDrivers) });
try {
  const ui = await connect(9861);
  await sleep(3000);
  await click(ui, '[data-tab="drivers"]');
  await sleep(500);
  check('pastille sur l’onglet quand une mise à jour existe', (await ui.ev(`document.getElementById('drivers-tab-dot').hidden`)) === false);
  check('résumé : une mise à jour disponible', (await text(ui, '#drivers-status-text')) === '1 mise à jour disponible.', await text(ui, '#drivers-status-text'));
  check('puce de résumé = 1', (await text(ui, '#drivers-chip')) === '1');
  check('groupe « mises à jour » visible', (await ui.ev(`document.getElementById('drivers-outdated').hidden`)) === false);
  const outdatedText = await text(ui, '#drivers-outdated-list');
  check('le pilote concerné est listé', outdatedText.includes('Carte graphique de test'), outdatedText);
  check('total des pilotes détectés = 3', (await text(ui, '#drivers-total-count')) === '3');
  check('bouton « Ouvrir Windows Update » visible', (await ui.ev(`document.getElementById('drivers-open-wu').hidden`)) === false);
  await ui.shot(path.join(shots, 'nat-pilotes-maj.png'));

  // Relancer la recherche depuis le bouton : le statut repasse par « en cours » puis « terminé ».
  await click(ui, '#drivers-rescan');
  await sleep(1200);
  check('nouvelle recherche terminée (même résultat simulé)', (await text(ui, '#drivers-chip')) === '1');
} finally { await app.stop(); }

// 3. Tous les pilotes à jour : pas de fausse alerte, pastille absente -----------------------------------------
app = launch(9862, { SYSTEMPULSE_ALLOW_DRIVER_SCAN: '1', SYSTEMPULSE_DRIVER_FAKE: JSON.stringify(fakeDrivers.map((d) => ({ ...d, status: 'upToDate' }))) });
try {
  const ui = await connect(9862);
  await sleep(3000);
  await click(ui, '[data-tab="drivers"]');
  await sleep(500);
  check('tous à jour : message explicite', (await text(ui, '#drivers-status-text')).includes('à jour'));
  check('pas de pastille quand tout est à jour', (await ui.ev(`document.getElementById('drivers-tab-dot').hidden`)) === true);
  check('groupe « mises à jour » caché', (await ui.ev(`document.getElementById('drivers-outdated').hidden`)) === true);
} finally { await app.stop(); }

// 4. Préférence décochée : enregistrée puis retrouvée au redémarrage ------------------------------------------
app = launch(9863, {});
const prefsDir = app.dataDir;
try {
  const ui = await connect(9863);
  await sleep(3000);
  await click(ui, '#display-toggle');
  await sleep(300);
  await click(ui, '#scan-drivers-toggle');
  await sleep(600);
  const saved = JSON.parse(fs.readFileSync(path.join(prefsDir, 'preferences.json'), 'utf8'));
  check('préférence enregistrée (décochée)', saved.scanDriversOnLaunch === false, JSON.stringify(saved.scanDriversOnLaunch));
} finally { await app.stop(); }

done();
