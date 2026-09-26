using System;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;

namespace SystemPulse
{
    // Vérifie s'il existe une version plus récente : une seule requête GET vers version.json, rien n'est envoyé.
    internal static class UpdateCheck
    {
        public const string DefaultUrl = "https://system-pulse-alpha.vercel.app/version.json";
        // Seules ces adresses peuvent être ouvertes depuis l'interface (le lien vient d'un fichier distant).
        private static readonly string[] AllowedPrefixes =
        {
            "https://github.com/lucaschoremskim-hub/system-pulse/",
            "https://system-pulse-alpha.vercel.app/"
        };

        public static bool IsAllowedLink(string url)
        {
            if (string.IsNullOrEmpty(url)) return false;
            foreach (string p in AllowedPrefixes)
                if (url.StartsWith(p, StringComparison.Ordinal)) return true;
            return false;
        }

        // Renvoie {version, url, notes} si une version plus récente existe, sinon null (y compris hors ligne).
        public static Task<Dictionary<string, object>> CheckAsync(string url, Version current)
        {
            return Task.Run(delegate
            {
                try
                {
                    ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                    HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
                    req.Timeout = 6000;
                    req.UserAgent = "SystemPulse/" + current;
                    using (WebResponse resp = req.GetResponse())
                    using (System.IO.StreamReader r = new System.IO.StreamReader(resp.GetResponseStream()))
                    {
                        Dictionary<string, object> info = Json.ParseObject(r.ReadToEnd());
                        Version latest;
                        if (!Version.TryParse(Json.GetString(info, "version"), out latest) || latest <= current) return null;
                        string link = Json.GetString(info, "url");
                        if (!IsAllowedLink(link)) return null;
                        Dictionary<string, object> result = new Dictionary<string, object>();
                        result["version"] = latest.ToString();
                        result["url"] = link;
                        result["notes"] = Json.GetString(info, "notes") ?? "";
                        return result;
                    }
                }
                catch (Exception) { return null; }
            });
        }
    }
}
