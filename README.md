# System Pulse

Moniteur Windows léger : CPU, GPU (toutes marques), RAM, température GPU (NVIDIA), réseau, tous les disques et FPS réels, avec un overlay déplaçable à poser par-dessus les jeux.

**Téléchargement : https://system-pulse-alpha.vercel.app** (zip de 0,7 Mo, rien à installer, ouverture en ~0,5 s).

Tout reste local : les mesures ne quittent jamais le PC. La seule requête réseau est la lecture de [`site/version.json`](site/version.json) au démarrage, pour signaler une nouvelle version.

## Fonctionnement

- **Hôte C#** (`src/`, .NET Framework 4.8, déjà présent sur Windows 10/11) : mesures, overlay, PresentMon, préférences.
- **Interface** (`ui/`) : HTML/CSS/JS affichés par **WebView2** (moteur d'Edge, déjà présent sur le PC). `host-bridge.js` relie l'interface à l'hôte (`window.systemPulse`).
- **Overlay** : fenêtre transparente dessinée en GDI+ (`OverlayForm.cs`), clic traversant quand elle est verrouillée.
- **Moteur** (`Program.cs`) : `--disable-features=msSmartScreenProtection --disable-gpu` (ouverture 2,5 s → 0,5 s ; CPU au repos 30 % → 8 % d'un coeur, mesuré).
- **Préférences** : `%APPDATA%\SystemPulse\preferences.json`. Journal de démarrage : `journal.txt` au même endroit.

Pourquoi .NET Framework 4.8 et pas .NET 8 : une appli WinForms .NET 8 autonome pèse ~66 Mo, contre 0,7 Mo ici, et la version « dépendante du runtime » obligerait à installer .NET 8. Le projet utilise tout de même le format moderne (`dotnet build`, C# récent).

## Développer

Prérequis : [SDK .NET 8](https://dotnet.microsoft.com/download) (il sait compiler la cible .NET Framework 4.8).

```powershell
dotnet build -c Release -p:Platform=x64         # bin\x64\Release\net48
powershell -File tools\package.ps1             # dist\SystemPulse.zip + .sha256
```

## Tests

Les tests pilotent l'interface par le protocole de débogage de WebView2 (version `Test`, jamais distribuée) et n'utilisent jamais le vrai PresentMon.

```powershell
dotnet build -c Test -p:Platform=x64 -o out-test\SystemPulse
node tests\ui-native.mjs  <dossier-captures>   # fenêtre principale (27 vérifications)
node tests\ui-overlay.mjs <dossier-captures>   # overlay réel (16)
node tests\ui-update.mjs  <dossier-captures>   # vérification de mise à jour (5)
node testsui-gpu.mjs     <dossier-captures>   # repli GPU par compteurs Windows (4)
```

## Publier une version

1. Changer `<Version>` dans `SystemPulse.csproj`, commit, push.
2. `git tag v2.3.0 && git push --tags`.

GitHub Actions (`.github/workflows/release.yml`) compile, crée la Release (zip + SHA-256), met à jour `site/version.json` ; Vercel redéploie le site. Les applications déjà installées affichent alors « Version x.y.z disponible ».

## Déploiement

| Élément | Où |
|---|---|
| Code, versions (zip) | GitHub : `lucaschoremskim-hub/system-pulse` |
| Page de téléchargement + `version.json` | Vercel, dossier `site/` |

## Composants tiers

- `Microsoft.Web.WebView2` 1.0.4191.47 (Microsoft, NuGet) — moteur d'affichage.
- `vendor/PresentMon-2.6.0-x64.exe` (Intel, MIT, signé) — mesure des FPS ; exige les droits administrateur ; compatibilité anti-triche non garantie. Licence : `vendor/LICENSE-PresentMon.txt`.

## Limites connues

- GPU : charge mesurée sur toutes les cartes (compteurs Windows « GPU Engine » ; `nvidia-smi` en priorité si présent). Température : cartes NVIDIA uniquement, « N/D » sinon.
- Exécutable non signé : SmartScreen peut afficher un avertissement.
- « Relancer en administrateur » : la confirmation Windows (UAC) n'est pas testée de bout en bout.
- Sur un PC sans WebView2 Runtime, un message l'indique et donne le lien d'installation.

Licence : MIT.
