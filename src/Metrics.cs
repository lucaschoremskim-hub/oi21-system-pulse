using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Management;

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
        private Dictionary<string, CounterSample> prevGpuSamples;
        private bool gpuInfoRead;

        private double? cpuTemp;
        private bool cpuTempFromHwInfo;
        private string forcedHwInfoStatus; // tests uniquement : simule un statut HWiNFO sans vraie installation
        private bool cpuTempUnsupported;
        private long lastCpuTempRefresh;
        private int cpuTempBusy;
        private readonly HwInfoBridge hwinfo = new HwInfoBridge();

        private readonly string hostname = Environment.MachineName;
        private readonly string systemRoot;
        private static readonly long StopwatchFrequency = Stopwatch.Frequency;

        public MetricsCollector()
        {
            systemRoot = (Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\").Substring(0, 2).ToUpperInvariant();
            SampleCpu();
#if TESTHOOKS
            nvidiaMissing = Environment.GetEnvironmentVariable("SYSTEMPULSE_GPU_MODE") == "counters";
#endif
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
            if (nvidiaMissing) { RefreshGpuGeneric(); return; }
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
                nvidiaMissing = true; // pas de nvidia-smi : bascule sur les compteurs Windows (tous constructeurs)
                RefreshGpuGeneric();
            }
        }

        // Repli pour AMD / Intel (et NVIDIA sans nvidia-smi) : compteurs « GPU Engine » de Windows 10 1709+.
        // Comme le Gestionnaire des tâches : somme par moteur (3D, Copy, Video...) et par carte, puis le moteur le plus chargé.
        // La température n'est pas exposée par ces compteurs : elle reste N/D hors NVIDIA.
        private void RefreshGpuGeneric()
        {
            try
            {
                if (!gpuInfoRead) { gpuInfoRead = true; ReadGpuRegistryInfo(); }
                PerformanceCounterCategory category = new PerformanceCounterCategory("GPU Engine");
                InstanceDataCollection utilization = category.ReadCategory()["Utilization Percentage"];
                if (utilization == null) return;
                Dictionary<string, CounterSample> next = new Dictionary<string, CounterSample>();
                Dictionary<string, double> sums = new Dictionary<string, double>();
                foreach (InstanceData inst in utilization.Values)
                {
                    next[inst.InstanceName] = inst.Sample;
                    CounterSample before;
                    if (prevGpuSamples == null || !prevGpuSamples.TryGetValue(inst.InstanceName, out before)) continue;
                    Match m = Regex.Match(inst.InstanceName, @"luid_(0x[0-9a-fA-F]+_0x[0-9a-fA-F]+).*engtype_(\w+)");
                    if (!m.Success) continue;
                    string key = m.Groups[1].Value + "/" + m.Groups[2].Value;
                    double v = CounterSample.Calculate(before, inst.Sample);
                    double old;
                    sums.TryGetValue(key, out old);
                    sums[key] = old + v;
                }
                bool firstSample = prevGpuSamples == null;
                prevGpuSamples = next;
                if (firstSample) return; // il faut deux mesures pour obtenir un pourcentage
                double max = 0;
                foreach (double v in sums.Values) max = Math.Max(max, v);
                gpuUsage = Math.Max(0, Math.Min(100, max));
                gpuTemp = null;
            }
            catch (Exception) { /* compteurs indisponibles : GPU affiché N/D */ }
        }

        private void ReadGpuRegistryInfo()
        {
            try
            {
                using (Microsoft.Win32.RegistryKey cls = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}"))
                {
                    if (cls == null) return;
                    foreach (string name in cls.GetSubKeyNames())
                    {
                        using (Microsoft.Win32.RegistryKey k = cls.OpenSubKey(name))
                        {
                            if (k == null) continue;
                            string desc = k.GetValue("DriverDesc") as string;
                            if (string.IsNullOrEmpty(desc) || desc.IndexOf("Basic", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                            gpuName = desc;
                            object mem = k.GetValue("HardwareInformation.qwMemorySize");
                            if (mem is long) gpuVram = (double)(long)mem;
                            else if (mem is byte[] && ((byte[])mem).Length >= 8) gpuVram = BitConverter.ToInt64((byte[])mem, 0);
                            if (gpuName != null) return;
                        }
                    }
                }
            }
            catch (Exception) { }
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
            long now = NowMs();
            if (now - lastGpuRefresh < 3000) return;
            if (Interlocked.CompareExchange(ref gpuBusy, 1, 0) != 0) return;
            lastGpuRefresh = now;
            Task.Run(delegate { try { RefreshGpu(); } finally { Interlocked.Exchange(ref gpuBusy, 0); } });
        }

        // Température CPU, dans l'ordre : (1) HWiNFO64 s'il tourne à côté (mémoire partagée, lecture seule,
        // nécessite que System Pulse tourne aussi en administrateur — voir HwInfoBridge.cs) ; sinon (2) les zones
        // thermiques ACPI exposées par la carte mère (root\WMI, MSAcpi_ThermalZoneTemperature), gratuites et sans
        // droits mais souvent absentes sur les PC de bureau (capteurs tiers non décrits à l'ACPI) : "N/D" alors,
        // honnêtement, plutôt qu'une fausse mesure. Pas d'autre alternative sans dépendre d'un pilote que Windows
        // bloque désormais par défaut (liste des pilotes vulnérables) : voir la note dans le README.
        private void RefreshCpuTemp()
        {
#if TESTHOOKS
            string forced = Environment.GetEnvironmentVariable("SYSTEMPULSE_CPU_TEMP_C");
            if (!string.IsNullOrEmpty(forced))
            {
                double v;
                cpuTemp = double.TryParse(forced, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v) ? (double?)v : null;
                cpuTempFromHwInfo = false;
                return;
            }
            if (Environment.GetEnvironmentVariable("SYSTEMPULSE_CPU_TEMP_MODE") == "unsupported") { cpuTemp = null; cpuTempFromHwInfo = false; cpuTempUnsupported = true; return; }
            if (Environment.GetEnvironmentVariable("SYSTEMPULSE_CPU_TEMP_MODE") == "no-hwinfo") { cpuTempFromHwInfo = false; RefreshCpuTempAcpi(); return; }
            // Simule HWiNFO détecté mais illisible (droits insuffisants), sans dépendre d'une vraie installation.
            if (Environment.GetEnvironmentVariable("SYSTEMPULSE_CPU_TEMP_MODE") == "hwinfo-denied")
            { cpuTemp = null; cpuTempFromHwInfo = false; cpuTempUnsupported = true; forcedHwInfoStatus = "accessDenied"; return; }
#endif
            double? fromHwInfo = hwinfo.TryReadCpuTemperature();
            if (fromHwInfo.HasValue) { cpuTemp = fromHwInfo; cpuTempFromHwInfo = true; return; }
            cpuTempFromHwInfo = false;
            RefreshCpuTempAcpi();
        }

        private void RefreshCpuTempAcpi()
        {
            if (cpuTempUnsupported) return;
            try
            {
                using (ManagementObjectSearcher s = new ManagementObjectSearcher(@"root\WMI", "SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature"))
                using (ManagementObjectCollection zones = s.Get())
                {
                    double? max = null;
                    foreach (ManagementObject zone in zones)
                    {
                        double tenthsKelvin = Convert.ToDouble(zone["CurrentTemperature"]);
                        double celsius = tenthsKelvin / 10.0 - 273.15;
                        if (celsius < -40 || celsius > 130) continue; // capteur aberrant : ignoré plutôt qu'affiché
                        if (!max.HasValue || celsius > max.Value) max = celsius;
                    }
                    cpuTemp = max;
                    if (!max.HasValue) cpuTempUnsupported = true; // zones présentes mais aucune valeur plausible
                }
            }
            catch (Exception)
            {
                cpuTempUnsupported = true; // classe WMI absente : carte mère qui n'expose pas la température (fréquent)
                cpuTemp = null;
            }
        }

        private void KickCpuTemp()
        {
            long now = NowMs();
            if (now - lastCpuTempRefresh < 5000) return;
            if (Interlocked.CompareExchange(ref cpuTempBusy, 1, 0) != 0) return;
            lastCpuTempRefresh = now;
            Task.Run(delegate { try { RefreshCpuTemp(); } finally { Interlocked.Exchange(ref cpuTempBusy, 0); } });
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
            KickCpuTemp();

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
            cpu["temperature"] = NullableRound1(cpuTemp);
            root["cpu"] = cpu;

            Dictionary<string, object> gpu = new Dictionary<string, object>();
            gpu["value"] = NullableRound1(gpuUsage); gpu["name"] = gpuName; gpu["temperature"] = NullableRound1(gpuTemp); gpu["vram"] = gpuVram.HasValue ? (object)gpuVram.Value : null;
            root["gpu"] = gpu;

            Dictionary<string, object> ram = new Dictionary<string, object>();
            ram["value"] = memTotal > 0 ? Round1(memUsed / memTotal * 100.0) : null; ram["used"] = memUsed; ram["total"] = memTotal; ram["available"] = memAvail;
            root["ram"] = ram;

            Dictionary<string, object> temps = new Dictionary<string, object>();
            temps["cpu"] = NullableRound1(cpuTemp);
            temps["cpuSource"] = cpuTemp.HasValue ? (cpuTempFromHwInfo ? "hwinfo" : "acpi") : null;
            temps["gpu"] = NullableRound1(gpuTemp);
            // "accessDenied" : HWiNFO tourne mais sa mémoire partagée exige que System Pulse tourne aussi en
            // administrateur (voir HwInfoBridge.cs) — sert à guider l'utilisateur plutôt qu'un simple "N/D".
            temps["cpuHwInfoStatus"] = forcedHwInfoStatus ?? (hwinfo.Status.ToString().Substring(0, 1).ToLowerInvariant() + hwinfo.Status.ToString().Substring(1));
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

        public void Dispose() { hwinfo.Dispose(); }
    }
}
