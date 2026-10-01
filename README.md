# Oi-21 System Pulse

Moniteur Windows léger : CPU, GPU (toutes marques), RAM, température CPU et GPU (selon le matériel), réseau, tous les disques et FPS réels, avec un overlay déplaçable à poser par-dessus les jeux.

**Téléchargement : https://oi21-system-pulse.vercel.app** (zip de 0,7 Mo, rien à installer, ouverture en ~0,5 s).

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
node tests\ui-gpu.mjs     <dossier-captures>   # repli GPU par compteurs Windows (4)
node tests\ui-cpu-temp.mjs <dossier-captures>  # température CPU (HWiNFO, ACPI, niveaux, N/D) (16)
node tests\ui-hwinfo-launch.mjs <dossier-captures>  # lancement de HWiNFO64, préférence, bouton admin (8)
```

## Publier une version

1. Changer `<Version>` dans `SystemPulse.csproj`, commit, push.
2. `git tag v2.6.0 && git push --tags`.

GitHub Actions (`.github/workflows/release.yml`) compile, crée la Release (zip + SHA-256), met à jour `site/version.json` ; Vercel redéploie le site. Les applications déjà installées affichent alors « Version x.y.z disponible ».

## Déploiement

| Élément | Où |
|---|---|
| Code, versions (zip) | GitHub : `lucaschoremskim-hub/oi21-system-pulse` |
| Page de téléchargement + `version.json` | Vercel, dossier `site/` |

## Composants tiers

- `Microsoft.Web.WebView2` 1.0.4191.47 (Microsoft, NuGet) — moteur d'affichage.
- `vendor/PresentMon-2.6.0-x64.exe` (Intel, MIT, signé) — mesure des FPS ; exige les droits administrateur ; compatibilité anti-triche non garantie. Licence : `vendor/LICENSE-PresentMon.txt`.

## Limites connues

- GPU : charge mesurée sur toutes les cartes (compteurs Windows « GPU Engine » ; `nvidia-smi` en priorité si présent). Température : cartes NVIDIA uniquement, « N/D » sinon.
- Température CPU, dans l'ordre : (1) [HWiNFO64](https://www.hwinfo.com/) s'il tourne à côté (`HwInfoBridge.cs` lit sa mémoire partagée, lecture seule — mécanisme officiel, le même qu'utilisent Rainmeter ou MSI Afterburner) ; sinon (2) les zones thermiques ACPI (`root\WMI`, `MSAcpi_ThermalZoneTemperature`), sans droits administrateur ni pilote tiers, mais souvent absentes sur les PC de bureau (capteurs non décrits à l'ACPI) : « N/D » alors, honnêtement, plutôt qu'une fausse valeur — c'est le cas sur la carte mère utilisée pour développer ce projet.
  - HWiNFO64 n'est pas intégré ni redistribué (logiciel fermé) : à installer soi-même (`HwInfoLauncher.cs` ne fait que le détecter et le lancer s'il est présent — registre des programmes installés puis `Program Files\HWiNFO64` ; case « Affichage » > « Lancer HWiNFO64 au démarrage », cochée par défaut, sans effet si HWiNFO n'est pas installé). Vérifié (8.54, installeur officiel) : System Pulse le détecte et le lance correctement, même sans droits administrateur. Son tout premier lancement affiche son propre écran de démarrage (choisir « Sensors only », activer « Shared Memory Support » dans ses réglages) : il s'en souvient ensuite.
  - Vérifié ici (HWiNFO 8.54) : sa mémoire partagée n'est lisible, même en lecture seule, que si **System Pulse tourne aussi en administrateur** (même bouton que pour les FPS) — HWiNFO protège son partage par une liste de contrôle d'accès qui exige le même niveau de droits. Sans ça, repli automatique sur les zones ACPI.
  - La version gratuite de HWiNFO limite ce partage à 12 h après son propre démarrage (relancer HWiNFO, ou la version Pro, pour lever la limite).
  - Mesure directe universelle (type LibreHardwareMonitor) testée et écartée : son pilote échoue au chargement (`ERROR_DRIVER_BLOCKED`, 0xE1) sur un Windows à jour — Microsoft le bloque par défaut depuis 2022-2023 (liste des pilotes vulnérables), admin ou non.
- Exécutable non signé : SmartScreen peut afficher un avertissement.
- « Relancer en administrateur » : la confirmation Windows (UAC) n'est pas testée de bout en bout.
- Sur un PC sans WebView2 Runtime, un message l'indique et donne le lien d'installation.

Licence : MIT.
