using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace revit_mcp_plugin.Tests.Assistant.Golden;

/// <summary>One golden scenario (REV-111).</summary>
public sealed class GoldenCase
{
    [JsonProperty("id")]
    public string Id { get; set; } = "";

    [JsonProperty("group")]
    public string Group { get; set; } = "";

    [JsonProperty("userText")]
    public string UserText { get; set; } = "";

    [JsonProperty("expectTools")]
    public List<string> ExpectTools { get; set; } = new();

    [JsonProperty("forbidTools")]
    public List<string> ForbidTools { get; set; } = new();

    [JsonProperty("expectNoTools")]
    public bool ExpectNoTools { get; set; }

    [JsonProperty("expectToolsOrdered")]
    public bool ExpectToolsOrdered { get; set; }

    [JsonProperty("maxRounds")]
    public int MaxRounds { get; set; } = 8;

    [JsonProperty("requireArgs")]
    public Dictionary<string, List<string>> RequireArgs { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonProperty("firstToolMustBe")]
    public string? FirstToolMustBe { get; set; }

    public static IReadOnlyList<string> RequiredGroups { get; } =
    [
        "read",
        "annotate",
        "tep_vs_schedules",
        "norm_audit",
        "routing_traps",
        "impossible",
        "multistep",
    ];
}

public sealed class GoldenToolCall
{
    public int Round { get; set; }
    public string Name { get; set; } = "";
    public JObject Args { get; set; } = new();
}

public sealed class GoldenCaseResult
{
    public string Id { get; set; } = "";
    public string Group { get; set; } = "";
    public bool Passed { get; set; }
    public bool FirstToolCorrect { get; set; }
    public bool ForbidOk { get; set; }
    public bool RequireArgsOk { get; set; }
    public int Rounds { get; set; }
    public int PromptTokens { get; set; }
    public List<string> ActualTools { get; set; } = new();
    public List<string> Failures { get; set; } = new();
    public string Reply { get; set; } = "";
}

public sealed class GoldenRunReport
{
    public DateTime TsUtc { get; set; } = DateTime.UtcNow;
    public string Mode { get; set; } = "dry-run";
    public int Total { get; set; }
    public int Passed { get; set; }
    public double FirstToolAccuracy { get; set; }
    public double ForbidAccuracy { get; set; }
    public double RequireArgsAccuracy { get; set; }
    public double AvgRounds { get; set; }
    public double AvgPromptTokens { get; set; }
    public List<GoldenCaseResult> Cases { get; set; } = new();

    public string ToMarkdown()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"# Golden set report ({Mode})");
        sb.AppendLine();
        sb.AppendLine($"| Metric | Value | Target |");
        sb.AppendLine($"| --- | --- | --- |");
        sb.AppendLine($"| Passed | {Passed}/{Total} | — |");
        sb.AppendLine($"| First tool correct | {FirstToolAccuracy:P0} | ≥ 90% |");
        sb.AppendLine($"| Forbid tools clean | {ForbidAccuracy:P0} | 100% |");
        sb.AppendLine($"| Required args present | {RequireArgsAccuracy:P0} | ≥ 80% |");
        sb.AppendLine($"| Avg rounds | {AvgRounds:F1} | ↓ over time |");
        sb.AppendLine($"| Avg promptTokens | {AvgPromptTokens:F0} | ↓ over time |");
        sb.AppendLine();
        var fails = Cases.Where(c => !c.Passed).ToList();
        if (fails.Count == 0)
        {
            sb.AppendLine("All cases passed.");
            return sb.ToString();
        }

        sb.AppendLine("## Failures");
        sb.AppendLine();
        foreach (var f in fails)
        {
            sb.AppendLine($"### `{f.Id}` ({f.Group})");
            sb.AppendLine($"- tools: `{string.Join(" → ", f.ActualTools)}`");
            foreach (var msg in f.Failures)
                sb.AppendLine($"- {msg}");
            sb.AppendLine();
        }

        return sb.ToString();
    }
}

public static class GoldenCaseLoader
{
    public static string GoldenDir
    {
        get
        {
            var candidates = new[]
            {
                Path.Combine(AppContext.BaseDirectory, "golden"),
                Path.Combine(AppContext.BaseDirectory, "Golden"),
                Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Golden")),
                Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "golden")),
            };
            foreach (var dir in candidates)
            {
                if (Directory.Exists(dir)
                    && Directory.EnumerateFiles(dir, "*.json")
                        .Any(p => !Path.GetFileName(p).Equals("baseline.json", StringComparison.OrdinalIgnoreCase)))
                    return dir;
            }

            return candidates[0];
        }
    }

    public static IReadOnlyList<GoldenCase> LoadAll()
    {
        var dir = GoldenDir;
        if (!Directory.Exists(dir))
            throw new DirectoryNotFoundException("Golden case folder not found: " + dir);

        var list = new List<GoldenCase>();
        foreach (var path in Directory.EnumerateFiles(dir, "*.json")
                     .Where(p => !Path.GetFileName(p).Equals("baseline.json", StringComparison.OrdinalIgnoreCase))
                     .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            var json = File.ReadAllText(path);
            var c = JsonConvert.DeserializeObject<GoldenCase>(json)
                    ?? throw new InvalidOperationException("Invalid golden case: " + path);
            if (string.IsNullOrWhiteSpace(c.Id))
                c.Id = Path.GetFileNameWithoutExtension(path);
            list.Add(c);
        }

        return list;
    }
}
