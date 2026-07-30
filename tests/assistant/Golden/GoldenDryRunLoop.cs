using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using revit_mcp_plugin.Core.Assistant;

namespace revit_mcp_plugin.Tests.Assistant.Golden;

/// <summary>
/// Minimal ReAct loop for golden dry-run: real/scripted LLM + stub tools, no Revit (REV-111).
/// </summary>
public sealed class GoldenDryRunLoop
{
    private readonly IChatCompletionsClient _llm;
    private readonly IAssistantToolExecutor _tools;
    private readonly string? _systemPromptOverride;

    public GoldenDryRunLoop(
        IChatCompletionsClient llm,
        IAssistantToolExecutor tools,
        string? systemPrompt = null)
    {
        _llm = llm ?? throw new ArgumentNullException(nameof(llm));
        _tools = tools ?? throw new ArgumentNullException(nameof(tools));
        _systemPromptOverride = systemPrompt;
    }

    public async Task<(IReadOnlyList<GoldenToolCall> Calls, string Reply, int PromptTokens, int Rounds)> RunAsync(
        string userText,
        int maxRounds,
        CancellationToken cancellationToken = default)
    {
        var activeProfiles = ToolCatalog.NormalizeProfiles(IntentRouter.ResolveHeuristic(userText));
        var resolvedPrompt = _systemPromptOverride ?? AssistantSystemPrompt.Build(activeProfiles, userText);
        var history = new JArray
        {
            new JObject { ["role"] = "system", ["content"] = resolvedPrompt },
            new JObject { ["role"] = "user", ["content"] = userText ?? "" },
        };
        var tools = ToolCatalog.GetOpenAiTools(activeProfiles);
        var calls = new List<GoldenToolCall>();
        var promptTokens = 0;
        var rounds = 0;

        for (var round = 0; round < Math.Max(1, maxRounds); round++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            rounds = round + 1;
            tools = ToolCatalog.GetOpenAiTools(activeProfiles);

            var completion = await _llm.ChatCompletionsAsync(history, tools, cancellationToken)
                .ConfigureAwait(false);

            var usage = completion["usage"];
            if (usage != null)
                promptTokens += usage["prompt_tokens"]?.Value<int>() ?? 0;

            // Approximate catalog size into tokens for scripted runs (usage is fixed).
            promptTokens += Math.Max(0, tools.ToString(Newtonsoft.Json.Formatting.None).Length / 4);

            var choice = completion["choices"]?[0]?["message"] as JObject;
            if (choice == null)
                return (calls, "Пустой ответ от ИИ.", promptTokens, rounds);

            history.Add((JObject)choice.DeepClone());

            var toolCalls = choice["tool_calls"] as JArray;
            if (toolCalls == null || toolCalls.Count == 0)
            {
                var reply = choice["content"]?.ToString()?.Trim() ?? "";
                return (calls, reply, promptTokens, rounds);
            }

            foreach (var callTok in toolCalls)
            {
                var call = callTok as JObject;
                if (call == null) continue;

                var callId = call["id"]?.ToString() ?? Guid.NewGuid().ToString("N");
                var fn = call["function"] as JObject;
                var name = GoldenScorer.Canonical(fn?["name"]?.ToString() ?? "");
                var argsJson = fn?["arguments"]?.ToString() ?? "{}";
                JObject argsObj;
                try { argsObj = JObject.Parse(argsJson); }
                catch { argsObj = new JObject(); }

                calls.Add(new GoldenToolCall
                {
                    Round = round,
                    Name = name,
                    Args = argsObj,
                });

                if (!ToolCatalog.IsToolAllowed(name, activeProfiles))
                {
                    var missing = ToolCatalog.GetMissingProfiles(name, activeProfiles);
                    if (missing.Count > 0)
                    {
                        activeProfiles = ToolCatalog.MergeProfiles(activeProfiles, missing);
                        history.Add(new JObject
                        {
                            ["role"] = "tool",
                            ["tool_call_id"] = callId,
                            ["content"] = Truncate(new JObject
                            {
                                ["error"] = "tool_not_in_profile",
                                ["tool"] = name,
                                ["availableInProfiles"] = new JArray(missing.ToArray()),
                                ["hint"] = "Profiles expanded — call the same tool again.",
                            }.ToString(Newtonsoft.Json.Formatting.None), 3500),
                        });
                        continue;
                    }

                    var reordered = ToolCatalog.PrioritizeProfilesForTool(name, activeProfiles);
                    if (ToolCatalog.IsToolAllowed(name, reordered))
                    {
                        activeProfiles = reordered;
                        history.Add(new JObject
                        {
                            ["role"] = "tool",
                            ["tool_call_id"] = callId,
                            ["content"] = Truncate(new JObject
                            {
                                ["error"] = "tool_not_in_profile",
                                ["tool"] = name,
                                ["hint"] = "Profiles reordered for cap — call the same tool again.",
                            }.ToString(Newtonsoft.Json.Formatting.None), 3500),
                        });
                        continue;
                    }

                    history.Add(new JObject
                    {
                        ["role"] = "tool",
                        ["tool_call_id"] = callId,
                        ["content"] = Truncate(new JObject
                        {
                            ["error"] = "unknown_tool",
                            ["tool"] = name,
                        }.ToString(Newtonsoft.Json.Formatting.None), 3500),
                    });
                    continue;
                }

                string raw;
                try
                {
                    raw = _tools.Execute(name, argsJson);
                }
                catch (Exception ex)
                {
                    raw = new JObject
                    {
                        ["jsonrpc"] = "2.0",
                        ["error"] = new JObject { ["message"] = ex.Message }
                    }.ToString();
                }

                history.Add(new JObject
                {
                    ["role"] = "tool",
                    ["tool_call_id"] = callId,
                    ["content"] = Truncate(raw, 3500),
                });
            }
        }

        return (calls, "Слишком много шагов.", promptTokens, rounds);
    }

