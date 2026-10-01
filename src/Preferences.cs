using System;
using System.Collections.Generic;
using System.IO;

namespace SystemPulse
{
    // Choix d'affichage et position de l'overlay, enregistrés dans %APPDATA%\SystemPulse\preferences.json.
    internal sealed class Preferences
    {
        public static readonly string[] CardKeys = { "cpu", "gpu", "ram", "temperatures", "network", "storage", "fps" };
        public static readonly string[] OverlayKeys = { "cpu", "gpu", "ram", "temp", "network", "storage", "fps" };

        // Les FPS lancent PresentMon (lecture d'événements Windows, droits administrateur) : désactivés tant qu'on ne les coche pas.
        private static bool DefaultOn(string group, string key)
        {
            return key != "fps";
        }

        public Dictionary<string, bool> Cards = new Dictionary<string, bool>();
        public Dictionary<string, bool> Overlay = new Dictionary<string, bool>();
        // Ne contient que les disques explicitement cochés/décochés ; un disque absent est affiché.
        public Dictionary<string, bool> Drives = new Dictionary<string, bool>();
        public int? OverlayX;
        public int? OverlayY;
        // Lancer HWiNFO64 avec System Pulse, s'il est installé (pour une vraie température CPU : voir HwInfoBridge.cs).
        // Par défaut oui : sans HWiNFO installé, ça ne fait simplement rien.
        public bool LaunchHwInfo = true;

        public Preferences()
        {
            foreach (string k in CardKeys) Cards[k] = DefaultOn("cards", k);
            foreach (string k in OverlayKeys) Overlay[k] = DefaultOn("overlay", k);
        }

        public bool CardShown(string key) { bool v; return !Cards.TryGetValue(key, out v) || v; }
        public bool OverlayShown(string key) { bool v; return !Overlay.TryGetValue(key, out v) || v; }
        public bool DriveShown(string mount) { bool v; return !Drives.TryGetValue(mount, out v) || v; }

        public Dictionary<string, object> ToVisibility()
        {
            Dictionary<string, object> map = new Dictionary<string, object>();
            map["cards"] = new Dictionary<string, bool>(Cards);
            map["overlay"] = new Dictionary<string, bool>(Overlay);
            map["drives"] = new Dictionary<string, bool>(Drives);
            return map;
        }

        // Fusionne une demande de l'interface (valeurs booléennes connues uniquement).
        public void MergeVisibility(Dictionary<string, object> requested)
        {
            if (requested == null) return;
            MergeGroup(Cards, CardKeys, Json.GetObject(requested, "cards"));
            MergeGroup(Overlay, OverlayKeys, Json.GetObject(requested, "overlay"));
            Dictionary<string, object> drives = Json.GetObject(requested, "drives");
            if (drives != null)
            {
                foreach (KeyValuePair<string, object> pair in drives)
                {
                    if (System.Text.RegularExpressions.Regex.IsMatch(pair.Key, "^[A-Z]:$") && pair.Value is bool)
                        Drives[pair.Key] = (bool)pair.Value;
                }
            }
        }

        private static void MergeGroup(Dictionary<string, bool> target, string[] keys, Dictionary<string, object> source)
        {
            if (source == null) return;
            foreach (string key in keys)
            {
                object value = Json.Get(source, key);
                if (value is bool) target[key] = (bool)value;
            }
        }

        public static string DataDirectory()
        {
            string overrideDir = Environment.GetEnvironmentVariable("SYSTEMPULSE_DATA");
            if (!string.IsNullOrEmpty(overrideDir)) return overrideDir;
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SystemPulse");
        }

        private static string FilePath() { return Path.Combine(DataDirectory(), "preferences.json"); }

        public static Preferences Load()
        {
            Preferences prefs = new Preferences();
            string path = FilePath();
            // Reprise des réglages de l'ancienne version Electron si c'est le premier lancement.
            if (!File.Exists(path))
            {
                string old = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "system-pulse-dashboard", "preferences.json");
                if (Environment.GetEnvironmentVariable("SYSTEMPULSE_DATA") == null && File.Exists(old)) path = old;
            }
            try
            {
                if (!File.Exists(path)) return prefs;
                Dictionary<string, object> root = Json.ParseObject(File.ReadAllText(path));
                prefs.MergeVisibility(Json.GetObject(root, "visibility"));
                Dictionary<string, object> pos = Json.GetObject(root, "overlayPosition");
                double? x = Json.GetNumber(pos, "x");
                double? y = Json.GetNumber(pos, "y");
                if (x.HasValue && y.HasValue) { prefs.OverlayX = (int)Math.Round(x.Value); prefs.OverlayY = (int)Math.Round(y.Value); }
                prefs.LaunchHwInfo = Json.GetBool(root, "launchHwInfo", true);
            }
            catch (Exception)
            {
                return new Preferences();
            }
            return prefs;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(DataDirectory());
                Dictionary<string, object> root = new Dictionary<string, object>();
                root["version"] = 2;
                root["visibility"] = ToVisibility();
                if (OverlayX.HasValue && OverlayY.HasValue)
                {
                    Dictionary<string, object> pos = new Dictionary<string, object>();
                    pos["x"] = OverlayX.Value;
                    pos["y"] = OverlayY.Value;
                    root["overlayPosition"] = pos;
                }
                root["launchHwInfo"] = LaunchHwInfo;
                File.WriteAllText(FilePath(), Json.Serialize(root));
            }
            catch (Exception)
            {
                // Les préférences ne sont pas critiques : on ignore une écriture impossible.
            }
        }
    }
}
