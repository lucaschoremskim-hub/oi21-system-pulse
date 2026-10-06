using System;
using System.Collections.Generic;
using System.Globalization;
using System.Management;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace SystemPulse
{
    internal sealed class DriverRecord
    {
        public string Device;
        public string Manufacturer;
        public string Provider;
        public string InstalledVersion;
        public DateTime? InstalledDate;
        public string DeviceClass;
        public string HardwareId;
        // "unknown" (non vérifié) -> "upToDate" ou "updateAvailable" une fois Windows Update interrogé.
        public string Status = "unknown";
        public string AvailableVersion;
        public string UpdateTitle;
    }

    // État partagé entre le scan (thread d'arrière-plan) et l'interface.
    internal sealed class DriverScanState
    {
        public string Status = "idle"; // idle | scanning | done | error
        public string Error;
        public DateTime? LastScan;
        public List<DriverRecord> Drivers = new List<DriverRecord>();

        public Dictionary<string, object> ToSnapshot()
        {
            List<DriverRecord> drivers = Drivers; // copie de la référence : jamais mutée en place
            List<object> list = new List<object>();
            int outdated = 0;
            foreach (DriverRecord d in drivers)
            {
                if (d.Status == "updateAvailable") outdated += 1;
                Dictionary<string, object> one = new Dictionary<string, object>();
                one["device"] = d.Device;
                one["manufacturer"] = d.Manufacturer;
                one["provider"] = d.Provider;
                one["installedVersion"] = d.InstalledVersion;
                one["installedDate"] = d.InstalledDate.HasValue ? (object)ToUnixMs(d.InstalledDate.Value) : null;
                one["deviceClass"] = d.DeviceClass;
                one["status"] = d.Status;
                one["availableVersion"] = d.AvailableVersion;
                one["updateTitle"] = d.UpdateTitle;
                list.Add(one);
            }
            Dictionary<string, object> root = new Dictionary<string, object>();
            root["status"] = Status;
            root["error"] = Error;
            root["lastScan"] = LastScan.HasValue ? (object)ToUnixMs(LastScan.Value) : null;
            root["drivers"] = list;
            root["outdatedCount"] = outdated;
            root["total"] = drivers.Count;
            return root;
        }

        private static long ToUnixMs(DateTime dt)
        {
            return (long)(dt.ToUniversalTime() - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
        }
    }

    // Scanne les pilotes installés (WMI, Win32_PnPSignedDriver) puis demande à l'agent Windows Update intégré
    // (Microsoft.Update.Session) s'il connaît une version plus récente pour chacun. Aucun service tiers, aucun
    // compte : c'est le même mécanisme que le Windows Update du panneau de configuration. System Pulse ne
    // télécharge ni n'installe rien lui-même ; l'onglet Pilotes renvoie vers Windows Update pour l'installation.
    internal sealed class DriverScanner
    {
        public readonly DriverScanState State = new DriverScanState();
        private int busy;

        // Démarre un scan en arrière-plan ; renvoie false si un scan est déjà en cours (pas d'effet, pas d'erreur).
        public bool Scan(Action<DriverScanState> onDone)
        {
            if (Interlocked.CompareExchange(ref busy, 1, 0) != 0) return false;
            State.Status = "scanning";
            State.Error = null;
            Task.Run(delegate
            {
                try
                {
                    List<DriverRecord> list = ReadDriverList();
                    list.Sort(delegate (DriverRecord a, DriverRecord b) { return string.CompareOrdinal(a.Device ?? "", b.Device ?? ""); });
                    State.Drivers = list;
                    State.Status = "done";
                    State.LastScan = DateTime.Now;
                }
                catch (Exception ex)
                {
                    State.Status = "error";
                    State.Error = ex.Message;
                }
                finally
                {
                    Interlocked.Exchange(ref busy, 0);
                    onDone(State);
                }
            });
            return true;
        }

        private static List<DriverRecord> ReadDriverList()
        {
#if TESTHOOKS
            string fake = Environment.GetEnvironmentVariable("SYSTEMPULSE_DRIVER_FAKE");
            if (!string.IsNullOrEmpty(fake)) return ParseFake(fake);
#endif
            List<DriverRecord> list = ScanInstalled();
            // Windows Update peut être indisponible (hors ligne, service coupé par une politique...) : dans ce
            // cas les pilotes restent "unknown" plutôt que de leur inventer un statut.
            try { CrossReferenceWindowsUpdate(list); } catch (Exception) { }
            return list;
        }

        private static readonly HashSet<string> IgnoredClasses = new HashSet<string>
        {
            "VOLUME", "VOLUMESNAPSHOT", "LEGACYDRIVER", "SOFTWARECOMPONENT", "PRINTQUEUE", "SOFTWAREDEVICE"
        };

        private static List<DriverRecord> ScanInstalled()
        {
            List<DriverRecord> list = new List<DriverRecord>();
            using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(
                "SELECT DeviceName, Manufacturer, DriverProviderName, DriverVersion, DriverDate, DeviceClass, HardWareID FROM Win32_PnPSignedDriver"))
            using (ManagementObjectCollection results = searcher.Get())
            {
                foreach (ManagementObject item in results)
                {
                    try
                    {
                        string device = item["DeviceName"] as string;
                        if (string.IsNullOrWhiteSpace(device)) continue;
                        string deviceClass = item["DeviceClass"] as string;
                        if (deviceClass != null && IgnoredClasses.Contains(deviceClass.ToUpperInvariant())) continue;

                        DriverRecord record = new DriverRecord();
                        record.Device = device;
                        record.Manufacturer = item["Manufacturer"] as string;
                        record.Provider = item["DriverProviderName"] as string;
                        record.InstalledVersion = item["DriverVersion"] as string;
                        record.DeviceClass = deviceClass;
                        record.HardwareId = item["HardWareID"] as string;
                        string driverDate = item["DriverDate"] as string;
                        if (!string.IsNullOrEmpty(driverDate))
                        {
                            try { record.InstalledDate = ManagementDateTimeConverter.ToDateTime(driverDate); }
                            catch (Exception) { }
                        }
                        list.Add(record);
                    }
                    catch (Exception) { /* une entrée WMI illisible ne doit pas interrompre le scan */ }
                    finally { item.Dispose(); }
                }
            }
            return list;
        }

#if TESTHOOKS
        // Scan simulé pour les tests d'interface : déterministe, sans WMI ni réseau.
        private static List<DriverRecord> ParseFake(string json)
        {
            List<DriverRecord> list = new List<DriverRecord>();
            object[] items = Json.ParseArray(json);
            if (items == null) return list;
            foreach (object o in items)
            {
                Dictionary<string, object> d = o as Dictionary<string, object>;
                if (d == null) continue;
                DriverRecord r = new DriverRecord();
                r.Device = Json.GetString(d, "device");
                r.Manufacturer = Json.GetString(d, "manufacturer");
                r.Provider = Json.GetString(d, "provider");
                r.InstalledVersion = Json.GetString(d, "installedVersion");
                r.DeviceClass = Json.GetString(d, "deviceClass");
                r.Status = Json.GetString(d, "status") ?? "unknown";
                r.AvailableVersion = Json.GetString(d, "availableVersion");
                r.UpdateTitle = Json.GetString(d, "updateTitle");
                list.Add(r);
            }
            return list;
        }
#endif

        // L'agent Windows Update expose, pour les mises à jour de type pilote, des propriétés dédiées
        // (DriverHardwareID, DriverProvider, DriverVerDate...) directement sur IUpdate. On y accède par
        // réflexion COM tardive pour ne dépendre d'aucune bibliothèque de types supplémentaire.
        private static void CrossReferenceWindowsUpdate(List<DriverRecord> installed)
        {
            Type sessionType = Type.GetTypeFromProgID("Microsoft.Update.Session");
            if (sessionType == null) return; // agent Windows Update absent : les pilotes restent "unknown"
            object session = Activator.CreateInstance(sessionType);
            object searcher = Invoke(session, "CreateUpdateSearcher");
            Set(searcher, "Online", true);
            object result = Invoke(searcher, "Search", "IsInstalled=0 and Type='Driver' and IsHidden=0");
            object updates = Get(result, "Updates");
            int count = Convert.ToInt32(Get(updates, "Count"), CultureInfo.InvariantCulture);
            bool readable = true; // passe à faux si même une seule entrée n'a pas pu être lue (propriétés COM inattendues)
            for (int i = 0; i < count; i++)
            {
                try { ApplyUpdateMatch(installed, Invoke(updates, "Item", i)); }
                catch (Exception) { readable = false; /* une entrée illisible ne doit pas interrompre les autres */ }
            }
            if (!readable) return; // lecture partiellement en échec : on ne sait pas vraiment qui est à jour, donc "unknown" partout
            // La recherche a réussi et couvre l'ensemble des pilotes : ceux qu'elle n'a pas signalés sont donc
            // à jour au regard de cette source (pas une vérification auprès de chaque fabricant).
            foreach (DriverRecord d in installed) if (d.Status == "unknown") d.Status = "upToDate";
        }

        private static void ApplyUpdateMatch(List<DriverRecord> installed, object update)
        {
            string hwid = Get(update, "DriverHardwareID") as string;
            if (string.IsNullOrEmpty(hwid)) return;
            DriverRecord match = installed.Find(delegate (DriverRecord d)
            {
                return !string.IsNullOrEmpty(d.HardwareId) &&
                    (d.HardwareId.IndexOf(hwid, StringComparison.OrdinalIgnoreCase) >= 0 ||
                     hwid.IndexOf(d.HardwareId, StringComparison.OrdinalIgnoreCase) >= 0);
            });
            if (match == null) return; // pas de pilote installé correspondant : pas de correspondance inventée
            match.Status = "updateAvailable";
            match.UpdateTitle = Get(update, "Title") as string;
            string provider = Get(update, "DriverProvider") as string;
            if (!string.IsNullOrEmpty(provider)) match.Provider = provider;
            object verDate = null;
            try { verDate = Get(update, "DriverVerDate"); } catch (Exception) { }
            if (verDate is DateTime) match.AvailableVersion = ((DateTime)verDate).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        private static object Invoke(object target, string name, params object[] args)
        {
            return target.GetType().InvokeMember(name, BindingFlags.InvokeMethod | BindingFlags.Public | BindingFlags.Instance, null, target, args);
        }

        private static object Get(object target, string name)
        {
            return target.GetType().InvokeMember(name, BindingFlags.GetProperty | BindingFlags.Public | BindingFlags.Instance, null, target, null);
        }

        private static void Set(object target, string name, object value)
        {
            target.GetType().InvokeMember(name, BindingFlags.SetProperty | BindingFlags.Public | BindingFlags.Instance, null, target, new object[] { value });
        }
    }
}
