const view = new URLSearchParams(window.location.search).get('view') || 'main';
const isOverlay = view === 'overlay';
const api = window.systemPulse;

const $ = (id) => document.getElementById(id);

const history = {
  cpu: [],
  gpu: [],
  ram: []
};

let feedbackTimer = null;
let lastMetricsTimestamp = 0;
let visibility = { cards: {}, overlay: {}, drives: {} };
let lastDrives = [];
let driveChecksSignature = '';

function el(tag, className, text) {
  const node = document.createElement(tag);
  if (className) node.className = className;
  if (text !== undefined) node.textContent = text;
  return node;
}

// Un choix absent est considéré comme « affiché ».
function isShown(group, key) {
  return visibility[group]?.[key] !== false;
}

function visibleDrives() {
  return lastDrives.filter((drive) => isShown('drives', drive.mount));
}

// Niveau d'alerte d'une valeur : 'ok', 'warn' (orange) ou 'crit' (rouge).
function levelFor(value, warn, crit) {
  const number = Number(value);
  if (value === null || value === undefined || !Number.isFinite(number)) return 'ok';
  return number >= crit ? 'crit' : number >= warn ? 'warn' : 'ok';
}

function setLevel(cardName, level) {
  const card = document.querySelector(`[data-card="${cardName}"]`);
  if (card && card.dataset.level !== level) card.dataset.level = level;
}

function setRowLevel(selector, level) {
  const row = document.querySelector(selector);
  if (row && row.dataset.level !== level) row.dataset.level = level;
}

const levelRank = { ok: 0, warn: 1, crit: 2 };
function worstLevel(a, b) {
  return levelRank[a] >= levelRank[b] ? a : b;
}

function renderStorageCard() {
  const box = $('storage-drives');
  if (!box) return;
  const drives = visibleDrives();
  box.replaceChildren();
  let worst = 'ok';
  if (!drives.length) {
    box.append(el('div', 'drive-empty', lastDrives.length ? 'Aucun disque sélectionné.' : 'Aucun disque détecté.'));
    setLevel('storage', 'ok');
    return;
  }
  for (const drive of drives) {
    const row = el('div', 'drive-row');
    const level = levelFor(drive.value, 85, 95);
    row.dataset.level = level;
    if (level === 'crit' || (level === 'warn' && worst === 'ok')) worst = level;
    const track = el('div', 'progress-track');
    const fill = el('span', 'progress-fill');
    fill.style.width = drive.value === null || drive.value === undefined ? '0%' : `${clamp(drive.value, 0, 100)}%`;
    track.append(fill);
    const detail = `${formatBytes(drive.used)} / ${formatBytes(drive.total)} · ${formatBytes(drive.available)} libre`;
    row.title = `${drive.mount} ${detail}`;
    row.append(el('span', 'drive-name', drive.mount), track, el('span', 'drive-percent', `${formatPercent(drive.value)} %`), el('div', 'drive-detail', detail));
    box.append(row);
  }
  setLevel('storage', worst);
}

function renderOverlayStorage() {
  const box = $('overlay-storage-lines');
  if (!box) return;
  box.replaceChildren();
  for (const drive of visibleDrives()) {
    const line = el('div', 'overlay-line');
    line.append(el('span', 'overlay-label', `DISK ${drive.mount}`), el('strong', 'overlay-value overlay-value--pink', formatPercent(drive.value)), el('span', 'overlay-unit', '%'));
    box.append(line);
  }
}

// Les cases « Disques » du panneau suivent les disques détectés (reconstruites seulement si la liste change).
function renderDriveChecks() {
  const box = $('drive-checks');
  if (!box) return;
  const signature = lastDrives.map((drive) => drive.mount).join(',');
  if (signature === driveChecksSignature) return;
  driveChecksSignature = signature;
  box.replaceChildren();
  if (!lastDrives.length) {
    box.append(el('span', 'display-note', 'Aucun disque détecté.'));
    return;
  }
  for (const drive of lastDrives) {
    const label = el('label', 'check-item');
    const input = document.createElement('input');
    input.type = 'checkbox';
    input.dataset.group = 'drives';
    input.dataset.key = drive.mount;
    input.checked = isShown('drives', drive.mount);
    label.append(input, document.createTextNode(' '), el('span', '', `${drive.mount} — ${formatBytes(drive.total)}`));
    box.append(label);
  }
}

