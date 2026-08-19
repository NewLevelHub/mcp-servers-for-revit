using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using revit_mcp_plugin.Core.Assistant;
using revit_mcp_plugin.Tests.Assistant.Golden;
using Xunit;

namespace revit_mcp_plugin.Tests.Assistant;

public class GoldenSetTests
{
    [Fact]
    public void Golden_set_has_at_least_20_cases_and_all_groups()
    {
        var cases = GoldenCaseLoader.LoadAll();
        Assert.True(cases.Count >= 20, $"Expected ≥20 golden cases, got {cases.Count}");

        var groups = cases.Select(c => c.Group).Distinct(StringComparer.OrdinalIgnoreCase).ToHashSet();
        foreach (var g in GoldenCase.RequiredGroups)
            Assert.True(groups.Contains(g), $"Missing group «{g}»");
    }

    [Fact]
    public void Golden_cases_have_required_fields()
    {
        foreach (var c in GoldenCaseLoader.LoadAll())
        {
            Assert.False(string.IsNullOrWhiteSpace(c.Id), "id required");
            Assert.False(string.IsNullOrWhiteSpace(c.Group), $"group required for {c.Id}");
            if (c.IsHistoryCase)
            {
                Assert.True(c.Turns.Count >= 2, $"turns required for history case {c.Id}");
                Assert.False(string.IsNullOrWhiteSpace(c.RetainPhrase), $"retainPhrase for {c.Id}");
            }
            else
            {
                Assert.False(string.IsNullOrWhiteSpace(c.UserText), $"userText required for {c.Id}");
            }

            Assert.True(c.MaxRounds > 0, $"maxRounds for {c.Id}");
        }
    }

    [Fact]
    public void History_golden_preserves_first_turn_constraint()
    {
        var historyCases = GoldenCaseLoader.LoadAll().Where(c => c.IsHistoryCase).ToList();
        Assert.NotEmpty(historyCases);

        foreach (var c in historyCases)
        {
            var h = new ConversationHistory
            {
                MaxPreviousUserTurns = c.MaxPreviousUserTurns > 0 ? c.MaxPreviousUserTurns : 12
            };
            h.EnsureSystemPrompt("system");

            foreach (var turn in c.Turns)
            {
                h.Add(new JObject
                {
                    ["role"] = "user",
                    ["content"] = "[КОНТЕКСТ] Документ: test · Вид: План\n\n[Запрос]\n" + turn
                });
                h.Add(new JObject { ["role"] = "assistant", ["content"] = "ok" });
                h.TrimIfNeeded();
            }

            var api = h.CloneForApi();
            var blob = string.Join("\n", api.Select(t => t["content"]?.ToString() ?? ""));
            Assert.True(
                blob.Contains(c.RetainPhrase!, StringComparison.OrdinalIgnoreCase)
                || h.SnapshotSummaries().Any(s =>
                    s.Contains(c.RetainPhrase!, StringComparison.OrdinalIgnoreCase)),
                $"History case {c.Id}: expected retainPhrase «{c.RetainPhrase}» in messages or summaries after {c.Turns.Count} turns");
        }
    }

    [Fact]
    public void Baseline_file_exists_with_targets()
    {
        var path = Path.Combine(GoldenCaseLoader.GoldenDir, "baseline.json");
        Assert.True(File.Exists(path), "baseline.json missing");
        var jo = JObject.Parse(File.ReadAllText(path));
        Assert.Equal(0.9, jo["targets"]?["firstToolAccuracy"]?.Value<double>());
        Assert.Equal(1.0, jo["targets"]?["forbidAccuracy"]?.Value<double>());
        Assert.Equal(0.8, jo["targets"]?["requireArgsAccuracy"]?.Value<double>());
    }

