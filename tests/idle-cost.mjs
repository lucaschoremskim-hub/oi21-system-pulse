// Coût au repos (CPU et mémoire) selon les arguments du moteur : lance l'application 20 s et mesure. Lancer : node tests/idle-cost.mjs
import { execFileSync } from 'node:child_process';
import { launch, sleep } from './lib.mjs';

const ps = (script) => execFileSync('powershell.exe', ['-NoProfile', '-Command', script]).toString().trim();
const sample = (dir) => JSON.parse(ps(`
$dir = '${dir.replace(/\\/g, '\\\\')}'
$all = Get-CimInstance Win32_Process | Where-Object { ($_.Name -eq 'SystemPulse.exe') -or ($_.Name -eq 'msedgewebview2.exe' -and $_.CommandLine -like '*' + $dir.Replace('\\\\','\\') + '*') }
$cpu = 0; $mem = 0
foreach ($p in $all) { $pr = Get-Process -Id $p.ProcessId -ErrorAction SilentlyContinue; if ($pr) { $cpu += $pr.TotalProcessorTime.TotalSeconds; $mem += $pr.WorkingSet64 } }
@{ cpu = $cpu; memMo = [math]::Round($mem / 1MB); n = @($all).Count } | ConvertTo-Json -Compress`));

async function measure(port, args) {
  const app = launch(port, { SYSTEMPULSE_WV2_ARGS: args });
  try {
    await sleep(9000);
    const a = sample(app.dataDir);
    const t0 = Date.now();
    await sleep(20000);
    const b = sample(app.dataDir);
    const secs = (Date.now() - t0) / 1000;
    return { cpuPct: ((b.cpu - a.cpu) / secs) * 100, memMo: b.memMo, n: b.n };
  } finally { await app.stop(); }
}
const variants = { 'SmartScreen coupé (GPU actif)': '--disable-features=msSmartScreenProtection', 'SmartScreen coupé + GPU coupé': '--disable-features=msSmartScreenProtection --disable-gpu' };
let port = 9900;
for (const [name, args] of Object.entries(variants)) {
  const r = await measure(port++, args);
  console.log(`${name.padEnd(34)} CPU au repos : ${r.cpuPct.toFixed(2)} % d'un coeur ; mémoire : ${r.memMo} Mo ; ${r.n} processus`);
}
