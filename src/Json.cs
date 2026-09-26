using System;
using System.Collections.Generic;
using System.Web.Script.Serialization;

namespace SystemPulse
{
    // Petit utilitaire JSON (sérialiseur intégré à .NET Framework, aucune dépendance).
    internal static class Json
    {
        private static readonly JavaScriptSerializer Serializer = new JavaScriptSerializer { MaxJsonLength = 8 * 1024 * 1024 };

        public static string Serialize(object value)
        {
            return Serializer.Serialize(value);
        }

        public static Dictionary<string, object> ParseObject(string text)
        {
            object parsed = Serializer.DeserializeObject(text);
            return parsed as Dictionary<string, object>;
        }

        public static object Get(Dictionary<string, object> map, string key)
        {
            object value;
            if (map != null && map.TryGetValue(key, out value)) return value;
            return null;
        }

        public static string GetString(Dictionary<string, object> map, string key)
        {
            object value = Get(map, key);
            return value == null ? null : Convert.ToString(value);
        }

        public static bool GetBool(Dictionary<string, object> map, string key, bool fallback)
        {
            object value = Get(map, key);
            if (value is bool) return (bool)value;
            return fallback;
        }

        public static double? GetNumber(Dictionary<string, object> map, string key)
        {
            object value = Get(map, key);
            if (value == null || value is bool || value is string) return null;
            try { return Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture); }
            catch (Exception) { return null; }
        }

        public static Dictionary<string, object> GetObject(Dictionary<string, object> map, string key)
        {
            return Get(map, key) as Dictionary<string, object>;
        }
    }
}
