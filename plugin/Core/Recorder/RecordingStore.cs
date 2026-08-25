using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace revit_mcp_plugin.Core.Recorder
{
    /// <summary>
    /// File-backed storage for recorded recipes (REV-177) — one JSON file per recipe, so a
    /// recording survives a Revit restart (the ticket's own acceptance criterion). Same
    /// directory convention as RevitUiCatalog.CatalogDirectory: %USERPROFILE%\.mcp-servers-for-revit\.
    /// The commandset replay command reads these same files directly by path — the shared
    /// directory IS the contract between the two assemblies, no IPC needed since both run
    /// in the same Revit process on the same machine.
    /// </summary>
    public static class RecordingStore
    {
        public static string RecordingsDirectory =>
            OverrideDirectory ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".mcp-servers-for-revit",
                "recordings");

        /// <summary>Подменяется в тестах.</summary>
        public static string OverrideDirectory { get; set; }

        private static string PathFor(string id) => Path.Combine(RecordingsDirectory, $"{id}.json");

        public static void Save(RecordedRecipe recipe)
        {
            if (recipe == null) throw new ArgumentNullException(nameof(recipe));
            if (string.IsNullOrWhiteSpace(recipe.Id)) throw new ArgumentException("recipe.Id required");

            Directory.CreateDirectory(RecordingsDirectory);
            var json = JsonConvert.SerializeObject(recipe, Formatting.Indented);
            File.WriteAllText(PathFor(recipe.Id), json);
        }

        public static RecordedRecipe Load(string id)
        {
            var path = PathFor(id);
            if (!File.Exists(path)) return null;
            var json = File.ReadAllText(path);
            return JsonConvert.DeserializeObject<RecordedRecipe>(json);
        }

        public static List<RecordedRecipe> LoadAll()
        {
            var dir = RecordingsDirectory;
            if (!Directory.Exists(dir)) return new List<RecordedRecipe>();

            var recipes = new List<RecordedRecipe>();
            foreach (var file in Directory.GetFiles(dir, "*.json"))
            {
                try
                {
                    var recipe = JsonConvert.DeserializeObject<RecordedRecipe>(File.ReadAllText(file));
                    if (recipe != null) recipes.Add(recipe);
                }
                catch
                {
                    // A corrupt file must not take down the whole list.
                }
            }
            return recipes.OrderByDescending(r => r.RecordedUtc).ToList();
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
