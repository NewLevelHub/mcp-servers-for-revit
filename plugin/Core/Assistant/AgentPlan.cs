using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;

namespace revit_mcp_plugin.Core.Assistant
{
    /// <summary>One step in a declare_plan checklist (REV-120).</summary>
    public sealed class AgentPlanStep
    {
        public int N { get; set; }
        public string What { get; set; }
        public string Tool { get; set; }
        /// <summary>pending | done | failed | skipped</summary>
        public string Status { get; set; } = "pending";
    }

    /// <summary>Immutable-ish snapshot raised to the UI when the plan changes.</summary>
    public sealed class AgentPlanSnapshot
    {
        public string Goal { get; set; }
        public IReadOnlyList<AgentPlanStep> Steps { get; set; } = Array.Empty<AgentPlanStep>();
    }

    /// <summary>
    /// Parse / track / serialize <c>declare_plan</c> meta-tool (REV-120).
    /// Does not change the Revit model — only intention for the model and UI checklist.
    /// </summary>
    public sealed class AgentPlan
    {
        public string Goal { get; private set; }
        public IList<AgentPlanStep> Steps { get; } = new List<AgentPlanStep>();

        public AgentPlanSnapshot Snapshot()
        {
            return new AgentPlanSnapshot
            {
                Goal = Goal,
                Steps = Steps.Select(s => new AgentPlanStep
                {
                    N = s.N,
                    What = s.What,
                    Tool = s.Tool,
                    Status = s.Status,
                }).ToList(),
            };
        }

        public static bool TryParse(string argsJson, out AgentPlan plan, out string error)
        {
            plan = null;
            error = null;
            try
            {
                var jo = JObject.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson);
                var goal = jo["goal"]?.ToString()?.Trim();
                var stepsTok = jo["steps"] as JArray;
                if (string.IsNullOrWhiteSpace(goal))
                {
                    error = "Нужен goal — краткая цель запроса.";
                    return false;
                }

                if (stepsTok == null || stepsTok.Count == 0)
                {
                    error = "Нужен steps[] — хотя бы один шаг.";
                    return false;
                }

                plan = new AgentPlan { Goal = goal };
                var n = 1;
                foreach (var tok in stepsTok)
                {
                    var s = tok as JObject;
                    if (s == null) continue;
                    var what = (s["what"] ?? s["What"])?.ToString()?.Trim();
                    if (string.IsNullOrWhiteSpace(what))
                        what = "Шаг " + n;
                    var tool = (s["tool"] ?? s["Tool"])?.ToString()?.Trim();
                    var stepN = s["n"]?.Value<int?>() ?? s["N"]?.Value<int?>() ?? n;
                    plan.Steps.Add(new AgentPlanStep
                    {
                        N = stepN,
                        What = what,
                        Tool = tool,
                        Status = "pending",
                    });
                    n++;
                }

                if (plan.Steps.Count == 0)
                {
                    error = "steps[] пуст или без what.";
                    plan = null;
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                error = "Не удалось разобрать план: " + ex.Message;
                return false;
            }
        }

        /// <summary>Local tool execution for Socket bypass / golden stub path.</summary>
        public static string ExecuteAsTool(string argsJson)
        {
            if (!TryParse(argsJson, out var plan, out var error))
            {
                return ToolResultShaper.FailurePayload(new ToolCatalog.FailureHint(
                    error ?? "Некорректный план.",
                    "Передай goal и steps[{n,what,tool}].")).ToString();
            }

            return plan.ToSuccessPayload().ToString();
        }

        public JObject ToSuccessPayload()
        {
            var items = new JArray();
            foreach (var s in Steps)
            {
                items.Add(new JObject
                {
                    ["n"] = s.N,
                    ["what"] = s.What,
                    ["tool"] = s.Tool ?? "",
                    ["status"] = s.Status,
                });
            }

            var first = Steps.FirstOrDefault(x => x.Status == "pending") ?? Steps[0];
            return new JObject
            {
                ["ok"] = true,
                ["summary"] = $"План ({Steps.Count} шагов): {Goal}",
                ["count"] = Steps.Count,
                ["goal"] = Goal,
                ["items"] = items,
                ["nextStep"] = $"Выполни шаг {first.N}: {first.What}" +
                               (string.IsNullOrWhiteSpace(first.Tool) ? "" : $" ({first.Tool})"),
            };
        }

        /// <summary>
        /// Mark a plan step for this tool. Success can recover a previous failed attempt
        /// (e.g. door typeId wrong once, then create_point_based_element succeeds).
        /// </summary>
        public bool TryMarkTool(string toolName, bool ok)
        {
            if (Steps.Count == 0) return false;
            var canonical = ToolCatalog.ResolveToolAlias(toolName ?? "");

            // Prefer first pending matching step.
            var match = FindStep(canonical, pendingOnly: true);
            if (match == null && ok)
            {
                // Later success after a failed attempt → flip ✗ to ✓.
                match = FindStep(canonical, pendingOnly: false, statusEquals: "failed");
            }

            if (match == null)
                return false;

            // Don't downgrade an already-done step on a later unrelated failure of same tool.
            if (!ok && string.Equals(match.Status, "done", StringComparison.OrdinalIgnoreCase))
                return false;

            match.Status = ok ? "done" : "failed";
            return true;
        }