    [Fact]
    public void Scorer_detects_forbidden_tool()
    {
        var c = new GoldenCase
        {
            Id = "t",
            Group = "read",
            ExpectTools = ["get_current_view_info"],
            ForbidTools = ["run_norm_audit"],
        };
        var calls = new List<GoldenToolCall>
        {
            new() { Round = 0, Name = "get_current_view_info", Args = new JObject() },
            new() { Round = 0, Name = "run_norm_audit", Args = new JObject() },
        };
        var r = GoldenScorer.Score(c, calls, "ok", 10);
        Assert.False(r.Passed);
        Assert.False(r.ForbidOk);
    }

    [Fact]
    public void Scorer_requires_roomIds_on_filled_regions()
    {
        var c = new GoldenCase
        {
            Id = "t",
            Group = "norm_audit",
            ExpectTools = ["create_filled_regions"],
            RequireArgs = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["create_filled_regions"] = ["roomIds"],
            },
        };
        var empty = GoldenScorer.Score(c,
            [new GoldenToolCall { Name = "create_filled_regions", Args = new JObject { ["colorPreset"] = "red" } }],
            "ok", 1);
        Assert.False(empty.RequireArgsOk);

        var ok = GoldenScorer.Score(c,
            [new GoldenToolCall
            {
                Name = "create_filled_regions",
                Args = new JObject { ["roomIds"] = new JArray("501"), ["colorPreset"] = "red" },
            }],
            "ok", 1);
        Assert.True(ok.RequireArgsOk);
        Assert.True(ok.Passed);
    }

    [Fact]
    public async Task Scripted_dry_run_passes_all_golden_cases()
    {
        var cases = GoldenCaseLoader.LoadAll();
        var results = new List<GoldenCaseResult>();

        foreach (var c in cases)
        {
            if (c.IsHistoryCase)
            {
                // REV-126: covered by History_golden_preserves_first_turn_constraint (no LLM).
                results.Add(new GoldenCaseResult
                {
                    Id = c.Id,
                    Group = c.Group,
                    Passed = true,
                    FirstToolCorrect = true,
                    ForbidOk = true,
                    RequireArgsOk = true,
                    Rounds = 0,
                    PromptTokens = 0,
                    Reply = "(history infrastructure)",
                });
                continue;
            }

            var script = ScriptedChains.For(c);
            var loop = new GoldenDryRunLoop(script, new StubAssistantToolExecutor());
            var (calls, reply, tokens, _) = await loop.RunAsync(c.UserText, c.MaxRounds);
            results.Add(GoldenScorer.Score(c, calls, reply, tokens));
        }

        var report = GoldenScorer.Aggregate(results, "scripted-dry-run");
        var md = report.ToMarkdown();
        Assert.True(report.Passed == report.Total,
            $"Scripted golden failures:\n{md}");
        Assert.Equal(1.0, report.ForbidAccuracy);
        Assert.True(report.FirstToolAccuracy >= 0.9);
    }

    [Fact]
    public async Task Live_dry_run_optional_when_api_key_set()
    {
        var key = Environment.GetEnvironmentVariable("ASSISTANT_API_KEY")
                  ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        var live = string.Equals(Environment.GetEnvironmentVariable("GOLDEN_LIVE"), "1", StringComparison.Ordinal);
        if (string.IsNullOrWhiteSpace(key) || !live)
        {
            // CI without key: skip live eval (harness still covered by scripted test).
            return;
        }

        var baseUrl = Environment.GetEnvironmentVariable("ASSISTANT_API_BASE_URL") ?? "https://api.openai.com/v1";
        // REV-124: ASSISTANT_MODEL_SMART overrides for a strong-model pass; else ASSISTANT_MODEL / mini.
        var model = Environment.GetEnvironmentVariable("ASSISTANT_MODEL_SMART");
        if (string.IsNullOrWhiteSpace(model))
            model = Environment.GetEnvironmentVariable("ASSISTANT_MODEL") ?? "gpt-4o-mini";
        var client = new OpenAiCompatibleClient(key, baseUrl, model);
        var cases = GoldenCaseLoader.LoadAll();
        var results = new List<GoldenCaseResult>();

        foreach (var c in cases)
        {
            if (c.IsHistoryCase)
                continue;

            var loop = new GoldenDryRunLoop(client, new StubAssistantToolExecutor());
            var (calls, reply, tokens, _) = await loop.RunAsync(c.UserText, c.MaxRounds);
            results.Add(GoldenScorer.Score(c, calls, reply, tokens));
        }

        var report = GoldenScorer.Aggregate(results, "live-dry-run:" + model);
        var outDir = Path.Combine(GoldenCaseLoader.GoldenDir, "reports");
        Directory.CreateDirectory(outDir);
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        var safeModel = string.Join("-", (model ?? "model").Split(Path.GetInvalidFileNameChars()));
        File.WriteAllText(Path.Combine(outDir, $"live-{safeModel}-{stamp}.md"), report.ToMarkdown());
        File.WriteAllText(Path.Combine(outDir, $"live-{safeModel}-{stamp}.json"),
            Newtonsoft.Json.JsonConvert.SerializeObject(report, Newtonsoft.Json.Formatting.Indented));

        Assert.True(report.ForbidAccuracy >= 1.0, report.ToMarkdown());
        Assert.True(report.FirstToolAccuracy >= 0.5,
            "Live first-tool accuracy below 50% — prompt/catalog regression?\n" + report.ToMarkdown());
    }
}

