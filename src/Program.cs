using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace SystemPulse
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            bool relaunched = Array.IndexOf(args, "--relaunched") >= 0;
            // Réglages du moteur d'affichage (mesurés sur ce PC : ouverture 2,5 s -> 0,7 s ; CPU au repos 30 % -> 8 % d'un coeur) :
            // - msSmartScreenProtection coupé : évite ~1,8 s d'attente d'une vérification de réputation inutile (page locale) ;
            // - GPU coupé : l'interface est légère, le rendu logiciel coûte moins que l'accélération sur ce type de page.
            string engineArgs = "--disable-features=msSmartScreenProtection --disable-gpu";
            Environment.SetEnvironmentVariable("WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS", engineArgs);
#if TESTHOOKS
            string debugPort = Environment.GetEnvironmentVariable("SYSTEMPULSE_DEBUG_PORT");
            if (!string.IsNullOrEmpty(debugPort))
                Environment.SetEnvironmentVariable("WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS",
                    "--remote-debugging-port=" + debugPort + " " + engineArgs + " " + (Environment.GetEnvironmentVariable("SYSTEMPULSE_WV2_ARGS") ?? ""));
            foreach (string a in args)
            {
                if (a.StartsWith("--smoke-test=", StringComparison.Ordinal))
                {
                    // Mesure unique écrite dans un fichier, sans fenêtre.
                    using (MetricsCollector c = new MetricsCollector())
                    {
                        Thread.Sleep(600);
                        File.WriteAllText(a.Substring("--smoke-test=".Length), Json.Serialize(c.Snapshot(new Dictionary<string, object>())));
                    }
                    return 0;
                }
            }
            // En test, plusieurs instances isolées (SYSTEMPULSE_DATA) peuvent coexister avec celle de l'utilisateur.
            bool skipSingle = Environment.GetEnvironmentVariable("SYSTEMPULSE_DATA") != null;
#else
            bool skipSingle = false;
#endif
            Mutex mutex = null;
            if (!skipSingle)
            {
                try
                {
                    mutex = new Mutex(false, @"Local\SystemPulseDashboard");
                    // Après « Relancer en administrateur », l'ancienne instance a quelques secondes pour se fermer.
                    if (!mutex.WaitOne(relaunched ? 10000 : 0)) { ActivateExisting(); return 0; }
                }
                catch (UnauthorizedAccessException)
                {
                    ActivateExisting(); // instance lancée avec d'autres droits
                    return 0;
                }
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            try
            {
                Application.Run(new MainForm());
            }
            finally
            {
                if (mutex != null) { try { mutex.ReleaseMutex(); } catch (ApplicationException) { } }
            }
            return 0;
        }

        private static void ActivateExisting()
        {
            IntPtr hwnd = Native.FindWindow(null, "System Pulse");
            if (hwnd != IntPtr.Zero)
            {
                Native.ShowWindow(hwnd, Native.SW_RESTORE);
                Native.SetForegroundWindow(hwnd);
            }
        }
    }
}
