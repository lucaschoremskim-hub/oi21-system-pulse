using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace SystemPulse
{
    // Mesure des FPS de l'application qui affiche le plus d'images, via PresentMon (Intel, MIT).
    // PresentMon lit des événements Windows (ETW) : il n'injecte rien dans les jeux.
    // Il exige les droits administrateur ou l'appartenance au groupe « Performance Log Users ».
    internal sealed class FpsMonitor : IDisposable
    {
        private sealed class Frame { public double Ms; public long At; }
        private sealed class Entry { public string App; public List<Frame> Frames = new List<Frame>(); }

        private static readonly HashSet<string> Ignored = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "dwm.exe", "explorer.exe", "shellexperiencehost.exe", "searchhost.exe", "startmenuexperiencehost.exe",
            "textinputhost.exe", "applicationframehost.exe", "lockapp.exe", "presentmon-2.6.0-x64.exe",
            "system pulse dashboard.exe", "systempulse.exe", "electron.exe"
        };

        private readonly string command;
        private readonly string arguments;
        private readonly int windowMs;
        private readonly int staleMs;
        private readonly int earlyExitMs;
        private readonly object gate = new object();
        private readonly Dictionary<string, Entry> processes = new Dictionary<string, Entry>();

        private Process child;
        private bool stopping;
        private long startedAt;
        private int colApp = -1, colPid = -1, colFrame = -1, colDropped = -1;
        private int preHeaderLines;
        private string stderrText = "";
        private string status = "off"; // off | starting | waiting | ok | no-rights | error | missing
        private string detail = "";

        public FpsMonitor(string command, string arguments)
            : this(command, arguments, 1000, 2500, 6000) { }

        public FpsMonitor(string command, string arguments, int windowMs, int staleMs, int earlyExitMs)
        {
            this.command = command;
            this.arguments = arguments;
            this.windowMs = windowMs;
            this.staleMs = staleMs;
            this.earlyExitMs = earlyExitMs;
        }

        public static string DefaultArguments()
        {
            return "--output_stdout --no_console_stats --v1_metrics --exclude_dropped --no_track_input --no_track_gpu --session_name SystemPulseFPS --stop_existing_session";
        }

        private static long NowMs() { return Stopwatch.GetTimestamp() * 1000 / Stopwatch.Frequency; }

        public bool Running { get { lock (gate) { return child != null; } } }

        public void Start()
        {
            lock (gate)
            {
                if (child != null) return;
                if (string.IsNullOrEmpty(command) || (command.IndexOf('\\') >= 0 && !System.IO.File.Exists(command)))
                {
                    status = "missing"; detail = "PresentMon introuvable.";
                    return;
                }
                stopping = false;
                processes.Clear();
                colApp = colPid = colFrame = colDropped = -1;
                preHeaderLines = 0;
                stderrText = "";
                status = "starting";
                detail = "";
                startedAt = NowMs();
                try
                {
                    ProcessStartInfo psi = new ProcessStartInfo(command, arguments);
                    psi.UseShellExecute = false;
                    psi.CreateNoWindow = true;
                    psi.RedirectStandardOutput = true;
                    psi.RedirectStandardError = true;
                    Process p = new Process();
                    p.StartInfo = psi;
                    p.EnableRaisingEvents = true;
                    p.OutputDataReceived += delegate (object s, DataReceivedEventArgs e) { if (e.Data != null) OnLine(p, e.Data); };
                    p.ErrorDataReceived += delegate (object s, DataReceivedEventArgs e)
                    {
                        if (e.Data == null) return;
                        lock (gate)
                        {
                            if (child != p) return;
                            stderrText = (stderrText + e.Data + "\n");
                            if (stderrText.Length > 2000) stderrText = stderrText.Substring(stderrText.Length - 2000);
                        }
                    };
                    p.Exited += delegate (object s, EventArgs e) { OnExit(p); };
                    p.Start();
                    child = p;
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();
                }
                catch (System.ComponentModel.Win32Exception ex)
                {
                    child = null;
                    if (ex.NativeErrorCode == 2) { status = "missing"; detail = "PresentMon introuvable."; }
                    else { status = "error"; detail = ex.Message; }
                }
                catch (Exception ex)
                {
                    child = null; status = "error"; detail = ex.Message;
                }
            }
        }

        public void Stop()
        {
            Process p;
            lock (gate)
            {
                stopping = true;
                p = child;
                child = null;
                processes.Clear();
                status = "off";
                detail = "";
            }
            KillTree(p);
        }

        private static void KillTree(Process p)
        {
            if (p == null) return;
            try
            {
                if (!p.HasExited)
                {
                    ProcessStartInfo psi = new ProcessStartInfo("taskkill.exe", "/PID " + p.Id + " /T /F");
                    psi.CreateNoWindow = true; psi.UseShellExecute = false;
                    using (Process k = Process.Start(psi)) { k.WaitForExit(3000); }
                }
            }
            catch (Exception) { try { p.Kill(); } catch (Exception) { } }
        }

        private static string[] SplitCsv(string line)
        {
            List<string> cells = new List<string>();
            System.Text.StringBuilder current = new System.Text.StringBuilder();
            bool quoted = false;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (c == '"')
                {
                    if (quoted && i + 1 < line.Length && line[i + 1] == '"') { current.Append('"'); i++; }
                    else quoted = !quoted;
                }
                else if (c == ',' && !quoted) { cells.Add(current.ToString()); current.Length = 0; }
                else current.Append(c);
            }
            cells.Add(current.ToString());
            return cells.ToArray();
        }

        private void OnLine(Process source, string line)
        {
            if (line.Length == 0) return;
            string[] cells = SplitCsv(line);
            lock (gate)
            {
                if (child != source) return;
                if (colApp < 0)
                {
                    int app = Array.IndexOf(cells, "Application");
                    if (app < 0)
                    {
                        preHeaderLines++;
                        if (preHeaderLines > 5) { status = "error"; detail = "Format de sortie PresentMon inattendu."; }
                        return;
                    }
                    int pid = Array.IndexOf(cells, "ProcessID");
                    int frame = Array.IndexOf(cells, "msBetweenPresents");
                    if (frame < 0) frame = Array.IndexOf(cells, "FrameTime");
                    if (pid < 0 || frame < 0) { status = "error"; detail = "Format de sortie PresentMon inattendu."; return; }
                    colApp = app; colPid = pid; colFrame = frame; colDropped = Array.IndexOf(cells, "Dropped");
                    return;
                }
                int max = Math.Max(Math.Max(colApp, colPid), Math.Max(colFrame, colDropped));
                if (cells.Length <= max) return;
                if (colDropped >= 0 && cells[colDropped] == "1") return;
                double ms;
                if (!double.TryParse(cells[colFrame], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out ms)) return;
                if (ms <= 0 || ms > 5000) return;
                string key = cells[colPid];
                Entry entry;
                if (!processes.TryGetValue(key, out entry)) { entry = new Entry(); entry.App = cells[colApp]; processes[key] = entry; }
                Frame f = new Frame(); f.Ms = ms; f.At = NowMs();
                entry.Frames.Add(f);
                if (entry.Frames.Count > 400) entry.Frames.RemoveRange(0, entry.Frames.Count - 400);
            }
        }

        private void OnExit(Process p)
        {
            lock (gate)
            {
                if (child != p) return;
                child = null;
                if (stopping) { status = "off"; return; }
                int code = 0;
                try { code = p.ExitCode; } catch (Exception) { }
                bool early = NowMs() - startedAt < earlyExitMs;
                string text = stderrText.Trim();
                string errorLine = "";
                string firstLine = "";
                foreach (string raw in text.Split('\n'))
                {
                    string l = raw.Trim();
                    if (l.Length == 0) continue;
                    if (firstLine.Length == 0) firstLine = l;
                    if (errorLine.Length == 0 && l.StartsWith("error:", StringComparison.OrdinalIgnoreCase)) errorLine = l;
                }
                if (errorLine.Length == 0) errorLine = firstLine;
                if (code != 0 && (Regex.IsMatch(text, "access denied|privilege|Performance Log", RegexOptions.IgnoreCase) || (early && text.Length == 0)))
                {
                    status = "no-rights"; detail = errorLine;
                }
                else
                {
                    status = "error";
                    detail = errorLine.Length > 0 ? errorLine : "PresentMon s'est arrêté (code " + code + ").";
                }
            }
        }

        // Résultat pour l'interface : { value, app, status, detail }.
        public Dictionary<string, object> Snapshot()
        {
            lock (gate)
            {
                Dictionary<string, object> result = new Dictionary<string, object>();
                if (status == "off" || status == "missing" || status == "no-rights" || status == "error")
                {
                    result["value"] = null; result["app"] = null; result["status"] = status; result["detail"] = detail;
                    return result;
                }
                long now = NowMs();
                string bestApp = null;
                double bestFps = -1;
                List<string> dead = new List<string>();
                foreach (KeyValuePair<string, Entry> pair in processes)
                {
                    Entry entry = pair.Value;
                    entry.Frames.RemoveAll(delegate (Frame f) { return now - f.At > staleMs; });
                    if (entry.Frames.Count == 0) { dead.Add(pair.Key); continue; }
                    if (Ignored.Contains(entry.App)) continue;
                    double total = 0; int count = 0;
                    for (int i = entry.Frames.Count - 1; i >= 0 && total < windowMs; i--) { total += entry.Frames[i].Ms; count++; }
                    if (total <= 0) continue;
                    double fps = count / total * 1000.0;
                    if (fps > bestFps) { bestFps = fps; bestApp = entry.App; }
                }
                foreach (string key in dead) processes.Remove(key);
                if (bestApp != null)
                {
                    status = "ok";
                    result["value"] = Math.Round(bestFps, 1); result["app"] = bestApp; result["status"] = "ok"; result["detail"] = "";
                    return result;
                }
                if (child != null && colApp >= 0) status = "waiting";
                result["value"] = null; result["app"] = null; result["status"] = status; result["detail"] = detail;
                return result;
            }
        }

        public void Dispose() { Stop(); }
    }
}