// Garantit que toutes les cartes tiennent dans la fenêtre : si le contenu dépasse, la grille est réduite (zoom)
// juste ce qu'il faut, sans jamais descendre sous 65 %. La disposition normale n'est jamais réduite.
let fitPending = false;
let lastFitSignature = '';

function fitGrid() {
  const grid = document.querySelector('.metrics-grid');
  const content = document.querySelector('.dashboard-content');
  if (isOverlay || !grid || !content || grid.offsetParent === null) return;
  const signature = [innerWidth, innerHeight, document.querySelectorAll('[data-card]:not([hidden])').length, visibleDrives().length, $('fps-note')?.textContent, $('fps-admin')?.hidden].join('|');
  if (signature === lastFitSignature) return;
  lastFitSignature = signature;
  grid.style.zoom = '1';
  for (let i = 0; i < 4; i += 1) {
    const overflow = content.scrollHeight - content.clientHeight;
    if (overflow <= 1) break;
    const current = Number(grid.style.zoom) || 1;
    const gridHeight = grid.getBoundingClientRect().height;
    if (gridHeight <= 0) break;
    const next = Math.max(0.65, current * ((gridHeight - overflow) / gridHeight));
    if (next >= current - 0.004) break;
    grid.style.zoom = String(Math.round(next * 1000) / 1000);
  }
  if (grid.style.zoom === '1') grid.style.removeProperty('zoom');
}

function scheduleFit() {
  if (fitPending || isOverlay) return;
  fitPending = true;
  requestAnimationFrame(() => { fitPending = false; fitGrid(); });
}

function applyVisibility() {
  document.querySelectorAll('[data-card]').forEach((card) => {
    card.hidden = !isShown('cards', card.dataset.card);
  });
  const anyCard = [...document.querySelectorAll('[data-card]')].some((card) => !card.hidden);
  const empty = $('empty-cards');
  if (empty) empty.hidden = anyCard;
  document.querySelectorAll('[data-overlay-line]').forEach((line) => {
    line.hidden = !isShown('overlay', line.dataset.overlayLine);
  });
  renderDriveChecks();
  document.querySelectorAll('input[data-group][data-key]').forEach((input) => {
    input.checked = isShown(input.dataset.group, input.dataset.key);
  });
  renderStorageCard();
  renderOverlayStorage();
  scheduleFit();
}

function collectVisibility() {
  const next = { cards: {}, overlay: {}, drives: {} };
  document.querySelectorAll('input[data-group][data-key]').forEach((input) => {
    if (next[input.dataset.group]) next[input.dataset.group][input.dataset.key] = input.checked;
  });
  return next;
}

async function saveVisibility() {
  try {
    visibility = await api.setVisibility(collectVisibility());
    applyVisibility();
  } catch (_error) {
    showFeedback('Le choix d’affichage n’a pas pu être enregistré.');
  }
}

function clamp(value, min, max) {
  return Math.min(Math.max(Number(value) || 0, min), max);
}

function formatPercent(value) {
  return value === null || value === undefined || !Number.isFinite(Number(value)) ? '—' : Number(value).toFixed(1).replace('.', ',');
}

function formatTemperature(value) {
  return value === null || value === undefined || !Number.isFinite(Number(value)) ? 'N/D' : `${Math.round(Number(value))} °C`;
}

function formatBytes(bytes, decimals = 1) {
  if (bytes === null || bytes === undefined || !Number.isFinite(Number(bytes))) return '—';
  const value = Number(bytes);
  if (value < 1024) return `${Math.round(value)} B`;
  const units = ['KB', 'MB', 'GB', 'TB'];
  let unitIndex = -1;
  let scaled = value;
  while (scaled >= 1024 && unitIndex < units.length - 1) {
    scaled /= 1024;
    unitIndex += 1;
  }
  const precision = scaled >= 100 || decimals === 0 ? 0 : decimals;
  return `${scaled.toFixed(precision).replace('.', ',')} ${units[unitIndex]}`;
}

