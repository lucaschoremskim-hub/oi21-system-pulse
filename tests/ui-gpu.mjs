// Test du repli GPU (compteurs Windows « GPU Engine », sans nvidia-smi) : usage, nom de la carte, température N/D.
// Lancer : node tests/ui-gpu.mjs [dossier-des-captures]   (nécessite : dotnet build -c Test -p:Platform=x64 -o out-test/SystemPulse)
import path from 'node:path';
import { launch, connect, sleep, reporter } from './lib.mjs';

const shots = process.argv[2] || '.';
const { check, done } = reporter();
const app = launch(9830, { SYSTEMPULSE_GPU_MODE: 'counters' });
try {
  const ui = await connect(9830);
  let m = null;
  for (let i = 0; i < 30 && !(m && m.gpu && m.gpu.value !== null); i += 1) { await sleep(1000); m = await ui.ev('window.systemPulse.getMetrics()'); }
  check('usage GPU mesuré par les compteurs (nombre 0-100)', m && m.gpu && typeof m.gpu.value === 'number' && m.gpu.value >= 0 && m.gpu.value <= 100, JSON.stringify(m && m.gpu));
  check('nom de la carte lu dans le registre', !!(m && m.gpu && m.gpu.name), m && m.gpu && m.gpu.name);
  check('température non exposée (null, pas de valeur inventée)', m && m.gpu && m.gpu.temperature === null);
  await sleep(2500);
  check('pastille GPU en LIVE', (await ui.ev(`document.getElementById('gpu-chip').textContent`)) === 'LIVE');
  await ui.shot(path.join(shots, 'nat-gpu-compteurs.png'));
} finally { await app.stop(); }
done();
