using System;
using System.Globalization;
using System.IO;
using Newtonsoft.Json.Linq;

namespace revit_mcp_plugin.Utils
{
    /// <summary>
    /// Какая сборка стоит на этой машине.
    /// </summary>
    /// <remarks>
    /// version.json пишет апдейтер после каждой успешной установки, deploy-local.ps1 —
    /// после ручного развёртывания. Без этой отметки жалобу нельзя отличить от «у него
    /// просто старая сборка»: исправление уезжает в релиз, а на машине остаётся то, что
    /// было, и оба случая выглядят в отчёте одинаково.
    /// </remarks>
    public static class BuildVersion
    {
        private static string _cached;

        /// <summary>Версия для показа человеку: «v1.4.2 от 17.08.2026» или «dev».</summary>
        public static string Current
        {
            get
            {
                if (_cached == null)
                    _cached = Read();
                return _cached;
            }
        }

        private static string Read()
        {
            try
            {
                var path = Path.Combine(PathManager.GetAppDataDirectoryPath(), "version.json");
                if (!File.Exists(path))
                    return "dev";

                var stamp = JObject.Parse(File.ReadAllText(path));
                var version = stamp["version"]?.ToString();
                if (string.IsNullOrEmpty(version))
                    return "dev";

                var installedAt = stamp["installedAt"]?.ToString();
                DateTimeOffset installed;
                if (!string.IsNullOrEmpty(installedAt) &&
                    DateTimeOffset.TryParse(installedAt, CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind, out installed))
                {
                    return version + " от " + installed.LocalDateTime.ToString("dd.MM.yyyy");
                }

                return version;
            }
            catch
            {
                return "dev";
            }
        }
    }
}
