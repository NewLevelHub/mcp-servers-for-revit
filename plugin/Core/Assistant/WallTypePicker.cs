using System;
using Newtonsoft.Json.Linq;

namespace revit_mcp_plugin.Core.Assistant
{
    /// <summary>
    /// Pick a usable Basic/partition wall typeId — never curtain / витраж (REV-120 smoke).
    /// </summary>
    public static class WallTypePicker
    {
        public static string TypeBlob(JObject o)
        {
            if (o == null) return "";
            return string.Join(" ",
                o["category"]?.ToString() ?? "",
                o["Category"]?.ToString() ?? "",
                o["familyName"]?.ToString() ?? "",
                o["FamilyName"]?.ToString() ?? "",
                o["name"]?.ToString() ?? "",
                o["Name"]?.ToString() ?? "",
                o["typeName"]?.ToString() ?? "");
        }

        public static bool IsCurtainOrGlazing(string blob)
        {
            if (string.IsNullOrEmpty(blob)) return false;
            return blob.IndexOf("витраж", StringComparison.OrdinalIgnoreCase) >= 0
                || blob.IndexOf("curtain", StringComparison.OrdinalIgnoreCase) >= 0
                || blob.IndexOf("glazing", StringComparison.OrdinalIgnoreCase) >= 0
                || blob.IndexOf("витрин", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>True for Basic/partition walls suitable for create_line_based_element layouts.</summary>
        public static bool IsLikelyBasicWall(JObject o)
        {
            if (o == null) return false;
            var blob = TypeBlob(o);
            if (IsCurtainOrGlazing(blob))
                return false;

            return blob.IndexOf("Wall", StringComparison.OrdinalIgnoreCase) >= 0
                || blob.IndexOf("OST_Walls", StringComparison.OrdinalIgnoreCase) >= 0
                || blob.IndexOf("Стен", StringComparison.OrdinalIgnoreCase) >= 0
                || blob.IndexOf("перегород", StringComparison.OrdinalIgnoreCase) >= 0
                || blob.IndexOf("базовая", StringComparison.OrdinalIgnoreCase) >= 0
                || blob.IndexOf("basic wall", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// Higher = better default for room layout walls.
        /// Curtain = 0; generic wall = 1; basic/partition = 3; thin partition keywords = 4.
        /// </summary>
        public static int Rank(JObject o)
        {
            if (o == null) return 0;
            var blob = TypeBlob(o);
            if (IsCurtainOrGlazing(blob))
                return 0;
            if (!IsLikelyBasicWall(o))
                return 0;

            var rank = 1;
            if (blob.IndexOf("базовая", StringComparison.OrdinalIgnoreCase) >= 0
                || blob.IndexOf("basic wall", StringComparison.OrdinalIgnoreCase) >= 0
                || blob.IndexOf("перегород", StringComparison.OrdinalIgnoreCase) >= 0
                || blob.IndexOf("partition", StringComparison.OrdinalIgnoreCase) >= 0
                || blob.IndexOf("газоблок", StringComparison.OrdinalIgnoreCase) >= 0
                || blob.IndexOf("гкл", StringComparison.OrdinalIgnoreCase) >= 0)
                rank = 3;

            if (blob.IndexOf("перегород", StringComparison.OrdinalIgnoreCase) >= 0
                || blob.IndexOf("partition", StringComparison.OrdinalIgnoreCase) >= 0
                || blob.IndexOf("гкл", StringComparison.OrdinalIgnoreCase) >= 0)
                rank = 4;

            return rank;
        }

        public static long? TryGetTypeId(JObject o)
        {
            if (o == null) return null;
            var id = o["typeId"] ?? o["TypeId"] ?? o["FamilyTypeId"] ?? o["familyTypeId"] ?? o["id"] ?? o["Id"];
            if (id != null && long.TryParse(id.ToString(), out var n) && n > 0)
                return n;
            return null;
        }
    }
}