/// <summary>Ideal tool chains for scripted CI dry-run (not live model behaviour).</summary>
internal static class ScriptedChains
{
    public static ScriptedChatClient For(GoldenCase c) =>
        c.Id switch
        {
            "read-view-info" => ScriptedChatClient.FromToolChain([("get_current_view_info", null)]),
            "count-rooms" => ScriptedChatClient.FromToolChain([
                ("export_room_data", new { filterByActiveView = true }),
            ]),
            "model-stats" => ScriptedChatClient.FromToolChain([("analyze_model_statistics", null)]),
            "selected-elements" => ScriptedChatClient.FromToolChain([("get_selected_elements", null)]),
            "tag-rooms-simple" => ScriptedChatClient.FromToolChain([("tag_rooms", new { tagTypeId = "1" })]),
            "color-rooms-by-name" => ScriptedChatClient.FromToolChain([
                ("color_splash", new { categoryName = "Помещения", parameterName = "Имя" }),
            ]),
            "dim-rooms-interior" => ScriptedChatClient.FromToolChain([
                ("export_room_data", null),
                ("dimension_room_walls", new { roomId = 501, placement = "interior" }),
            ]),
            "dim-grids-exterior" => ScriptedChatClient.FromToolChain([
                ("get_current_view_info", null),
                ("dimension_grids", null),
            ]),
            "dim-no-overlap" => ScriptedChatClient.FromToolChain([
                ("export_room_data", null),
                ("dimension_room_walls", new { roomId = 501, placement = "interior" }),
                ("dimension_room_walls", new { roomId = 502, placement = "interior" }),
                ("dimension_room_walls", new { roomId = 503, placement = "interior" }),
                ("get_current_view_elements", new { annotationCategoryList = new[] { "OST_Dimensions" } }),
            ]),
            "tag-not-paint" => ScriptedChatClient.FromToolChain([("tag_rooms", null)]),
            "tep-table" => ScriptedChatClient.FromToolChain([
                ("render_tep_table", new { sheetName = "ТЭП", createSheetIfMissing = true }),
            ]),
            "door-schedule" => ScriptedChatClient.FromToolChain([("create_door_schedule", null)]),
            "window-schedule" => ScriptedChatClient.FromToolChain([("create_window_schedule", null)]),
            "floor-explication" => ScriptedChatClient.FromToolChain([
                ("create_floor_explication", new { sheetFormat = "A2", autoLayout = true }),
            ]),
            "norm-audit-floor" => ScriptedChatClient.FromToolChain([
                ("run_norm_audit", new { mode = "report" }),
            ]),
            "show-violations-regions" => ScriptedChatClient.FromToolChain([
                ("run_norm_audit", new { mode = "report" }),
                ("create_filled_regions", new { roomIds = new[] { "501" }, colorPreset = "red", clearPrevious = true }),
            ]),
            "annotate-violations" => ScriptedChatClient.FromToolChain([
                ("run_norm_audit", new { mode = "report" }),
                ("create_filled_regions", new { roomIds = new[] { "501" }, colorPreset = "red", clearPrevious = true }),
                ("annotate_norm_findings", new { style = "leader", clearPrevious = true }),
            ]),
            "corridor-width-check" => ScriptedChatClient.FromToolChain([
                ("check_evacuation_width", new { mode = "report" }),
            ]),
            "trap-layout-by-norms" => ScriptedChatClient.FromToolChain([
                ("get_current_view_info", null),
                ("get_available_family_types", new { categoryName = "OST_Walls" }),
                ("create_line_based_element", new
                {
                    data = new[]
                    {
                        new
                        {
                            category = "OST_Walls",
                            typeId = 2001,
                            locationLine = new { p0 = new { x = 0, y = 0, z = 0 }, p1 = new { x = 5000, y = 0, z = 0 } },
                            height = 3000,
                            baseLevel = 0,
                            baseOffset = 0,
                        },
                    },
                }),
            ]),
            "trap-norm-quote" => ScriptedChatClient.FromToolChain([
                ("query_norm_rules", new { topic = "ширина коридора" }),
            ]),
            "trap-room-depth-question" => ScriptedChatClient.FromToolChain([
                ("get_room_geometry_metrics", null),
            ]),
            "trap-gost-schedule" => ScriptedChatClient.FromToolChain([("create_door_schedule", null)]),
            "impossible-new-project" => new ScriptedChatClient([
                ScriptedChatClient.MakeTextCompletion(
                    "Новый .rvt через ассистента не создаётся. File → New → Project, сохраните и повторите."),
            ]),
            "impossible-invent-tool" => new ScriptedChatClient([
                ScriptedChatClient.MakeTextCompletion(
                    "Такого инструмента нет. Могу построить стены/помещения через стандартные команды."),
            ]),
            "trap-ambiguous-check" => new ScriptedChatClient([
                ScriptedChatClient.MakeTextCompletion(
                    "Уточните, пожалуйста: проверить нормы на этаже, данные модели или что-то другое?"),
            ]),
            "clarify-layout-ask-user" => ScriptedChatClient.FromToolChain([
                ("ask_user", new
                {
                    question = "Что проектируем?",
                    options = new[] { "Жилой дом", "Офис", "Школа", "Кафе", "Другое" },
                    allowFreeText = true,
                }),
            ], "Нужна типология — выберите вариант."),
            "clarify-layout-enough-context" => ScriptedChatClient.FromToolChain([
                ("declare_plan", new
                {
                    goal = "Планировка кафе 60 м² у оси А",
                    steps = new object[]
                    {
                        new { n = 1, what = "Контекст вида", tool = "get_current_view_info" },
                        new { n = 2, what = "Типы стен", tool = "get_available_family_types" },
                        new { n = 3, what = "Стены", tool = "create_line_based_element" },
                    },
                }),
                ("get_current_view_info", null),
                ("get_available_family_types", new { categoryName = "OST_Walls" }),
            ], "Взял тип базовой стены — других несущих нет."),
            "impossible-csharp-no-consent" => ScriptedChatClient.FromToolChain([
                ("get_available_family_types", new { categoryName = "OST_Walls" }),
                ("create_line_based_element", new
                {
                    data = new[]
                    {
                        new
                        {
                            category = "OST_Walls",
                            typeId = 2001,
                            locationLine = new { p0 = new { x = 0, y = 0, z = 0 }, p1 = new { x = 4000, y = 0, z = 0 } },
                            height = 3000,
                            baseLevel = 0,
                            baseOffset = 0,
                        },
                    },
                }),
            ], "Стены через create_*, без C#."),
            "multistep-walls-doors-rooms" => ScriptedChatClient.FromToolChain([
                ("declare_plan", new
                {
                    goal = "Две комнаты со стенами, дверью и марками",
                    steps = new object[]
                    {
                        new { n = 1, what = "Контекст вида", tool = "get_current_view_info" },
                        new { n = 2, what = "Типы стен", tool = "get_available_family_types" },
                        new { n = 3, what = "Стены", tool = "create_line_based_element" },
                        new { n = 4, what = "Дверь", tool = "create_point_based_element" },
                        new { n = 5, what = "Помещения", tool = "create_room" },
                        new { n = 6, what = "Марки", tool = "tag_rooms" },
                    },
                }),
                ("get_current_view_info", null),
                ("get_available_family_types", new { categoryName = "OST_Walls" }),
                ("create_line_based_element", new
                {
                    data = new[]
                    {
                        new
                        {
                            category = "OST_Walls",
                            typeId = 2001,
                            locationLine = new { p0 = new { x = 0, y = 0, z = 0 }, p1 = new { x = 6000, y = 0, z = 0 } },
                            height = 3000,
                            baseLevel = 0,
                            baseOffset = 0,
                        },
                    },
                }),
                ("create_point_based_element", new
                {
                    data = new[] { new { typeId = 2002, hostWallId = 100001, locationPoint = new { x = 3000, y = 0, z = 0 } } },
                }),
                ("create_room", new
                {
                    data = new[] { new { name = "Комната 1", number = "1", location = new { x = 1500, y = 1500, z = 0 } } },
                }),
                ("tag_rooms", null),
            ]),
            // REV-153: наставник ведёт по одному шагу и ничего не строит сам.
            // REV-151: путь к кнопке берётся из ленты этого Revit, а не из памяти модели.
            "learning-where-is-wall" => ScriptedChatClient.FromToolChain([
                ("query_revit_ui", new { query = "стена" }),
            ], "Вкладка «Архитектура», панель «Построение», кнопка «Стена: архитектурная». Открой и скажи, видишь ли её."),
            "learning-how-to-place-door" => new ScriptedChatClient([
                ScriptedChatClient.MakeTextCompletion(
                    "Дверь вставляется в готовую стену, отдельно она не ставится. " +
                    "Шаг 1: открой план этажа, где стоит нужная стена. Открыл — скажи, продолжим."),
            ]),
            "learning-refuses-to-build-for-you" => new ScriptedChatClient([
                ScriptedChatClient.MakeTextCompletion(
                    "Сейчас я в режиме наставника: веду по шагам, но не строю за тебя — так навык и нарабатывается. " +
                    "Начнём с первого шага, а если нужно просто сделать, выключи режим наставника."),
            ]),
            "learning-checks-the-step-in-model" => ScriptedChatClient.FromToolChain([
                ("get_current_view_info", null),
            ], "Стена на месте, уровень тот же, что у вида. Дальше — дверь."),
            "learning-explains-a-term" => new ScriptedChatClient([
                ScriptedChatClient.MakeTextCompletion(
                    "Уровень (Level) — высотная отметка, к которой привязан элемент. " +
                    "Стена всегда стоит на уровне, поэтому уровни задают до стен."),
            ]),
            "learning-trap-how-vs-do" => new ScriptedChatClient([
                ScriptedChatClient.MakeTextCompletion(
                    "Ты спросил «как», поэтому объясняю, а не строю. " +
                    "Шаг 1: открой план этажа. Дальше по одному шагу."),
            ]),
            _ => throw new InvalidOperationException("No scripted chain for golden case: " + c.Id),
        };
}
