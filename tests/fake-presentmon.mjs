// Faux PresentMon pour les tests : émet un CSV au format v1 sur stdout. Aucun événement Windows n'est lu.
const mode = process.env.FAKE_MODE || 'game';
const header = 'Application,ProcessID,SwapChainAddress,Runtime,SyncInterval,PresentFlags,AllowsTearing,PresentMode,Dropped,TimeInSeconds,msInPresentAPI,msBetweenPresents';

if (mode === 'no-rights') {
  // Message réel observé avec PresentMon 2.6.0 sans droits (code de sortie 6).
  process.stderr.write("warning: PresentMon requires elevated privilege in order to query processes that are\n         short-running or started on another account.\nerror: failed to start trace session: access denied.\n       PresentMon requires either administrative privileges or to be run by a user in the\n       \"Performance Log Users\" user group.  View the readme for more details.\n");
  process.exit(6);
}
if (mode === 'crash') {
  process.stderr.write('fatal: something unrelated broke\n');
  setTimeout(() => process.exit(3), 6500);
  setInterval(() => {}, 1000);
}
setInterval(() => {}, 1000); // comme le vrai PresentMon, reste actif jusqu'à ce qu'on l'arrête
if (mode === 'bad-format') {
  for (let i = 0; i < 8; i += 1) process.stdout.write(`Foo,Bar\n${i},2\n`);
} else {
  process.stdout.write(header + '\r\n');
  let t = 0;
  const emit = (app, pid, ms, dropped = 0) => {
    process.stdout.write(`${app},${pid},0x1,DXGI,0,0,0,Hardware: Independent Flip,${dropped},${t.toFixed(6)},1.0,${ms}\r\n`);
  };
  // 120 FPS pour le jeu, 60 pour un navigateur, 300 pour dwm.exe (ignoré), quelques images abandonnées.
  const timer = setInterval(() => {
    for (let i = 0; i < 12; i += 1) emit('Game.exe', 4242, 8.333);
    for (let i = 0; i < 6; i += 1) emit('brave.exe', 1111, 16.667);
    for (let i = 0; i < 30; i += 1) emit('dwm.exe', 900, 3.333);
    emit('Game.exe', 4242, 500, 1);
    t += 0.1;
  }, 100);
  if (mode === 'game-then-stop') setTimeout(() => { clearInterval(timer); }, 700);
}
process.on('SIGTERM', () => process.exit(0));
