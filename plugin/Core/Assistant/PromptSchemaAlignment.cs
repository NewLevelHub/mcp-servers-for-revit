using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace revit_mcp_plugin.Core.Assistant
{
    /// <summary>
    /// Validates that prompt/preset instructions only reference tool parameters declared in
    /// <see cref="ToolCatalog"/> (REV-117).
    /// </summary>
    public static class PromptSchemaAlignment
    {
        private static readonly Regex ToolInvocationRegex = new Regex(
            @"\b([a-z][a-z0-9_]*)\s+((?:[a-z][a-z0-9_]*=[^\s,;)]+)(?:\s+(?:[a-z][a-z0-9_]*=[^\s,;)]+))*)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex ParamNameRegex = new Regex(
            @"([a-z][a-z0-9_]*)\s*=",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static IReadOnlyList<string> CollectInstructionTexts()
        {
            var texts = new List<string> { AssistantSystemPrompt.Text };
            foreach (var preset in ScenarioPresets.Pilot)
            {
                if (!string.IsNullOrWhiteSpace(preset.AgentInstruction))
                    texts.Add(preset.AgentInstruction);
            }
            return texts;
        }

        /// <summary>
        /// Returns human-readable violations: tool/param not in catalog schema.
        /// </summary>
        public static IReadOnlyList<string> FindPromptMismatches(IEnumerable<string> instructionTexts)
        {
            var violations = new List<string>();
            var knownTools = new HashSet<string>(
                ToolCatalog.GetOpenAiTools()
                    .OfType<Newtonsoft.Json.Linq.JObject>()
                    .Select(t => t["function"]?["name"]?.ToString())
                    .Where(n => !string.IsNullOrWhiteSpace(n)),
                StringComparer.OrdinalIgnoreCase);

            foreach (var text in instructionTexts ?? Array.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(text))
                    continue;

                foreach (Match match in ToolInvocationRegex.Matches(text))
                {
                    var tool = match.Groups[1].Value;
                    if (!knownTools.Contains(tool))
                        continue;

                    var paramBlob = match.Groups[2].Value;
                    foreach (Match paramMatch in ParamNameRegex.Matches(paramBlob))
                    {
                        var param = paramMatch.Groups[1].Value;
                        var declared = ToolCatalog.GetParameterPropertyNames(tool);
                        if (!declared.Any(p => p.Equals(param, StringComparison.OrdinalIgnoreCase)))
                        {
                            violations.Add(
                                $"Prompt references {tool}.{param} but ToolCatalog schema has: " +
                                (declared.Count == 0 ? "(no properties)" : string.Join(", ", declared)));
                        }
                    }
                }
            }

            return violations;
        }

        public static IReadOnlyList<string> FindEnrichmentMismatches(
            IReadOnlyDictionary<string, IReadOnlyList<string>> keysByTool = null)
        {
            var violations = new List<string>();
            var source = keysByTool ?? NormCheckSchemaKeys.EnrichmentKeysByTool;
            foreach (var kv in source)
            {
                var tool = kv.Key;
                var declared = new HashSet<string>(
                    ToolCatalog.GetParameterPropertyNames(tool),
                    StringComparer.OrdinalIgnoreCase);

                foreach (var key in kv.Value)
                {
                    if (NormCheckSchemaKeys.InternalEnrichmentKeys.Contains(key))
                        continue;
                    if (!declared.Contains(key))
                    {
                        violations.Add(
                            $"NormCheckDefaults.EnrichArgs injects {tool}.{key} but ToolCatalog schema has: " +
                            (declared.Count == 0 ? "(no properties)" : string.Join(", ", declared)));
                    }
                }
            }

            return violations;
        }
    }
}
