using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace SystemPulse
{
    // Lance HWiNFO64 avec System Pulse, s'il est installé (pour une vraie température CPU : voir HwInfoBridge.cs).
    // Ne l'installe jamais : seulement le détecter (registre des programmes installés, emplacements habituels)
    // et le démarrer s'il n'est pas déjà lancé. Sans HWiNFO installé, ne fait rien, silencieusement.
    internal static class HwInfoLauncher
    {
        private static readonly string[] ProcessNames = { "HWiNFO64", "HWiNFO32", "HWiNFO_ARM64" };

        public static bool IsRunning()
        {
            foreach (string name in ProcessNames)
            {
                try { Process[] p = Process.GetProcessesByName(name); if (p.Length > 0) return true; }
                catch (Exception) { }
            }
            return false;
        }

        // Au besoin seulement (appel non trivial) : registre des programmes installés, puis emplacements habituels.
        public static string FindInstalledExe()
        {
            string fromRegistry = FindInRegistry();
            if (fromRegistry != null) return fromRegistry;

            string[] candidates =
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "HWiNFO64", "HWiNFO64.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "HWiNFO64", "HWiNFO64.exe"),
            };
            foreach (string path in candidates) { if (File.Exists(path)) return path; }
            return null;
        }

        private static string FindInRegistry()
        {
            string[] hives =
            {
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
                @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
            };
            foreach (string hive in hives)
            {
                try
                {
                    using (RegistryKey root = Registry.LocalMachine.OpenSubKey(hive))
                    {
                        if (root == null) continue;
                        foreach (string sub in root.GetSubKeyNames())
                        {
                            using (RegistryKey key = root.OpenSubKey(sub))
                            {
                                string name = key == null ? null : key.GetValue("DisplayName") as string;
                                if (name == null || name.IndexOf("HWiNFO", StringComparison.OrdinalIgnoreCase) < 0) continue;
                                string dir = key.GetValue("InstallLocation") as string;
                                if (string.IsNullOrEmpty(dir)) continue;
                                string exe = Path.Combine(dir, "HWiNFO64.exe");
                                if (File.Exists(exe)) return exe;
                            }
                        }
                    }
                }
                catch (Exception) { }
            }
            return null;
        }

        // Lance HWiNFO64 s'il est installé et pas déjà lancé. Ne bloque jamais, ne montre jamais d'erreur :
        // la plupart des utilisateurs n'ont pas HWiNFO, et c'est un fonctionnement normal, pas une panne.
        public static void LaunchIfInstalled()
        {
            try
            {
                if (IsRunning()) return;
                string exe = FindInstalledExe();
                if (exe == null) return;
                ProcessStartInfo psi = new ProcessStartInfo(exe);
                psi.UseShellExecute = true; // laisse Windows gérer sa propre demande de droits, si son manifeste le prévoit
                psi.WorkingDirectory = Path.GetDirectoryName(exe);
                Process.Start(psi);
            }
            catch (Exception)
            {
                // Lancement refusé (UAC annulée, antivirus...) : ce n'est pas plus grave qu'une mesure en N/D.
            }
        }
    }
}
