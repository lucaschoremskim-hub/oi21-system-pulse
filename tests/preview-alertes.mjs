// Aperçu de design (VALEURS SIMULÉES, pas de vraies mesures) : montre les couleurs d'alerte. Lancer : node tests/preview-alertes.mjs <fichier.png>
import { launch, connect, sleep } from './lib.mjs';
const out = process.argv[2] || 'apercu-alertes.png';
const app = launch(9930);
try {
  const ui = await connect(9930);
  await sleep(3500);
  await ui.ev(`(() => {
    // Fige l'affichage (arrête les mises à jour reçues) puis force des valeurs pour l'aperçu.
    window.systemPulse.onMetrics = () => () => {};
    const set = (id, v) => { document.getElementById(id).textContent = v; };
    const card = (n, l) => { document.querySelector('[data-card=' + n + ']').dataset.level = l; };
    set('cpu-value', '94,2'); document.getElementById('cpu-bar').style.width = '94%'; card('cpu', 'crit');
    set('gpu-value', '78,0'); document.getElementById('gpu-bar').style.width = '78%'; card('gpu', 'warn');
    set('ram-value', '61,3'); document.getElementById('ram-bar').style.width = '61%'; card('ram', 'ok');
    set('gpu-temp', '83 °C'); card('temperatures', 'crit');
    const rows = document.querySelectorAll('#storage-drives .drive-row');
    if (rows[2]) { rows[2].dataset.level = 'crit'; rows[2].querySelector('.drive-percent').textContent = '96,4 %'; rows[2].querySelector('.progress-fill').style.width = '96%'; card('storage', 'crit'); }
    if (rows[1]) { rows[1].dataset.level = 'warn'; rows[1].querySelector('.drive-percent').textContent = '88,0 %'; rows[1].querySelector('.progress-fill').style.width = '88%'; }
  })()`);
  await ui.shot(out);
  ui.close();
} finally { await app.stop(); }