function formatRate(bytesPerSecond) {
  if (bytesPerSecond === null || bytesPerSecond === undefined || !Number.isFinite(Number(bytesPerSecond))) return '—';
  const value = Number(bytesPerSecond);
  if (value < 1024) return `${Math.round(value)} B/s`;
  if (value < 1024 * 1024) return `${(value / 1024).toFixed(value < 10240 ? 1 : 0).replace('.', ',')} KB/s`;
  if (value < 1024 * 1024 * 1024) return `${(value / (1024 * 1024)).toFixed(value < 10485760 ? 1 : 0).replace('.', ',')} MB/s`;
  return `${(value / (1024 * 1024 * 1024)).toFixed(1).replace('.', ',')} GB/s`;
}

function formatTime(dateValue) {
  const date = new Date(dateValue);
  return Number.isNaN(date.getTime()) ? '—' : date.toLocaleTimeString('fr-FR', { hour: '2-digit', minute: '2-digit', second: '2-digit' });
}

function setText(id, value) {
  const element = $(id);
  if (element) element.textContent = value;
}

function showFeedback(message, duration = 3000) {
  setText('global-status', message);
  if (feedbackTimer) clearTimeout(feedbackTimer);
  feedbackTimer = setTimeout(() => {
    feedbackTimer = null;
  }, duration);
}

function setBar(id, value) {
  const element = $(id);
  if (!element) return;
  const valid = value !== null && value !== undefined && Number.isFinite(Number(value));
  element.style.width = valid ? `${clamp(value, 0, 100)}%` : '0%';
}

function appendHistory(key, value) {
  if (value === null || value === undefined || !Number.isFinite(Number(value))) return;
  const values = history[key];
  values.push(clamp(value, 0, 100));
  if (values.length > 24) values.shift();
}

function updateSparkline(id, key) {
  const element = $(id);
  const values = history[key];
  if (!element || !values.length) return;
  const width = 240;
  const height = 42;
  const pad = 3;
  const points = values.map((value, index) => {
    const x = values.length === 1 ? width : (index / (values.length - 1)) * width;
    const y = height - pad - (clamp(value, 0, 100) / 100) * (height - pad * 2);
    return `${x.toFixed(1)},${y.toFixed(1)}`;
  });
  element.setAttribute('points', points.join(' '));
}

const FPS_NOTES = {
  off: 'Mesure désactivée.',
  starting: 'Démarrage de la mesure…',
  waiting: 'En attente d’une application qui affiche des images (lancez un jeu).',
  'no-rights': 'Requiert les droits administrateur.',
  missing: 'PresentMon est introuvable dans le dossier de l’application.'
};

function updateFps(fps) {
  const status = fps?.status || 'off';
  const hasValue = status === 'ok' && fps.value !== null && fps.value !== undefined && Number.isFinite(Number(fps.value));
  const value = $('fps-value');
  if (value) {
    value.textContent = hasValue ? String(Math.round(fps.value)) : 'N/D';
    value.classList.toggle('metric-value--muted', !hasValue);
  }
  const chip = $('fps-chip');
  if (chip) chip.textContent = hasValue ? 'LIVE' : 'N/D';
  let note = FPS_NOTES[status] || '';
  if (status === 'ok') note = `Application suivie : ${fps.app}`;
  if (status === 'error') note = fps?.detail || 'La mesure des FPS a échoué.';
  setText('fps-note', note);
  const admin = $('fps-admin');
  if (admin) admin.hidden = status !== 'no-rights';
  setText('overlay-fps', hasValue ? String(Math.round(fps.value)) : '—');
  scheduleFit();
}

function updateDrives(storage) {
  lastDrives = Array.isArray(storage?.drives) ? storage.drives : [];
  renderDriveChecks();
  renderStorageCard();
  renderOverlayStorage();
  scheduleFit();
}

