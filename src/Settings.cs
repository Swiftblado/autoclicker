using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace AutoClicker
{
    /// <summary>Tiny key=value settings file stored in %APPDATA%\AutoClicker\settings.ini.</summary>
    internal sealed class Settings
    {
        readonly Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        static string FilePath
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "AutoClicker", "settings.ini");
            }
        }

        public static Settings Load()
        {
            var settings = new Settings();
            try
            {
                if (File.Exists(FilePath))
                {
                    foreach (var line in File.ReadAllLines(FilePath))
                    {
                        int eq = line.IndexOf('=');
                        if (eq > 0) settings.values[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
                    }
                }
            }
            catch (Exception) { /* fall back to defaults */ }
            return settings;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                var lines = new List<string>();
                foreach (var pair in values) lines.Add(pair.Key + "=" + pair.Value);
                File.WriteAllLines(FilePath, lines);
            }
            catch (Exception) { /* settings are a convenience; ignore write failures */ }
        }

        public void Set(string key, object value)
        {
            values[key] = Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        public decimal GetDecimal(string key, decimal fallback)
        {
            string raw;
            decimal result;
            if (values.TryGetValue(key, out raw) &&
                decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out result))
                return result;
            return fallback;
        }

        public int GetInt(string key, int fallback)
        {
            return (int)GetDecimal(key, fallback);
        }

        public bool GetBool(string key, bool fallback)
        {
            string raw;
            bool result;
            if (values.TryGetValue(key, out raw) && bool.TryParse(raw, out result)) return result;
            return fallback;
        }
    }
}
