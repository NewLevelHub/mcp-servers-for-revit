using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace revit_mcp_plugin.Core.Assistant
{
    /// <summary>
    /// Port of server applyFireDoorRules for the in-Revit agent.
    /// Revit check_fire_doors returns facts only; Node normally applies PDF rules.
    /// Here we enrich from the offline norm catalog so the LLM gets compliant/reason/source
    /// and can place leaders (выноски), not only SetColor.
    /// </summary>
    public static class FireDoorRulesApplier
    {
        private static readonly Regex FireDoorRequirementRe = new Regex(
            @"противопожарн|самозакрывающ|предел\w*\s+огнестойк|огнестойк\w*\s+двер|\bEI\s*\d{2,3}\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly string[] ScenarioPriority =
        {
            "stair-to-corridor",
            "between-compartments",
            "evacuation-exit",
            "egress-route",
            "fire-compartment-door",
        };

        /// <summary>
        /// If raw JSON-RPC is a successful check_fire_doors facts payload, replace result
        /// with enriched compliance + annotationHints.
        /// </summary>
        public static string EnrichRawResult(string rawResult)
        {
            try
            {
                var jo = JObject.Parse(rawResult);
                if (jo["error"] != null)
                    return rawResult;

                var result = jo["result"] as JObject;
                if (result == null)
                    return rawResult;

                var success = result["Success"]?.Value<bool?>()
                    ?? result["success"]?.Value<bool?>();
                if (success == false)
                    return rawResult;

                // Already enriched by Node-style payload
                if (result["nonCompliantCount"] != null || result["requiredFireDoors"] != null)
                    return rawResult;

                var doorsToken = result["Doors"] ?? result["doors"];
                var doorsArr = doorsToken as JArray;
                if (doorsArr == null || doorsArr.Count == 0)
                    return rawResult;

                var rules = LoadRulesFromCatalog();
                var enriched = Apply(doorsArr, rules);
                jo["result"] = enriched;
                return jo.ToString(Formatting.None);
            }
            catch
            {
                return rawResult;
            }
        }

        private static List<FireDoorNormRule> LoadRulesFromCatalog()
        {
            var cat = NormCatalogStore.GetCatalog();
            var list = new List<FireDoorNormRule>();
            if (cat?.Rules == null)
                return list;

            foreach (var rule in cat.Rules)
            {
                var quote = rule.Source?.Quote ?? "";
                if (quote.IndexOf("двер", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                if (!FireDoorRequirementRe.IsMatch(quote))
                    continue;

                var scenario = InferScenario(quote);
                list.Add(new FireDoorNormRule
                {
                    Id = rule.Id ?? "",
                    Scenario = scenario,
                    Reason = InferReason(scenario),
                    Document = rule.Source?.Document ?? "",
                    Clause = string.IsNullOrWhiteSpace(rule.Source?.Clause)
                        ? ExtractClause(quote)
                        : rule.Source.Clause,
                    Quote = quote
                });
            }

            return list;
        }

        private static JObject Apply(JArray doorsArr, List<FireDoorNormRule> rules)
        {
            var usable = rules
                .Where(r => FireDoorRequirementRe.IsMatch(r.Quote ?? ""))
                .ToList();

            var enrichedDoors = new JArray();
            var requiredCount = 0;
            var nonCompliantCount = 0;
            var hints = new JArray();

            foreach (var token in doorsArr)
            {
                var door = token as JObject ?? new JObject();
                var fromRoom = door["FromRoom"]?.ToString() ?? door["fromRoom"]?.ToString() ?? "";
                var toRoom = door["ToRoom"]?.ToString() ?? door["toRoom"]?.ToString() ?? "";
                var isOnEgress = door["IsOnEgressPath"]?.Value<bool?>()
                    ?? door["isOnEgressPath"]?.Value<bool?>()
                    ?? false;
                var isMarked = door["IsMarkedAsFireDoor"]?.Value<bool?>()
                    ?? door["isMarkedAsFireDoor"]?.Value<bool?>()
                    ?? false;
                var id = door["Id"]?.Value<long?>()
                    ?? door["id"]?.Value<long?>()
                    ?? 0;
                var mark = door["Mark"]?.ToString() ?? door["mark"]?.ToString() ?? "";
                var typeName = door["Type"]?.ToString() ?? door["type"]?.ToString() ?? "";

                var scenarios = DetectScenarios(fromRoom, toRoom, isOnEgress);
                var matched = PickRule(scenarios, usable);
                var requires = matched != null && scenarios.Count > 0;
                var compliant = !requires || isMarked;

                if (requires) requiredCount++;
                if (requires && !compliant) nonCompliantCount++;

                var item = (JObject)door.DeepClone();
                item["requiresFireDoor"] = requires;
                item["ruleId"] = matched?.Id ?? "";
                item["reason"] = matched?.Reason ?? "";
                item["compliant"] = compliant;
                item["source"] = matched == null
                    ? new JObject { ["document"] = "", ["clause"] = "", ["quote"] = "" }
                    : new JObject
                    {
                        ["document"] = matched.Document,
                        ["clause"] = matched.Clause,
                        ["quote"] = Truncate(matched.Quote, 480)
                    };
                enrichedDoors.Add(item);

                if (requires && !compliant && id > 0)
                {
                    var label = !string.IsNullOrWhiteSpace(mark) ? mark : typeName;
                    if (string.IsNullOrWhiteSpace(label)) label = $"Дверь {id}";
                    var doc = matched?.Document ?? "";
                    var clause = matched?.Clause ?? "";
                    var reason = matched?.Reason ?? "Требуется противопожарная дверь";
                    hints.Add(new JObject
                    {
                        ["elementId"] = id,
                        ["text"] = $"{label}: {reason} · {doc} {clause}".Trim(),
                        ["leader"] = true
                    });
                }
            }

            return new JObject
            {
                ["Success"] = true,
                ["success"] = true,
                ["Message"] =
                    $"Проверено {enrichedDoors.Count} дверей по {usable.Count} правилам каталога; " +
                    $"требуется противопожарных: {requiredCount}, несоответствий: {nonCompliantCount}.",
                ["message"] =
                    $"Проверено {enrichedDoors.Count} дверей; несоответствий: {nonCompliantCount}.",
                ["TotalDoors"] = enrichedDoors.Count,
                ["totalDoors"] = enrichedDoors.Count,
                ["requiredFireDoors"] = requiredCount,
                ["nonCompliantCount"] = nonCompliantCount,
                ["Doors"] = enrichedDoors,
                ["doors"] = enrichedDoors,
                ["annotationHints"] = hints,
                ["catalogUsed"] = NormCatalogStore.IsAvailable
            };
        }

        private static List<string> DetectScenarios(string fromRoom, string toRoom, bool isOnEgressPath)
        {
            var scenarios = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var fromEgress = ContainsEgressKeyword(fromRoom) || IsStairwell(fromRoom) || IsVestibule(fromRoom);
            var toEgress = ContainsEgressKeyword(toRoom) || IsStairwell(toRoom) || IsVestibule(toRoom);
            var fromResidential = IsResidential(fromRoom);
            var toResidential = IsResidential(toRoom);
            var fromVestibule = IsVestibule(fromRoom);
            var toVestibule = IsVestibule(toRoom);

            if ((IsStairwell(fromRoom) && (ContainsEgressKeyword(toRoom) || toResidential || toVestibule))
                || (IsStairwell(toRoom) && (ContainsEgressKeyword(fromRoom) || fromResidential || fromVestibule)))
            {
                scenarios.Add("stair-to-corridor");
            }

            if ((fromVestibule || toVestibule)
                && !(fromResidential && toResidential)
                && (isOnEgressPath || fromEgress || toEgress))
            {
                scenarios.Add("between-compartments");
            }

            if (Regex.IsMatch($"{fromRoom} {toRoom}", @"выход|exit", RegexOptions.IgnoreCase)
                && (fromEgress || toEgress))
            {
                scenarios.Add("evacuation-exit");
            }

            return scenarios.ToList();
        }

        private static FireDoorNormRule PickRule(List<string> scenarios, List<FireDoorNormRule> rules)
        {
            if (scenarios == null || scenarios.Count == 0 || rules == null || rules.Count == 0)
                return null;

            foreach (var scenario in ScenarioPriority)
            {
                if (!scenarios.Contains(scenario)) continue;
                var hit = rules.FirstOrDefault(r =>
                    string.Equals(r.Scenario, scenario, StringComparison.OrdinalIgnoreCase));
                if (hit != null) return hit;
            }

            var strong = scenarios.Any(s =>
                s == "stair-to-corridor" || s == "between-compartments" || s == "evacuation-exit");
            return strong ? rules[0] : null;
        }

        private static string InferScenario(string quote)
        {
            var n = (quote ?? "").ToLowerInvariant();
            if (n.Contains("лестничн") && (n.Contains("коридор") || n.Contains("квартир") || n.Contains("холл")))
                return "stair-to-corridor";
            if (Regex.IsMatch(n, @"пожарн\w*\s+отсек|противопожарн\w*\s+преград|противопожарн\w*\s+перегород"))
                return "between-compartments";
            if (Regex.IsMatch(n, @"эвакуационн\w*\s+выход|выход\w*.{0,40}эвакуац"))
                return "evacuation-exit";
            if (Regex.IsMatch(n,
                    @"противопожарн.{0,60}(путь\w*\s+эвакуац|эвакуационн\w*\s+путь|коридор)|(путь\w*\s+эвакуац|эвакуационн\w*\s+путь|коридор).{0,60}противопожарн"))
                return "egress-route";
            return "fire-compartment-door";
        }

        private static string InferReason(string scenario)
        {
            switch (scenario)
            {
                case "egress-route": return "Дверь на пути эвакуации";
                case "between-compartments": return "Дверь между пожарными отсеками / преградой";
                case "stair-to-corridor": return "Дверь между лестничной клеткой и коридором/квартирой";
                case "evacuation-exit": return "Дверь эвакуационного выхода";
                default: return "Требуется противопожарная дверь";
            }
        }

        private static string ExtractClause(string quote)
        {
            var m = Regex.Match(quote ?? "", @"(?:^|\s)(?:п\.?\s*|§\s*)(\d+(?:\.\d+)*)", RegexOptions.IgnoreCase);
            return m.Success ? $"п. {m.Groups[1].Value}" : "";
        }

        private static bool ContainsEgressKeyword(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            var n = value.ToLowerInvariant();
            return n.Contains("коридор") || n.Contains("лест") || n.Contains("эвак")
                || n.Contains("corridor") || n.Contains("stair") || n.Contains("egress") || n.Contains("hall");
        }

        private static bool IsStairwell(string roomName)
        {
            var n = (roomName ?? "").ToLowerInvariant();
            return n.Contains("лестнич") || n.Contains("stair") || n.Contains("лк ");
        }

        private static bool IsVestibule(string roomName)
        {
            var n = (roomName ?? "").ToLowerInvariant();
            return n.Contains("тамбур") || n.Contains("вестиб") || n.Contains("vestibule") || n.Contains("холл");
        }

        private static bool IsResidential(string roomName)
        {
            var n = (roomName ?? "").ToLowerInvariant();
            return n.Contains("квартир") || n.Contains("жил") || n.Contains("комнат")
                || n.Contains("спальн") || n.Contains("гостин") || n.Contains("кухн") || n.Contains("прихож");
        }

        private static string Truncate(string s, int max)
        {
            if (string.IsNullOrEmpty(s) || s.Length <= max) return s ?? "";
            return s.Substring(0, max - 1) + "…";
        }

        private sealed class FireDoorNormRule
        {
            public string Id;
            public string Scenario;
            public string Reason;
            public string Document;
            public string Clause;
            public string Quote;
        }
    }
}
