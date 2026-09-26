using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;

namespace SystemPulse
{
    internal sealed class DriveStat
    {
        public string Mount;
        public double Value;
        public long Used;
        public long Total;
        public long Available;
    }

    // Collecte légère : CPU/RAM/réseau à chaque cycle, disques (10 s) et GPU NVIDIA (3 s) en arrière-plan.
    internal sealed class MetricsCollector : IDisposable
    {
        private ulong prevIdle, prevKernel, prevUser;
        private bool hasPrevCpu;
        private double cpuValue, cpuUser, cpuSystem;

        private long prevRx, prevTx;
        private long prevNetTicks;
        private double netDown, netUp;
        private long lastNetRead;

        private volatile List<DriveStat> drives = new List<DriveStat>();
        private long lastDriveRefresh;
        private int driveBusy;
        private int netBusy;

        private string gpuName;
        private double? gpuUsage;
        private double? gpuTemp;
        private double? gpuVram;
        private long lastGpuRefresh;
        private int gpuBusy;
        private bool nvidiaMissing;

        private readonly string hostname = Environment.MachineName;
        private readonly string systemRoot;
        private static readonly long StopwatchFrequency = Stopwatch.Frequency;

        public MetricsCollector()
        {
            systemRoot = (Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\").Substring(0, 2).ToUpperInvariant();
            SampleCpu();
            // Lectures potentiellement lentes au premier appel : en arrière-plan, l'interface ne les attend pas.
            netBusy = 1;
            Task.Run(delegate { try { SampleNetwork(); } finally { Interlocked.Exchange(ref netBusy, 0); } });
            driveBusy = 1;
            Task.Run(delegate { try { RefreshDrives(); } finally { Interlocked.Exchange(ref driveBusy, 0); } });
        }

        private static long NowMs() { return Stopwatch.GetTimestamp() * 1000 / StopwatchFrequency; }

        private static ulong ToUlong(Native.FILETIME t) { return ((ulong)t.High << 32) | t.Low; }

        private void SampleCpu()
        {
            Native.FILETIME idle, kernel, user;
            if (!Native.GetSystemTimes(out idle, out kernel, out user)) return;
            ulong i = ToUlong(idle), k = ToUlong(kernel), u = ToUlong(user);
            if (hasPrevCpu)
            {
                double dIdle = i - prevIdle;
                double dKernel = k - prevKernel;
                double dUser = u - prevUser;
                double total = dKernel + dUser; // le noyau inclut le temps d'inactivité
                if (total > 0)
                {
                    cpuValue = Math.Max(0, Math.Min(100, (total - dIdle) / total * 100));
                    cpuUser = Math.Max(0, Math.Min(100, dUser / total * 100));
                    cpuSystem = Math.Max(0, Math.Min(100, (dKernel - dIdle) / total * 100));
                }
            }
            prevIdle = i; prevKernel = k; prevUser = u;
            hasPrevCpu = true;
        }

        private void SampleNetwork()
        {
            long rx = 0, tx = 0;
            try
            {
                foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != OperationalStatus.Up) continue;
                    if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback || nic.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
                    IPv4InterfaceStatistics stats = nic.GetIPv4Statistics();
                    rx += stats.BytesReceived;
                    tx += stats.BytesSent;
                }
            }
            catch (Exception) { return; }
            long now = NowMs();
            if (prevNetTicks > 0 && now > prevNetTicks)
            {
                double seconds = (now - prevNetTicks) / 1000.0;
                netDown = Math.Max(0, (rx - prevRx) / seconds);
                netUp = Math.Max(0, (tx - prevTx) / seconds);
            }
            prevRx = rx; prevTx = tx; prevNetTicks = now;
        }

        private void RefreshDrives()
        {
            List<DriveStat> next = new List<DriveStat>();
            try
            {
                foreach (DriveInfo d in DriveInfo.GetDrives())
                {
                    if (d.DriveType != DriveType.Fixed && d.DriveType != DriveType.Removable) continue;
                    try
                    {
                        if (!d.IsReady) continue;
                        long total = d.TotalSize;
                        if (total <= 0) continue;
                        long free = d.AvailableFreeSpace;
                        long used = Math.Max(0, total - free);
                        DriveStat s = new DriveStat();
                        s.Mount = d.Name.Substring(0, 2).ToUpperInvariant();
                        s.Total = total; s.Available = free; s.Used = used;
                        s.Value = (double)used / total * 100.0;
                        next.Add(s);
                    }
                    catch (Exception) { /* disque indisponible : ignoré */ }
                }
            }
            catch (Exception) { }
            next.Sort(delegate (DriveStat a, DriveStat b) { return string.CompareOrdinal(a.Mount, b.Mount); });
            drives = next;
            lastDriveRefresh = NowMs();
        }