    private static string Truncate(string s, int max)
    {
        if (string.IsNullOrEmpty(s) || s.Length <= max)
            return s;
        return s.Substring(0, max) + "…";
    }
}

/// <summary>Stub tool executor — canned success payloads so multi-step scenarios can continue.</summary>
public sealed class StubAssistantToolExecutor : IAssistantToolExecutor
{
    private int _nextId = 100000;

    public string Execute(string toolName, string argsJson)
    {
        var id = ++_nextId;
        var name = GoldenScorer.Canonical(toolName);

        JObject result = name switch
        {
            "get_current_view_info" => new JObject
            {
                ["ViewName"] = "Уровень 1",
                ["ViewType"] = "FloorPlan",
                ["LevelName"] = "Уровень 1",
                ["LevelElevationMm"] = 0,
                ["Scale"] = 100,
            },
            "get_available_family_types" => new JObject
            {
                ["types"] = new JArray
                {
                    new JObject
                    {
                        ["typeId"] = 2001,
                        ["name"] = "Базовая стена",
                        ["category"] = "OST_Walls",
                        ["familyName"] = "Стена",
                    },
                    new JObject
                    {
                        ["typeId"] = 2002,
                        ["name"] = "Дверь 900",
                        ["category"] = "OST_Doors",
                        ["familyName"] = "Дверь",
                    },
                },
                ["suggestedWallTypeId"] = 2001,
            },
            "create_line_based_element" => new JObject
            {
                ["Success"] = true,
                ["CreatedElementIds"] = new JArray { id, id + 1, id + 2, id + 3 },
                ["message"] = "walls ok",
            },
            "create_point_based_element" => new JObject
            {
                ["Success"] = true,
                ["CreatedElementIds"] = new JArray { id },
            },
            "create_room" => new JObject
            {
                ["Success"] = true,
                ["Id"] = id,
                ["Name"] = "Комната",
            },
            "export_room_data" => new JObject
            {
                ["rooms"] = new JArray
                {
                    new JObject { ["Id"] = 501, ["Name"] = "Кухня", ["Area"] = 12.5 },
                    new JObject { ["Id"] = 502, ["Name"] = "Гостиная", ["Area"] = 22.0 },
                },
                ["count"] = 2,
            },
            "run_norm_audit" => new JObject
            {
                ["findings"] = new JArray
                {
                    new JObject
                    {
                        ["status"] = "violation",
                        ["elementId"] = 501,
                        ["elementType"] = "Room",
                        ["name"] = "Коридор",
                        ["actual"] = 900,
                        ["required"] = 1200,
                        ["document"] = "СП РК",
                        ["clause"] = "4.4",
                    },
                },
                ["skippedRules"] = new JArray(),
            },
            "query_norm_rules" => new JObject
            {
                ["rules"] = new JArray
                {
                    new JObject
                    {
                        ["document"] = "СП РК 3.02-101-2012",
                        ["clause"] = "4.4.10",
                        ["quote"] = "Ширина коридора не менее 1,2 м",
                        ["min"] = 1200,
                    },
                },
            },
            "analyze_model_statistics" => new JObject
            {
                ["walls"] = 40,
                ["rooms"] = 12,
                ["doors"] = 8,
            },
            "get_selected_elements" => new JObject
            {
                ["elements"] = new JArray(),
                ["count"] = 0,
            },
            "get_room_geometry_metrics" => new JObject
            {
                ["rooms"] = new JArray
                {
                    new JObject { ["Id"] = 501, ["widthMm"] = 3500, ["depthMm"] = 5200, ["areaM2"] = 18.2 },
                },
            },
            _ => new JObject
            {
                ["Success"] = true,
                ["ok"] = true,
                ["Id"] = id,
                ["message"] = $"stub:{name}",
            },
        };

        return new JObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = "stub",
            ["result"] = result,
        }.ToString(Newtonsoft.Json.Formatting.None);
    }
}

