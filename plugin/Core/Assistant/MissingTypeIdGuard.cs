using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace revit_mcp_plugin.Core.Assistant
{
    /// <summary>
    /// REV-121: refuse silent typeId injection. When the model omits typeId on create_*_element,
    /// return a teaching error with candidates from the family-types cache so the next round
    /// picks deliberately. Never fall back to a wall type for doors/windows.
    /// </summary>
    public static class MissingTypeIdGuard
    {
        public const int CandidateLimit = 8;

        public sealed class CheckResult
        {
            public bool Missing;
            public JObject Payload;
            public string Error;
        }

        /// <summary>
        /// If any data item lacks typeId, returns a shaped failure for the model.
        /// Otherwise <see cref="CheckResult.Missing"/> is false and args may proceed unchanged.
        /// </summary>
        public static CheckResult Check(
            Dictionary<string, string> cache,
            string toolName,
            string argsJson)
        {
            if (!IsCreateElementTool(toolName))
                return Ok();

            JObject args;
            try { args = JObject.Parse(argsJson ?? "{}"); }
            catch { return Ok(); }

            var data = args["data"] as JArray;
            if (data == null || data.Count == 0)
                return Ok();

            var missingIndexes = new List<int>();
            for (var i = 0; i < data.Count; i++)
            {
                var item = data[i] as JObject;
                if (item == null) continue;
                if (!HasTypeId(item))
                    missingIndexes.Add(i);
            }

            if (missingIndexes.Count == 0)
                return Ok();

            var kind = ResolveKind(toolName);
            var candidates = CollectCandidates(cache, kind);
            var error = KindError(kind);
            var fix = KindFix(kind);
            var payload = new JObject
            {
                ["ok"] = false,
                ["error"] = error,
                ["fix"] = fix,
                ["missingCount"] = missingIndexes.Count,
                ["missingIndexes"] = new JArray(missingIndexes.Cast<object>().ToArray()),
                ["candidates"] = candidates,
                ["nextStep"] = fix
            };

            return new CheckResult
            {
                Missing = true,
                Payload = payload,
                Error = error
            };
        }

        public static bool IsCreateElementTool(string toolName)
        {
            if (string.IsNullOrWhiteSpace(toolName)) return false;
            return toolName.Equals("create_line_based_element", StringComparison.OrdinalIgnoreCase)
                || toolName.Equals("create_point_based_element", StringComparison.OrdinalIgnoreCase)
                || toolName.Equals("create_surface_based_element", StringComparison.OrdinalIgnoreCase);
        }

        private static CheckResult Ok() => new CheckResult { Missing = false };

        private static bool HasTypeId(JObject item)
        {
            var tid = item["typeId"]?.Value<long?>() ?? item["TypeId"]?.Value<long?>();
            return tid.HasValue && tid.Value > 0;
        }

        private enum TypeKind
        {
            Wall,
            PointHosted,
            Surface
        }

        private static TypeKind ResolveKind(string toolName)
        {
            if (toolName.Equals("create_point_based_element", StringComparison.OrdinalIgnoreCase))
                return TypeKind.PointHosted;
            if (toolName.Equals("create_surface_based_element", StringComparison.OrdinalIgnoreCase))
                return TypeKind.Surface;
            return TypeKind.Wall;
        }

        private static string KindError(TypeKind kind)
        {
            switch (kind)
            {
                case TypeKind.PointHosted:
                    return "Не указан typeId для двери/окна";
                case TypeKind.Surface:
                    return "Не указан typeId для пола/потолка/крыши";
                default:
                    return "Не указан typeId для стены";
            }
        }

        private static string KindFix(TypeKind kind)
        {
            switch (kind)
            {
                case TypeKind.PointHosted:
                    return "Вызови get_available_family_types (OST_Doors / OST_Windows) и повтори create_point_based_element с числовым typeId + hostWallId. Не подставляй typeId стены.";
                case TypeKind.Surface:
                    return "Вызови get_available_family_types (OST_Floors) и повтори create_surface_based_element с числовым typeId.";
                default:
                    return "Вызови get_available_family_types (OST_Walls) и повтори create_line_based_element с typeId из списка candidates (не Витраж).";
            }
        }

        private static JArray CollectCandidates(Dictionary<string, string> cache, TypeKind kind)
        {
            var result = new JArray();
            if (cache == null || cache.Count == 0)
                return result;

            var seen = new HashSet<long>();
            foreach (var type in EnumerateCachedTypes(cache))
            {
                if (!MatchesKind(type, kind))
                    continue;

                var id = WallTypePicker.TryGetTypeId(type);
                if (!id.HasValue || !seen.Add(id.Value))
                    continue;

                result.Add(new JObject
                {
                    ["typeId"] = id.Value,
                    ["name"] = FirstString(type, "name", "Name", "typeName", "TypeName") ?? id.Value.ToString(),
                    ["familyName"] = FirstString(type, "familyName", "FamilyName"),
                    ["category"] = FirstString(type, "category", "Category")
                });

                if (result.Count >= CandidateLimit)
                    break;
            }

            return result;
        }

        private static IEnumerable<JObject> EnumerateCachedTypes(Dictionary<string, string> cache)
        {
            var bag = new List<JObject>();
            foreach (var kv in cache)
            {
                if (kv.Key.IndexOf("get_available_family_types", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                try
                {
                    var token = JToken.Parse(kv.Value);
                    if (token is JObject root && root["result"] != null)
                        token = root["result"];

                    JArray types = token as JArray;
                    if (types == null && token is JObject obj)
                    {
                        types = (obj["types"] ?? obj["Types"] ?? obj["familyTypes"]
                                 ?? obj["items"] ?? obj["Response"]) as JArray;
                    }

                    if (types == null)
                        continue;

                    foreach (var t in types.OfType<JObject>())
                        bag.Add(t);
                }
                catch
                {
                    // ignore bad cache entry
                }
            }

            return bag
                .OrderByDescending(o => kindRank(o))
                .ThenBy(o => FirstString(o, "name", "Name") ?? "");

            int kindRank(JObject o) => WallTypePicker.Rank(o);
        }

        /// <summary>
        /// Point-hosted (doors/windows) must never surface wall types as candidates.
        /// </summary>
        private static bool MatchesKind(JObject type, TypeKind kind)
        {
            var blob = WallTypePicker.TypeBlob(type);
            switch (kind)
            {
                case TypeKind.Wall:
                    return WallTypePicker.Rank(type) > 0;

                case TypeKind.PointHosted:
                    if (WallTypePicker.IsLikelyBasicWall(type) || WallTypePicker.IsCurtainOrGlazing(blob))
                        return false;
                    return blob.IndexOf("Door", StringComparison.OrdinalIgnoreCase) >= 0
                        || blob.IndexOf("Window", StringComparison.OrdinalIgnoreCase) >= 0
                        || blob.IndexOf("OST_Doors", StringComparison.OrdinalIgnoreCase) >= 0
                        || blob.IndexOf("OST_Windows", StringComparison.OrdinalIgnoreCase) >= 0
                        || blob.IndexOf("Двер", StringComparison.OrdinalIgnoreCase) >= 0
                        || blob.IndexOf("Окн", StringComparison.OrdinalIgnoreCase) >= 0
                        || blob.IndexOf("Furniture", StringComparison.OrdinalIgnoreCase) >= 0
                        || blob.IndexOf("Мебел", StringComparison.OrdinalIgnoreCase) >= 0;

                case TypeKind.Surface:
                    if (WallTypePicker.IsLikelyBasicWall(type))
                        return false;
                    return blob.IndexOf("Floor", StringComparison.OrdinalIgnoreCase) >= 0
                        || blob.IndexOf("Ceiling", StringComparison.OrdinalIgnoreCase) >= 0
                        || blob.IndexOf("Roof", StringComparison.OrdinalIgnoreCase) >= 0
                        || blob.IndexOf("OST_Floors", StringComparison.OrdinalIgnoreCase) >= 0
                        || blob.IndexOf("OST_Ceilings", StringComparison.OrdinalIgnoreCase) >= 0
                        || blob.IndexOf("OST_Roofs", StringComparison.OrdinalIgnoreCase) >= 0
                        || blob.IndexOf("Пол", StringComparison.OrdinalIgnoreCase) >= 0
                        || blob.IndexOf("Потол", StringComparison.OrdinalIgnoreCase) >= 0
                        || blob.IndexOf("Кровл", StringComparison.OrdinalIgnoreCase) >= 0;

                default:
                    return false;
            }
        }

        private static string FirstString(JObject o, params string[] names)
        {
            if (o == null) return null;
            foreach (var n in names)
            {
                var t = o[n];
                if (t != null && t.Type != JTokenType.Null)
                {
                    var s = t.ToString();
                    if (!string.IsNullOrWhiteSpace(s))
                        return s;
                }
            }
            return null;
        }
    }
}
