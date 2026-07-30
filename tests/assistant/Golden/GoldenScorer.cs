using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace revit_mcp_plugin.Tests.Assistant.Golden;

public static class GoldenScorer
{
    private static readonly Dictionary<string, string> Aliases =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["tag_all_rooms"] = "tag_rooms",
            ["tag_all_walls"] = "tag_walls",
            ["color_elements"] = "color_splash",
        };

    public static string Canonical(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "";
        var n = name.Trim();
        return Aliases.TryGetValue(n, out var a) ? a : n;
    }

    public static GoldenCaseResult Score(GoldenCase c, IReadOnlyList<GoldenToolCall> calls, string reply, int promptTokens)
    {
        var result = new GoldenCaseResult
        {
            Id = c.Id,
            Group = c.Group,
            Rounds = calls.Count == 0 ? 1 : calls.Max(x => x.Round) + 1,
            PromptTokens = promptTokens,
            ActualTools = calls.Select(x => Canonical(x.Name)).ToList(),
            Reply = reply ?? "",
        };

        var actual = result.ActualTools;
        var failures = result.Failures;

        // Forbid
        result.ForbidOk = true;
        foreach (var f in c.ForbidTools ?? [])
        {
            var cf = Canonical(f);
            if (actual.Any(a => a.Equals(cf, StringComparison.OrdinalIgnoreCase)))
            {
                result.ForbidOk = false;
                failures.Add($"forbidTools: вызван «{cf}»");
            }
        }

        // No tools
        if (c.ExpectNoTools)
        {
            if (actual.Count > 0)
                failures.Add($"expectNoTools: ожидался отказ без tool, факт: {string.Join(", ", actual)}");
            result.FirstToolCorrect = actual.Count == 0;
            result.RequireArgsOk = true;
            result.Passed = failures.Count == 0;
            return result;
        }

        // First tool
        if (!string.IsNullOrWhiteSpace(c.FirstToolMustBe))
        {
            var want = Canonical(c.FirstToolMustBe);
            var got = actual.FirstOrDefault() ?? "";
            result.FirstToolCorrect = got.Equals(want, StringComparison.OrdinalIgnoreCase);
            if (!result.FirstToolCorrect)
                failures.Add($"firstToolMustBe: ожидали «{want}», факт «{got}»");
        }
        else if (c.ExpectTools is { Count: > 0 })
        {
            var firstExpect = Canonical(c.ExpectTools[0]);
            var got = actual.FirstOrDefault() ?? "";
            // Soft: first expected tool appears as the first call OR as any of the first-round tools
            result.FirstToolCorrect = actual.Count > 0 && (
                got.Equals(firstExpect, StringComparison.OrdinalIgnoreCase)
                || actual.Take(2).Any(a => a.Equals(firstExpect, StringComparison.OrdinalIgnoreCase)));
            if (!result.FirstToolCorrect)
                failures.Add($"first expectTools: ожидали рано «{firstExpect}», цепочка: {string.Join(" → ", actual)}");
        }
        else
        {
            result.FirstToolCorrect = true;
        }

        // Expect tools present
        if (c.ExpectToolsOrdered && c.ExpectTools is { Count: > 0 })
        {
            var idx = 0;
            foreach (var wantRaw in c.ExpectTools)
            {
                var want = Canonical(wantRaw);
                var found = -1;
                for (var i = idx; i < actual.Count; i++)
                {
                    if (actual[i].Equals(want, StringComparison.OrdinalIgnoreCase))
                    {
                        found = i;
                        break;
                    }
                }

                if (found < 0)
                    failures.Add($"expectToolsOrdered: не найден «{want}» после позиции {idx}");
                else
                    idx = found + 1;
            }
        }
        else
        {
            foreach (var wantRaw in c.ExpectTools ?? [])
            {
                var want = Canonical(wantRaw);
                if (!actual.Any(a => a.Equals(want, StringComparison.OrdinalIgnoreCase)))
                    failures.Add($"expectTools: нет «{want}»");
            }
        }

        // Required args (on first call of that tool)
        result.RequireArgsOk = true;
        if (c.RequireArgs != null)
        {
            foreach (var kv in c.RequireArgs)
            {
                var tool = Canonical(kv.Key);
                var call = calls.FirstOrDefault(x => Canonical(x.Name).Equals(tool, StringComparison.OrdinalIgnoreCase));
                if (call == null)
                {
                    result.RequireArgsOk = false;
                    failures.Add($"requireArgs: tool «{tool}» не вызывался");
                    continue;
                }

                foreach (var arg in kv.Value ?? [])
                {
                    if (!HasArg(call.Args, arg))
                    {
                        result.RequireArgsOk = false;
                        failures.Add($"requireArgs: у «{tool}» нет «{arg}»");
                    }
                }
            }
        }

        // Soft maxRounds (informational if only slightly over — still fail if way over)
        if (c.MaxRounds > 0 && result.Rounds > c.MaxRounds)
            failures.Add($"maxRounds: {result.Rounds} > {c.MaxRounds}");

        result.Passed = failures.Count == 0;
        return result;
    }

    public static GoldenRunReport Aggregate(IEnumerable<GoldenCaseResult> results, string mode)
    {
        var list = results.ToList();
        var total = list.Count;

        var firstChecked = list.Where(r => r.ActualTools.Count > 0 || r.FirstToolCorrect).ToList();
        var forbidChecked = list;
        var argsRelevant = list.Where(r =>
            r.Failures.Any(f => f.StartsWith("requireArgs", StringComparison.OrdinalIgnoreCase))
            || r.RequireArgsOk).ToList();

        return new GoldenRunReport
        {
            Mode = mode,
            Total = total,
            Passed = list.Count(r => r.Passed),
            FirstToolAccuracy = firstChecked.Count == 0
                ? 1
                : firstChecked.Count(r => r.FirstToolCorrect) / (double)firstChecked.Count,
            ForbidAccuracy = forbidChecked.Count == 0
                ? 1
                : forbidChecked.Count(r => r.ForbidOk) / (double)forbidChecked.Count,
            RequireArgsAccuracy = argsRelevant.Count == 0
                ? 1
                : argsRelevant.Count(r => r.RequireArgsOk) / (double)argsRelevant.Count,
            AvgRounds = total == 0 ? 0 : list.Average(r => r.Rounds),
            AvgPromptTokens = total == 0 ? 0 : list.Average(r => r.PromptTokens),
            Cases = list,
        };
    }

    private static bool HasArg(JObject args, string name)
    {
        if (args == null || string.IsNullOrWhiteSpace(name))
            return false;

        var token = FindToken(args, name);
        if (token == null || token.Type == JTokenType.Null)
            return false;
        if (token is JArray arr)
            return arr.Count > 0;
        if (token.Type == JTokenType.String)
            return !string.IsNullOrWhiteSpace(token.Value<string>());
        if (token.Type == JTokenType.Object)
            return token.HasValues;
        return true;
    }

    private static JToken? FindToken(JToken root, string name)
    {
        if (root is JObject o)
        {
            foreach (var p in o.Properties())
            {
                if (p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    return p.Value;
                var nested = FindToken(p.Value, name);
                if (nested != null)
                    return nested;
            }
        }
        else if (root is JArray a)
        {
            foreach (var item in a)
            {
                var nested = FindToken(item, name);
                if (nested != null)
                    return nested;
            }
        }

        return null;
    }
}