function updateMain(metrics) {
  if (!metrics || metrics.error) {
    setText('global-status', 'Lecture des capteurs indisponible');
    setText('temperature-note', metrics?.error || 'Réessayez dans quelques secondes.');
    return;
  }

  const cpu = metrics.cpu || {};
  const gpu = metrics.gpu || {};
  const ram = metrics.ram || {};
  const temperatures = metrics.temperatures || {};
  const network = metrics.network || {};
  const storage = metrics.storage || {};

  setText('global-status', 'Capteurs connectés · données locales');
  setText('last-update', formatTime(metrics.timestamp));
  setLevel('cpu', levelFor(cpu.value, 75, 90));
  setLevel('gpu', levelFor(gpu.value, 75, 90));
  setLevel('ram', levelFor(ram.value, 80, 92));
  const cpuTempLevel = levelFor(temperatures.cpu, 75, 88);
  const gpuTempLevel = levelFor(temperatures.gpu, 72, 82);
  setRowLevel('.temperature-row--cpu', cpuTempLevel);
  setRowLevel('.temperature-row--gpu', gpuTempLevel);
  setLevel('temperatures', worstLevel(cpuTempLevel, gpuTempLevel));
  setText('cpu-value', formatPercent(cpu.value));
  setText('cpu-detail', cpu.user === null || cpu.user === undefined ? 'Charge globale' : `${formatPercent(cpu.user)} % utilisateur`);
  setText('cpu-cores', cpu.cores ? `${cpu.cores} cœurs` : '— cœurs');
  setBar('cpu-bar', cpu.value);
  appendHistory('cpu', cpu.value);
  updateSparkline('cpu-sparkline', 'cpu');

  setText('gpu-value', formatPercent(gpu.value));
  setText('gpu-detail', gpu.name ? gpu.name : (gpu.value === null || gpu.value === undefined ? 'Capteur non exposé' : 'Charge graphique'));
  setText('gpu-temp-inline', formatTemperature(gpu.temperature));
  setText('gpu-chip', gpu.value === null || gpu.value === undefined ? 'N/D' : 'LIVE');
  setBar('gpu-bar', gpu.value);
  appendHistory('gpu', gpu.value);
  updateSparkline('gpu-sparkline', 'gpu');

  setText('ram-value', formatPercent(ram.value));
  setText('ram-detail', `${formatBytes(ram.used)} / ${formatBytes(ram.total)}`);
  setText('ram-available', `${formatBytes(ram.available)} libre`);
  setBar('ram-bar', ram.value);
  appendHistory('ram', ram.value);
  updateSparkline('ram-sparkline', 'ram');

  setText('cpu-temp', formatTemperature(temperatures.cpu));
  setText('gpu-temp', formatTemperature(temperatures.gpu));
  const hasCpuTemperature = temperatures.cpu !== null && temperatures.cpu !== undefined && Number.isFinite(Number(temperatures.cpu));
  const hasGpuTemperature = temperatures.gpu !== null && temperatures.gpu !== undefined && Number.isFinite(Number(temperatures.gpu));
  if (hasCpuTemperature && hasGpuTemperature) setText('temperature-note', 'Mesurées par la carte mère et le pilote graphique.');
  else if (hasCpuTemperature) setText('temperature-note', 'CPU mesuré par la carte mère · GPU non exposé par Windows ou le pilote.');
  else if (hasGpuTemperature) setText('temperature-note', 'GPU mesuré par le pilote graphique · CPU non exposé par cette carte mère.');
  else setText('temperature-note', 'Non exposées par cette carte mère ni par le pilote graphique.');

  setText('network-download', formatRate(network.download));
  setText('network-upload', formatRate(network.upload));
  setText('network-interface', network.interface ? `Interface ${network.interface}` : 'Interface active détectée');

  updateDrives(storage);

  updateFps(metrics.fps);
  setText('machine-name', metrics.system?.hostname || 'Machine locale');
}

