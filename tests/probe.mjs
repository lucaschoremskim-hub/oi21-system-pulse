import path from 'node:path';
import { launch, connect, sleep, reporter } from './lib.mjs';
const shots = process.argv[2] || '.';
const { check, done } = reporter();
const app = launch(9801);
try {
  const ui = await connect(9801);
  await sleep(5000);
  check('pont hôte chargé', (await ui.ev(`typeof window.systemPulse.getState`)) === 'function');
  check('classe native-host', (await ui.ev(`document.documentElement.classList.contains('native-host')`)) === true);
  check('barre de titre HTML masquée', (await ui.ev(`getComputedStyle(document.querySelector('.titlebar')).display`)) === 'none');
  const vals = await ui.ev(`({cpu: document.getElementById('cpu-value').textContent, ram: document.getElementById('ram-value').textContent, gpu: document.getElementById('gpu-detail').textContent, drives: [...document.querySelectorAll('#storage-drives .drive-name')].map(e=>e.textContent).join(','), status: document.getElementById('global-status').textContent, size: innerWidth+'x'+innerHeight})`);
  console.log(JSON.stringify(vals));
  check('CPU affiché', vals.cpu !== '—');
  check('RAM affichée', vals.ram !== '—');
  check('disques A: C: D:', vals.drives === 'A:,C:,D:', `(${vals.drives})`);
  const st = await ui.ev(`window.systemPulse.getState().then(s => JSON.stringify({v:s.version, i:s.intervalMs, f:s.visibility.cards.fps}))`);
  console.log('état:', st);
  await ui.shot(path.join(shots, 'native-principale.png'));
  ui.close();
} finally { await app.stop(); }
done();