        private void RefreshGpu()
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("nvidia-smi.exe",
                    "--query-gpu=name,utilization.gpu,temperature.gpu,memory.used,memory.total --format=csv,noheader,nounits");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                using (Process p = Process.Start(psi))
                {
                    string output = p.StandardOutput.ReadToEnd();
                    if (!p.WaitForExit(2500)) { try { p.Kill(); } catch (Exception) { } return; }
                    string line = null;
                    foreach (string l in output.Split('\n')) { if (l.Trim().Length > 0) { line = l.Trim(); break; } }
                    if (line == null) return;
                    string[] parts = line.Split(',');
                    if (parts.Length < 5) return;
                    gpuName = parts[0].Trim();
                    gpuUsage = ParseNumber(parts[1]);
                    gpuTemp = ParseNumber(parts[2]);
                    double? total = ParseNumber(parts[4]);
                    gpuVram = total.HasValue ? total.Value * 1024 * 1024 : (double?)null;
                }
            }
            catch (Exception)
            {
                nvidiaMissing = true; // pas de nvidia-smi : GPU non mesuré (N/D)
            }
        }

        private static double? ParseNumber(string text)
        {
            double value;
            if (double.TryParse(text.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out value)) return value;
            return null;
        }

        private void KickDrives()
        {
            long now = NowMs();
            if (now - lastDriveRefresh < 10000) return;
            if (Interlocked.CompareExchange(ref driveBusy, 1, 0) != 0) return;
            lastDriveRefresh = now;
            Task.Run(delegate { try { RefreshDrives(); } finally { Interlocked.Exchange(ref driveBusy, 0); } });
        }

        private void KickGpu()
        {
            if (nvidiaMissing) return;
            long now = NowMs();
            if (now - lastGpuRefresh < 3000) return;
            if (Interlocked.CompareExchange(ref gpuBusy, 1, 0) != 0) return;
            lastGpuRefresh = now;
            Task.Run(delegate { try { RefreshGpu(); } finally { Interlocked.Exchange(ref gpuBusy, 0); } });
        }

        public List<DriveStat> Drives { get { return drives; } }

        private static object Round1(double value) { return Math.Round(value, 1); }

        private static object NullableRound1(double? value) { return value.HasValue ? (object)Math.Round(value.Value, 1) : null; }

        public Dictionary<string, object> Snapshot(Dictionary<string, object> fps)
        {
            SampleCpu();
            long now = NowMs();
            if (now - lastNetRead >= 900 && Interlocked.CompareExchange(ref netBusy, 1, 0) == 0)
            {
                lastNetRead = now;
                Task.Run(delegate { try { SampleNetwork(); } finally { Interlocked.Exchange(ref netBusy, 0); } });
            }
            KickDrives();
            KickGpu();

            Native.MEMORYSTATUSEX mem = new Native.MEMORYSTATUSEX();
            Native.GlobalMemoryStatusEx(mem);
            double memTotal = mem.ullTotalPhys;
            double memAvail = mem.ullAvailPhys;
            double memUsed = Math.Max(0, memTotal - memAvail);

            List<DriveStat> list = drives;
            DriveStat system = list.Find(delegate (DriveStat d) { return d.Mount == systemRoot; });
            if (system == null && list.Count > 0) system = list[0];

            Dictionary<string, object> root = new Dictionary<string, object>();
            root["timestamp"] = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalMilliseconds;

            Dictionary<string, object> sys = new Dictionary<string, object>();
            sys["hostname"] = hostname; sys["platform"] = "win32"; sys["distro"] = "Windows";
            root["system"] = sys;

            Dictionary<string, object> cpu = new Dictionary<string, object>();
            cpu["value"] = Round1(cpuValue); cpu["user"] = Round1(cpuUser); cpu["system"] = Round1(cpuSystem); cpu["cores"] = Environment.ProcessorCount;
            root["cpu"] = cpu;

            Dictionary<string, object> gpu = new Dictionary<string, object>();
            gpu["value"] = NullableRound1(gpuUsage); gpu["name"] = gpuName; gpu["temperature"] = NullableRound1(gpuTemp); gpu["vram"] = gpuVram.HasValue ? (object)gpuVram.Value : null;
            root["gpu"] = gpu;

            Dictionary<string, object> ram = new Dictionary<string, object>();
            ram["value"] = memTotal > 0 ? Round1(memUsed / memTotal * 100.0) : null; ram["used"] = memUsed; ram["total"] = memTotal; ram["available"] = memAvail;
            root["ram"] = ram;

            Dictionary<string, object> temps = new Dictionary<string, object>();
            temps["gpu"] = NullableRound1(gpuTemp);
            root["temperatures"] = temps;

            Dictionary<string, object> net = new Dictionary<string, object>();
            net["download"] = Round1(netDown); net["upload"] = Round1(netUp); net["interface"] = "Toutes interfaces";
            root["network"] = net;

            Dictionary<string, object> storage = new Dictionary<string, object>();
            storage["mount"] = system != null ? system.Mount : systemRoot;
            storage["value"] = system != null ? Round1(system.Value) : null;
            storage["used"] = system != null ? (object)system.Used : null;
            storage["total"] = system != null ? (object)system.Total : null;
            storage["available"] = system != null ? (object)system.Available : null;
            List<object> driveList = new List<object>();
            foreach (DriveStat d in list)
            {
                Dictionary<string, object> one = new Dictionary<string, object>();
                one["mount"] = d.Mount; one["value"] = Round1(d.Value); one["used"] = d.Used; one["total"] = d.Total; one["available"] = d.Available;
                driveList.Add(one);
            }
            storage["drives"] = driveList;
            root["storage"] = storage;

            root["fps"] = fps;
            return root;
        }

        public void Dispose() { }
    }
}