function updateOverlay(metrics) {
  if (!metrics || metrics.error) return;
  const cpu = metrics.cpu || {};
  const gpu = metrics.gpu || {};
  const ram = metrics.ram || {};
  const temperatures = metrics.temperatures || {};
  const network = metrics.network || {};
  const storage = metrics.storage || {};

  setText('overlay-cpu', formatPercent(cpu.value));
  setText('overlay-gpu', formatPercent(gpu.value));
  setText('overlay-ram', formatPercent(ram.value));
  setText('overlay-temp', formatTemperature(temperatures.gpu));
  setText('overlay-network-down', formatRate(network.download));
  setText('overlay-network-up', `↑ ${formatRate(network.upload)}`);
  updateDrives(storage);
  updateFps(metrics.fps);
  setText('overlay-update', formatTime(metrics.timestamp));
}

function applyState(state) {
  if (!state) return;
  if (state.visibility) {
    visibility = state.visibility;
    applyVisibility();
  }
  if (!isOverlay) {
    const link = $('update-link');
    if (link && state.update) {
      link.textContent = 'Version ' + state.update.version + ' disponible';
      link.title = state.update.notes || '';
      link.hidden = false;
      $('update-sep').hidden = false;
      if (!link.dataset.bound) {
        link.dataset.bound = '1';
        link.addEventListener('click', (event) => { event.preventDefault(); api.openUpdate(); });
      }
    }
  }
  if (isOverlay) document.body.classList.toggle('overlay-movable', Boolean(state.overlayMovable));
  if (!isOverlay) {
    const moveButton = $('overlay-move');
    if (moveButton) {
      moveButton.disabled = !state.overlayVisible;
      moveButton.setAttribute('aria-pressed', String(Boolean(state.overlayMovable)));
      setText('overlay-move-label', state.overlayMovable ? 'Verrouiller l’overlay' : 'Déplacer l’overlay');
    }
    const toggle = $('overlay-toggle');
    if (toggle) toggle.checked = Boolean(state.overlayVisible);
    setText('overlay-status', state.overlayVisible ? 'Actif au premier plan' : 'Désactivé');
    const intervalSelect = $('interval-select');
    if (intervalSelect) {
      intervalSelect.value = String(state.intervalMs);
      intervalSelect.dataset.appliedInterval = String(state.intervalMs);
    }
  }
}

function initMain() {
  document.body.classList.add('main-mode');
  window.addEventListener('resize', scheduleFit);
  $('overlay-toggle')?.addEventListener('change', async (event) => {
    const toggle = event.target;
    const requestedVisibility = toggle.checked;
    toggle.disabled = true;
    try {
      const visible = await api.setOverlayVisible(requestedVisibility);
      toggle.checked = visible;
      setText('overlay-status', visible ? 'Actif au premier plan' : 'Désactivé');
    } catch (_error) {
      toggle.checked = !requestedVisibility;
      setText('overlay-status', 'Impossible de modifier l’overlay');
      showFeedback('L’overlay n’a pas pu être modifié.');
    } finally {
      toggle.disabled = false;
    }
  });
  $('interval-select')?.addEventListener('change', async (event) => {
    const select = event.target;
    const previous = select.dataset.appliedInterval || select.value;
    select.disabled = true;
    try {
      const appliedInterval = await api.setInterval(select.value);
      select.value = String(appliedInterval);
      select.dataset.appliedInterval = String(appliedInterval);
    } catch (_error) {
      select.value = previous;
      showFeedback('La fréquence d’actualisation n’a pas pu être modifiée.');
    } finally {
      select.disabled = false;
    }
  });
  const setPanelOpen = (open) => {
    const panel = $('display-panel');
    if (!panel) return;
    panel.hidden = !open;
    document.body.classList.toggle('panel-open', open);
    scheduleFit();
    $('display-toggle')?.setAttribute('aria-expanded', String(open));
    if (!open) $('display-toggle')?.focus();
  };
  $('display-toggle')?.addEventListener('click', () => setPanelOpen($('display-panel')?.hidden !== false));
  $('display-close')?.addEventListener('click', () => setPanelOpen(false));
  $('overlay-move')?.addEventListener('click', async (event) => {
    const button = event.currentTarget;
    try {
      await api.setOverlayMovable(button.getAttribute('aria-pressed') !== 'true');
    } catch (_error) {
      showFeedback('Le mode déplacement de l’overlay n’a pas pu être modifié.');
    }
  });
  $('fps-admin')?.addEventListener('click', async (event) => {
    const button = event.currentTarget;
    button.disabled = true;
    button.textContent = 'Confirmez dans la fenêtre Windows…';
    try {
      await api.relaunchAsAdmin();
    } catch (_error) {
      button.disabled = false;
      button.textContent = 'Relancer en administrateur';
      showFeedback('Le relancement en administrateur a échoué.');
    }
  });
  document.addEventListener('keydown', (event) => {
    if (event.key === 'Escape' && $('display-panel') && !$('display-panel').hidden) setPanelOpen(false);
  });
  $('display-panel')?.addEventListener('change', (event) => {
    if (event.target instanceof HTMLInputElement && event.target.dataset.group) void saveVisibility();
  });
  $('display-panel')?.addEventListener('click', (event) => {
    const button = event.target instanceof Element ? event.target.closest('[data-select-all], [data-select-none]') : null;
    if (!button) return;
    const checked = button.hasAttribute('data-select-all');
    const group = button.dataset.selectAll || button.dataset.selectNone;
    document.querySelectorAll(`input[data-group="${group}"]`).forEach((input) => { input.checked = checked; });
    void saveVisibility();
  });
  document.querySelectorAll('[data-window-action]').forEach((button) => {
    button.addEventListener('click', () => api.windowAction(button.dataset.windowAction));
  });
}

