using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace SystemPulse
{
    // Fenêtre principale : héberge l'interface HTML (WebView2, moteur d'Edge déjà présent sur le PC)
    // et relie l'interface au moteur de mesures, à l'overlay et à PresentMon.
    internal sealed class MainForm : Form
    {
        public static readonly string Version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version.ToString(3);
        private const string Host = "systempulse.local";
        private static readonly CultureInfo Fr = new CultureInfo("fr-FR");
        private static readonly Color Background = Color.FromArgb(8, 13, 21);

        private readonly WebView2 web = new WebView2();
        private readonly Preferences prefs;
        private readonly MetricsCollector metrics = new MetricsCollector();
        private readonly FpsMonitor fps;
        private readonly Timer timer = new Timer();
        private OverlayForm overlay;
        private int intervalMs = 1000;
        private bool overlayVisible;
        private bool overlayMovable;
        private bool webReady;
        private Dictionary<string, object> updateInfo;

        public MainForm()
        {
            prefs = Preferences.Load();
            fps = CreateFpsMonitor();

            Text = "System Pulse";
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch (Exception) { /* icône par défaut */ }
            BackColor = Background;
            StartPosition = FormStartPosition.CenterScreen;
            float scale;
            using (Graphics g = CreateGraphics()) { scale = g.DpiX / 96f; }
            // Zone d'affichage 590 x 360 : avec la barre de titre Windows, la fenêtre reste compacte (~590 x 395).
            ClientSize = new Size((int)(590 * scale), (int)(360 * scale));
            MinimumSize = new Size((int)(480 * scale) + (Width - ClientSize.Width), (int)(300 * scale) + (Height - ClientSize.Height));

            web.Dock = DockStyle.Fill;
            web.DefaultBackgroundColor = Background;
            Controls.Add(web);

            timer.Interval = intervalMs;
            timer.Tick += delegate { Tick(); };
            Shown += delegate { InitWebView(); };
            FormClosing += delegate { Shutdown(); };
        }

        private static FpsMonitor CreateFpsMonitor()
        {
#if TESTHOOKS
            string testCommand = Environment.GetEnvironmentVariable("SYSTEMPULSE_FPS_CMD");
            if (!string.IsNullOrEmpty(testCommand))
                return new FpsMonitor(testCommand, Environment.GetEnvironmentVariable("SYSTEMPULSE_FPS_ARGS") ?? "");
#endif
            string exe = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "vendor", "PresentMon-2.6.0-x64.exe");
            return new FpsMonitor(exe, FpsMonitor.DefaultArguments());
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            // Barre de titre sombre (Windows 10 2004+ / 11).
            int on = 1;
            if (Native.DwmSetWindowAttribute(Handle, 20, ref on, 4) != 0) Native.DwmSetWindowAttribute(Handle, 19, ref on, 4);
        }

        // Journal minimal (dernières erreurs de démarrage) : %APPDATA%\SystemPulse\journal.txt
        private static void Log(string line)
        {
            try
            {
                Directory.CreateDirectory(Preferences.DataDirectory());
                File.AppendAllText(Path.Combine(Preferences.DataDirectory(), "journal.txt"), DateTime.Now.ToString("s") + " " + line + "\r\n");
            }
            catch (Exception) { }
        }

        private async void InitWebView()
        {
            try
            {
                string dataDir = Preferences.DataDirectory();
                string userData = Path.Combine(dataDir, "WebView2");
                Log("WebView2 : création de l'environnement");
                CoreWebView2Environment env = await CoreWebView2Environment.CreateAsync(null, userData, null);
                Log("WebView2 : environnement créé, version " + env.BrowserVersionString);
                await web.EnsureCoreWebView2Async(env);
                Log("WebView2 : prêt");
                CoreWebView2 core = web.CoreWebView2;
                core.Settings.AreDefaultContextMenusEnabled = false;
                core.Settings.IsStatusBarEnabled = false;
                core.Settings.IsZoomControlEnabled = false;
                core.Settings.AreBrowserAcceleratorKeysEnabled = false;
#if TESTHOOKS
                core.Settings.AreDevToolsEnabled = true;
#else
                core.Settings.AreDevToolsEnabled = false;
#endif
                core.SetVirtualHostNameToFolderMapping(Host, Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ui"), CoreWebView2HostResourceAccessKind.Allow);
                core.NewWindowRequested += delegate (object s, CoreWebView2NewWindowRequestedEventArgs e) { e.Handled = true; };
                core.NavigationStarting += delegate (object s, CoreWebView2NavigationStartingEventArgs e)
                {
                    if (!e.Uri.StartsWith("https://" + Host + "/", StringComparison.OrdinalIgnoreCase)) e.Cancel = true;
                };
                core.WebMessageReceived += delegate (object s, CoreWebView2WebMessageReceivedEventArgs e) { OnWebMessage(e); };
                webReady = true;
                core.Navigate("https://" + Host + "/index.html?view=main");
                UpdateFpsMonitoring();
                StartUpdateCheck();
                StartHwInfoLaunch();
#if TESTHOOKS
                if (Environment.GetEnvironmentVariable("SYSTEMPULSE_NO_TIMER") == "1") return;
#endif
                timer.Start();
            }
            catch (Exception ex)
            {
                Log("ERREUR au démarrage de l'interface : " + ex);
                MessageBox.Show(this,
                    "L'interface n'a pas pu démarrer.\r\n\r\n" + ex.Message +
                    "\r\n\r\nDétails dans : " + Path.Combine(Preferences.DataDirectory(), "journal.txt") +
                    "\r\nSi le problème vient du composant WebView2, installez « WebView2 Runtime » (microsoft.com/edge/webview2) puis relancez.",
                    "System Pulse", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Close();
            }
        }

        // ---- Communication avec l'interface -------------------------------------------------

        private void PostToWeb(object payload)
        {
            if (!webReady || web.CoreWebView2 == null) return;
            try { web.CoreWebView2.PostWebMessageAsJson(Json.Serialize(payload)); }
            catch (Exception) { /* fenêtre en fermeture */ }
        }

        private void PostEvent(string name, object data)
        {
            Dictionary<string, object> msg = new Dictionary<string, object>();
            msg["event"] = name;
            msg["data"] = data;
            PostToWeb(msg);
        }

        private Dictionary<string, object> State()
        {
            Dictionary<string, object> s = new Dictionary<string, object>();
            s["overlayVisible"] = overlayVisible;
            s["overlayMovable"] = overlayMovable;
            s["intervalMs"] = intervalMs;
            s["visibility"] = prefs.ToVisibility();
            s["platform"] = "win32";
            s["version"] = Version;
            s["update"] = updateInfo;
            s["launchHwInfo"] = prefs.LaunchHwInfo;
            return s;
        }

        private void BroadcastState() { PostEvent("app:state", State()); }

        private Dictionary<string, object> CurrentMetrics() { return metrics.Snapshot(fps.Snapshot()); }

        private void OnWebMessage(CoreWebView2WebMessageReceivedEventArgs e)
        {
            string text;
            try { text = e.TryGetWebMessageAsString(); } catch (Exception) { return; }
            Dictionary<string, object> msg = Json.ParseObject(text);
            if (msg == null) return;
            string type = Json.GetString(msg, "type");
            object payload = Json.Get(msg, "payload");
            object result = null;
            switch (type)
            {
                case "getState": result = State(); break;
                case "getMetrics": result = CurrentMetrics(); break;
                case "setOverlayVisible": result = SetOverlayVisible(payload is bool && (bool)payload); break;
                case "setOverlayMovable": result = SetOverlayMovable(payload is bool && (bool)payload); break;
                case "setInterval": result = SetIntervalMs(payload); break;
                case "setVisibility": result = SetVisibility(payload as Dictionary<string, object>); break;
                case "relaunchAsAdmin": result = RelaunchAsAdmin(); break;
                case "openUpdate": result = OpenUpdate(); break;
                case "setLaunchHwInfo": result = SetLaunchHwInfo(payload is bool && (bool)payload); break;
                default: break; // windowAction et autres : rien à faire (barre de titre native)
            }
            double? id = Json.GetNumber(msg, "id");
            if (id.HasValue)
            {
                Dictionary<string, object> reply = new Dictionary<string, object>();
                reply["id"] = (int)id.Value;
                reply["result"] = result;
                PostToWeb(reply);
            }
        }

        private int SetIntervalMs(object payload)
        {
            double value;
            try { value = Convert.ToDouble(payload, CultureInfo.InvariantCulture); } catch (Exception) { return intervalMs; }
            intervalMs = (int)Math.Max(500, Math.Min(5000, value));
            timer.Interval = intervalMs;
            BroadcastState();
            Tick();
            return intervalMs;
        }

        private Dictionary<string, object> SetVisibility(Dictionary<string, object> requested)
        {
            prefs.MergeVisibility(requested);
            prefs.Save();
            UpdateFpsMonitoring();
            if (overlayVisible) Tick();
            BroadcastState();
            return prefs.ToVisibility();
        }

        // La mesure des FPS ne tourne que si une vue FPS est affichée (carte ou overlay actif).
        private void UpdateFpsMonitoring()
        {
            bool wanted = prefs.CardShown("fps") || (overlayVisible && prefs.OverlayShown("fps"));
            if (wanted) fps.Start(); else fps.Stop();
        }

        private async void StartUpdateCheck()
        {
            string url = UpdateCheck.DefaultUrl;
#if TESTHOOKS
            url = Environment.GetEnvironmentVariable("SYSTEMPULSE_UPDATE_URL");
            if (string.IsNullOrEmpty(url)) return; // les tests ne sortent pas sur Internet
#endif
            Dictionary<string, object> info = await UpdateCheck.CheckAsync(url, new Version(Version));
            if (info == null || IsDisposed) return;
            updateInfo = info;
            BroadcastState();
        }

        private void StartHwInfoLaunch()
        {
#if TESTHOOKS
            if (Environment.GetEnvironmentVariable("SYSTEMPULSE_ALLOW_HWINFO_LAUNCH") != "1") return; // jamais pendant les tests
#endif
            if (!prefs.LaunchHwInfo) return;
            Task.Run(delegate { HwInfoLauncher.LaunchIfInstalled(); });
        }

        private bool SetLaunchHwInfo(bool on)
        {
            prefs.LaunchHwInfo = on;
            prefs.Save();
            if (on) StartHwInfoLaunch();
            return on;
        }

        private bool OpenUpdate()
        {
            string link = Json.GetString(updateInfo, "url");
            if (!UpdateCheck.IsAllowedLink(link)) return false;
            try { Process.Start(new ProcessStartInfo(link) { UseShellExecute = true }); return true; }
            catch (Exception) { return false; }
        }

        private bool RelaunchAsAdmin()
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(Application.ExecutablePath);
                psi.Verb = "runas";
                psi.UseShellExecute = true;
                psi.Arguments = "--relaunched";
                Process.Start(psi);
            }
            catch (Win32Exception)
            {
                return false; // confirmation Windows refusée : on reste ouvert
            }
            BeginInvoke(new Action(Close));
            return true;
        }

        // ---- Cycle de mesure ---------------------------------------------------------------

        private void Tick()
        {
            Dictionary<string, object> m = CurrentMetrics();
            PostEvent("metrics:update", m);
            if (overlayVisible) UpdateOverlay(m);
        }

        // ---- Overlay -----------------------------------------------------------------------

        private bool SetOverlayVisible(bool visible)
        {
            overlayVisible = visible;
            if (visible)
            {
                if (overlay == null || overlay.IsDisposed)
                {
                    overlay = new OverlayForm();
                    overlay.MoveFinished += delegate
                    {
                        prefs.OverlayX = overlay.Left;
                        prefs.OverlayY = overlay.Top;
                        prefs.Save();
                    };
                    overlay.Location = new Point(-32000, -32000);
                    overlay.Show();
                }
                UpdateOverlay(CurrentMetrics());
                PlaceOverlay();
                UpdateOverlay(CurrentMetrics());
            }
            else
            {
                if (overlayMovable) SetOverlayMovable(false);
                if (overlay != null && !overlay.IsDisposed) overlay.Hide();
            }
            UpdateFpsMonitoring();
            BroadcastState();
            return overlayVisible;
        }

        private void PlaceOverlay()
        {
            Size size = overlay.PixelSize;
            Point target;
            if (prefs.OverlayX.HasValue && prefs.OverlayY.HasValue)
            {
                target = OverlayForm.ClampToScreen(prefs.OverlayX.Value, prefs.OverlayY.Value, size);
            }
            else
            {
                Rectangle area = Screen.PrimaryScreen.WorkingArea;
                int margin = (int)(18 * overlay.DeviceDpi / 96f);
                target = new Point(area.Right - size.Width - margin, area.Top + margin);
            }
            overlay.Location = target;
            if (!overlay.Visible) overlay.Show();
        }

        private bool SetOverlayMovable(bool movable)
        {
            overlayMovable = movable && overlayVisible && overlay != null && !overlay.IsDisposed;
            if (overlay != null && !overlay.IsDisposed)
            {
                overlay.SetMoveMode(overlayMovable);
                if (!overlayMovable)
                {
                    Point p = OverlayForm.ClampToScreen(overlay.Left, overlay.Top, overlay.PixelSize);
                    overlay.Location = p;
                    prefs.OverlayX = p.X; prefs.OverlayY = p.Y;
                    prefs.Save();
                    UpdateOverlay(CurrentMetrics());
                }
            }
            BroadcastState();
            return overlayMovable;
        }

        private static string Pct(double v) { return v.ToString("0.0", Fr); }

        private static string Rate(double? bytesPerSecond)
        {
            if (!bytesPerSecond.HasValue) return "—";
            double v = bytesPerSecond.Value;
            if (v < 1024) return Math.Round(v) + " B/s";
            if (v < 1024 * 1024) return (v / 1024).ToString(v < 10240 ? "0.0" : "0", Fr) + " KB/s";
            if (v < 1024.0 * 1024 * 1024) return (v / (1024 * 1024)).ToString(v < 10485760 ? "0.0" : "0", Fr) + " MB/s";
            return (v / (1024.0 * 1024 * 1024)).ToString("0.0", Fr) + " GB/s";
        }

        private static double? Num(Dictionary<string, object> map, string key) { return Json.GetNumber(map, key); }

        private void UpdateOverlay(Dictionary<string, object> m)
        {
            if (overlay == null || overlay.IsDisposed) return;
            List<OverlayLine> lines = new List<OverlayLine>();
            Dictionary<string, object> cpu = Json.GetObject(m, "cpu");
            Dictionary<string, object> gpu = Json.GetObject(m, "gpu");
            Dictionary<string, object> ram = Json.GetObject(m, "ram");
            Dictionary<string, object> net = Json.GetObject(m, "network");
            Dictionary<string, object> fpsMap = Json.GetObject(m, "fps");
            Dictionary<string, object> storage = Json.GetObject(m, "storage");

            if (prefs.OverlayShown("cpu")) lines.Add(Line("CPU", Num(cpu, "value"), "%", OverlayForm.Mint));
            if (prefs.OverlayShown("gpu")) lines.Add(Line("GPU", Num(gpu, "value"), "%", OverlayForm.Violet));
            if (prefs.OverlayShown("ram")) lines.Add(Line("RAM", Num(ram, "value"), "%", OverlayForm.Amber));
            if (prefs.OverlayShown("temp"))
            {
                double? ct = Num(cpu, "temperature");
                OverlayLine cl = new OverlayLine();
                cl.Label = "CPU TEMP"; cl.Value = ct.HasValue ? Math.Round(ct.Value) + " °C" : "N/D"; cl.Unit = ""; cl.ValueColor = OverlayForm.Mint;
                lines.Add(cl);

                double? t = Num(gpu, "temperature");
                OverlayLine l = new OverlayLine();
                l.Label = "GPU TEMP"; l.Value = t.HasValue ? Math.Round(t.Value) + " °C" : "N/D"; l.Unit = ""; l.ValueColor = OverlayForm.Orange;
                lines.Add(l);
            }
            if (prefs.OverlayShown("fps"))
            {
                double? f = Num(fpsMap, "value");
                OverlayLine l = new OverlayLine();
                l.Label = "FPS"; l.Value = f.HasValue ? Math.Round(f.Value).ToString(CultureInfo.InvariantCulture) : "—"; l.Unit = ""; l.ValueColor = OverlayForm.Mint;
                lines.Add(l);
            }
            if (prefs.OverlayShown("network"))
            {
                OverlayLine l = new OverlayLine();
                l.Label = "NET ↓"; l.Value = Rate(Num(net, "download")); l.Unit = ""; l.Secondary = "↑ " + Rate(Num(net, "upload")); l.ValueColor = OverlayForm.Blue;
                lines.Add(l);
            }
            if (prefs.OverlayShown("storage"))
            {
                System.Collections.IEnumerable drives = Json.Get(storage, "drives") as System.Collections.IEnumerable;
                if (drives != null)
                {
                    foreach (object o in drives)
                    {
                        Dictionary<string, object> d = o as Dictionary<string, object>;
                        if (d == null) continue;
                        string mount = Json.GetString(d, "mount");
                        if (!prefs.DriveShown(mount)) continue;
                        lines.Add(Line("DISK " + mount, Num(d, "value"), "%", OverlayForm.Pink));
                    }
                }
            }
            overlay.Render(lines, DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture));
        }

        private static OverlayLine Line(string label, double? value, string unit, Color color)
        {
            OverlayLine l = new OverlayLine();
            l.Label = label; l.Value = value.HasValue ? Pct(value.Value) : "—"; l.Unit = unit; l.ValueColor = color;
            return l;
        }

        // ---- Fermeture ---------------------------------------------------------------------

        private void Shutdown()
        {
            timer.Stop();
            fps.Stop();
            prefs.Save();
            if (overlay != null && !overlay.IsDisposed) overlay.Close();
        }

#if TESTHOOKS
        // Aide aux tests : image de l'overlay et position en clair (réservé à la version de test).
        public string DebugOverlayInfo()
        {
            if (overlay == null || overlay.IsDisposed) return "{}";
            Dictionary<string, object> info = new Dictionary<string, object>();
            info["x"] = overlay.Left; info["y"] = overlay.Top; info["w"] = overlay.PixelSize.Width; info["h"] = overlay.PixelSize.Height;
            info["visible"] = overlay.Visible; info["move"] = overlay.MoveMode;
            return Json.Serialize(info);
        }
#endif
    }
}