/// <summary>Scripted LLM for CI — returns predetermined tool_calls / replies.</summary>
public sealed class ScriptedChatClient : IChatCompletionsClient
{
    private readonly Queue<JObject> _completions;

    public ScriptedChatClient(IEnumerable<JObject> completions)
    {
        _completions = new Queue<JObject>(completions);
    }

    public static ScriptedChatClient FromToolChain(IEnumerable<(string Name, object? Args)> tools, string finalReply = "Готово.")
    {
        var list = new List<JObject>();
        var i = 0;
        foreach (var (name, args) in tools)
        {
            i++;
            var argsJson = args == null
                ? "{}"
                : (args is string s ? s : JObject.FromObject(args).ToString(Newtonsoft.Json.Formatting.None));
            list.Add(MakeToolCompletion(name, argsJson, "call_" + i));
        }

        list.Add(MakeTextCompletion(finalReply));
        return new ScriptedChatClient(list);
    }

    public static JObject MakeToolCompletion(string name, string argsJson, string callId)
    {
        return new JObject
        {
            ["choices"] = new JArray
            {
                new JObject
                {
                    ["message"] = new JObject
                    {
                        ["role"] = "assistant",
                        ["content"] = null,
                        ["tool_calls"] = new JArray
                        {
                            new JObject
                            {
                                ["id"] = callId,
                                ["type"] = "function",
                                ["function"] = new JObject
                                {
                                    ["name"] = name,
                                    ["arguments"] = argsJson,
                                },
                            },
                        },
                    },
                },
            },
            ["usage"] = new JObject { ["prompt_tokens"] = 100, ["completion_tokens"] = 20 },
        };
    }

    public static JObject MakeTextCompletion(string text)
    {
        return new JObject
        {
            ["choices"] = new JArray
            {
                new JObject
                {
                    ["message"] = new JObject
                    {
                        ["role"] = "assistant",
                        ["content"] = text,
                    },
                },
            },
            ["usage"] = new JObject { ["prompt_tokens"] = 80, ["completion_tokens"] = 30 },
        };
    }

    public Task<JObject> ChatCompletionsAsync(JArray messages, JArray tools, CancellationToken cancellationToken)
    {
        if (_completions.Count == 0)
            return Task.FromResult(MakeTextCompletion("Готово."));
        return Task.FromResult(_completions.Dequeue());
    }
}
