using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace revit_mcp_plugin.Core.Assistant
{
    /// <summary>
    /// REV-178: user-saved chat scenarios, persisted so they survive a Revit restart — same
    /// %USERPROFILE%\.mcp-servers-for-revit\ directory convention as RevitUiCatalog.CatalogDirectory
    /// and RecordingStore.RecordingsDirectory, one JSON file per scenario.
    ///
    /// A saved scenario is a plain ScenarioPreset with IsUserCreated:true and Profiles:null — it
    /// runs through the exact same Chip_Click / RunPresetImmediate / StartRunAsync path as a
    /// built-in Pilot preset, which is what makes TutorMode.ResolveProfiles' protection apply to
    /// it automatically (REV-154): that method overrides whatever Profiles a chip asks for —
    /// including null — the moment tutor mode is on. There is deliberately no separate execution
    /// path here to keep that guarantee intact.
    /// </summary>
    public static class UserScenarioStore
    {
        public static string ScenariosDirectory =>
            OverrideDirectory ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".mcp-servers-for-revit",
                "scenarios");

        /// <summary>Подменяется в тестах.</summary>
        public static string OverrideDirectory { get; set; }

        private static string PathFor(string id) => Path.Combine(ScenariosDirectory, $"{id}.json");

        /// <summary>
        /// Generates a fresh id, guarding — belt and suspenders — against ever colliding with a
        /// built-in Pilot id even if one changes shape in the future (the acceptance criterion:
        /// "встроенные пресеты не ломаются и не перезаписываются пользовательскими").
        /// </summary>
        public static string NewId()
        {
            string id;
            do
            {
                id = "user_" + Guid.NewGuid().ToString("N").Substring(0, 12);
            } while (ScenarioPresets.Pilot.Any(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase)));
            return id;
        }

        public static ScenarioPreset Save(string label, string prompt, string description)
        {
            if (string.IsNullOrWhiteSpace(label)) throw new ArgumentException("label required");
            if (string.IsNullOrWhiteSpace(prompt)) throw new ArgumentException("prompt required");

            var preset = new ScenarioPreset
            {
                Id = NewId(),
                Label = label.Trim(),
                Icon = "⭐",
                Prompt = prompt.Trim(),
                Hint = string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
                Profiles = null,
                IsUserCreated = true,
                CreatedUtc = DateTime.UtcNow,
            };

            Directory.CreateDirectory(ScenariosDirectory);
            File.WriteAllText(PathFor(preset.Id), JsonConvert.SerializeObject(preset, Formatting.Indented));
            return preset;
        }

        public static List<ScenarioPreset> LoadAll()
        {
            var dir = ScenariosDirectory;
            if (!Directory.Exists(dir)) return new List<ScenarioPreset>();

            var presets = new List<ScenarioPreset>();
            foreach (var file in Directory.GetFiles(dir, "*.json"))
            {
                try
                {
                    var preset = JsonConvert.DeserializeObject<ScenarioPreset>(File.ReadAllText(file));
                    // A file a Pilot id was somehow saved under (hand-edited, or an id collision
                    // from a version before NewId() guarded against it) is never loaded as a
                    // user scenario — it would silently shadow the built-in chip of the same id.
                    if (preset != null && !ScenarioPresets.Pilot.Any(p => p.Id == preset.Id))
                    {
                        preset.IsUserCreated = true;
                        presets.Add(preset);
                    }
                }
                catch
                {
                    // A corrupt file must not take down the whole list.
                }
            }
            return presets.OrderBy(p => p.CreatedUtc).ToList();
        }

        public static bool Rename(string id, string newLabel)
        {
            if (string.IsNullOrWhiteSpace(newLabel)) throw new ArgumentException("newLabel required");
            var path = PathFor(id);
            if (!File.Exists(path)) return false;

            var preset = JsonConvert.DeserializeObject<ScenarioPreset>(File.ReadAllText(path));
            if (preset == null) return false;

            preset.Label = newLabel.Trim();
            File.WriteAllText(path, JsonConvert.SerializeObject(preset, Formatting.Indented));
            return true;
        }

        public static bool Delete(string id)
        {
            var path = PathFor(id);
            if (!File.Exists(path)) return false;
            File.Delete(path);
            return true;
        }
    }
}