        private AgentPlanStep FindStep(string canonicalTool, bool pendingOnly, string statusEquals = null)
        {
            foreach (var s in Steps)
            {
                if (pendingOnly
                    && !string.Equals(s.Status, "pending", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!pendingOnly && statusEquals != null
                    && !string.Equals(s.Status, statusEquals, StringComparison.OrdinalIgnoreCase))
                    continue;

                // Empty tool must NOT steal the mark from a later concrete step
                // (e.g. "Двери" without tool would get ✓ when create_room succeeds).
                if (string.IsNullOrWhiteSpace(s.Tool))
                    continue;

                var stepTool = ToolCatalog.ResolveToolAlias(s.Tool);
                if (stepTool.Equals(canonicalTool, StringComparison.OrdinalIgnoreCase))
                    return s;
            }

            return null;
        }

        public IEnumerable<AgentPlanStep> PendingSteps() =>
            Steps.Where(s => string.Equals(s.Status, "pending", StringComparison.OrdinalIgnoreCase));

        public IEnumerable<AgentPlanStep> DoneSteps() =>
            Steps.Where(s => string.Equals(s.Status, "done", StringComparison.OrdinalIgnoreCase));

        public static string BuildPartialReply(
            string reason,
            IList<string> doneSummary,
            AgentPlan plan)
        {
            var sb = new StringBuilder();
            sb.Append(reason?.Trim() ?? "Остановлено.");

            var collapsed = CollapseDoneLines(doneSummary);
            if (collapsed.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine();
                sb.Append("Успели: ");
                sb.Append(string.Join(" · ", collapsed));
            }

            if (plan?.Steps != null && plan.Steps.Count > 0)
            {
                var pending = plan.PendingSteps().ToList();
                if (pending.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine();
                    sb.Append("Не успели: ");
                    sb.Append(string.Join("; ", pending.Select(p =>
                        p.N + ". " + p.What)));
                    var next = pending[0];
                    sb.AppendLine();
                    sb.Append("Продолжить с шага ");
                    sb.Append(next.N);
                    sb.Append(": «");
                    sb.Append(next.What);
                    sb.Append("».");
                }
            }

            return sb.ToString().Trim();
        }

        private static IList<string> CollapseDoneLines(IList<string> items)
        {
            var result = new List<string>();
            if (items == null || items.Count == 0)
                return result;

            string current = null;
            var count = 0;
            foreach (var raw in items)
            {
                var item = raw ?? "";
                if (current != null && string.Equals(item, current, StringComparison.Ordinal))
                {
                    count++;
                    continue;
                }

                if (current != null)
                    result.Add(count > 1 ? current + " ×" + count : current);
                current = item;
                count = 1;
            }

            if (current != null)
                result.Add(count > 1 ? current + " ×" + count : current);

            return result;
        }
    }

    /// <summary>Round budget by request complexity (REV-120).</summary>
    public static class RoundBudget
    {
        public const int Simple = 5;
        public const int Normal = 12;
        public const int Complex = 30;

        public static int Resolve(IReadOnlyList<string> profiles, string userText = null)
        {
            var set = new HashSet<string>(
                profiles ?? Array.Empty<string>(),
                StringComparer.OrdinalIgnoreCase);

            if (set.Contains(ToolCatalog.Profiles.Modeling)
                || AssistantPlaybooks.ShouldIncludeTypology(userText))
                return Complex;

            if (set.Contains(ToolCatalog.Profiles.Norms)
                || set.Contains(ToolCatalog.Profiles.Annotation)
                || set.Contains(ToolCatalog.Profiles.Schedules)
                || set.Contains(ToolCatalog.Profiles.Sheets))
                return Normal;

            // Core / data reads — keep short.
            return Simple;
        }
    }

    /// <summary>
    /// Blocks the 3rd identical tool+args call in a turn (REV-120).
    /// Cacheable reads still reuse the 1st result on the 2nd call; the 3rd is blocked.
    /// </summary>
    public sealed class ToolCallLoopGuard
    {
        public const int MaxIdenticalCalls = 2;

        private readonly Dictionary<string, int> _counts =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        public static string Key(string toolName, string argsJson) =>
            (toolName ?? "").Trim().ToLowerInvariant() + "|" + NormalizeArgs(argsJson);

        private static string NormalizeArgs(string argsJson)
        {
            try
            {
                var tok = JToken.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson);
                return tok.ToString(Newtonsoft.Json.Formatting.None);
            }
            catch
            {
                return (argsJson ?? "{}").Trim();
            }
        }

        /// <summary>
        /// Record a call. Returns false when the call should be blocked (3rd+ identical).
        /// </summary>
        public bool TryAllow(string toolName, string argsJson, out int count)
        {
            var key = Key(toolName, argsJson);
            _counts.TryGetValue(key, out count);
            count++;
            _counts[key] = count;
            return count <= MaxIdenticalCalls;
        }

        public static JObject BlockPayload(string toolName)
        {
            return new JObject
            {
                ["ok"] = false,
                ["error"] = "Повторный вызов с теми же аргументами.",
                ["fix"] = "Поменяй подход, аргументы или сообщи о проблеме пользователю.",
                ["tool"] = toolName ?? "",
                ["summary"] = "Цикл: тот же вызов уже был дважды.",
            };
        }
    }
}