function initOverlay() {
  document.body.classList.add('overlay-mode');
  $('main-view').hidden = true;
  $('overlay-view').hidden = false;

  // La fenêtre s'ajuste à son contenu (8 px de marge de chaque côté).
  const readout = document.querySelector('.overlay-readout');
  if (readout) {
    let lastSize = '';
    const fit = () => {
      const width = readout.offsetWidth + 16;
      const height = readout.offsetHeight + 16;
      const size = `${width}x${height}`;
      if (size === lastSize) return;
      lastSize = size;
      api.fitOverlay(width, height);
    };
    new ResizeObserver(fit).observe(readout);
    fit();

    // Glisser pour déplacer (mode « déplacer » uniquement). Le capteur de pointeur suit la souris hors de la fenêtre.
    let dragging = false;
    let last = null;
    readout.addEventListener('pointerdown', (event) => {
      if (!document.body.classList.contains('overlay-movable') || event.button !== 0) return;
      dragging = true;
      last = { x: event.screenX, y: event.screenY };
      readout.setPointerCapture(event.pointerId);
      event.preventDefault();
    });
    readout.addEventListener('pointermove', (event) => {
      if (!dragging) return;
      const dx = event.screenX - last.x;
      const dy = event.screenY - last.y;
      if (dx === 0 && dy === 0) return;
      last = { x: event.screenX, y: event.screenY };
      api.moveOverlay(dx, dy);
    });
    const stopDragging = (event) => {
      if (!dragging) return;
      dragging = false;
      try { readout.releasePointerCapture(event.pointerId); } catch (_error) { /* déjà libéré */ }
      api.endOverlayMove();
    };
    readout.addEventListener('pointerup', stopDragging);
    readout.addEventListener('pointercancel', stopDragging);
  }
}

if (isOverlay) initOverlay();
else initMain();

function acceptMetrics(metrics) {
  const timestamp = Number(metrics?.timestamp) || 0;
  if (timestamp < lastMetricsTimestamp) return;
  lastMetricsTimestamp = timestamp;
  if (isOverlay) updateOverlay(metrics);
  else updateMain(metrics);
}

api.onMetrics(acceptMetrics);
api.onState(applyState);
api.getState().then(applyState).catch(() => {});
api.getMetrics().then(acceptMetrics).catch(() => {});
