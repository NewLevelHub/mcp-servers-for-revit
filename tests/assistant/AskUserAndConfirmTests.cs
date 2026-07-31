using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using revit_mcp_plugin.Core.Assistant;
using Xunit;

namespace revit_mcp_plugin.Tests.Assistant;

public class AskUserAndConfirmTests
{
    [Fact]
    public void AskUser_parser_requires_question_and_two_options()
    {
        Assert.False(AskUserParser.TryParse("{}", out _, out var err));
        Assert.Contains("question", err, StringComparison.OrdinalIgnoreCase);

        Assert.False(AskUserParser.TryParse(
            new JObject { ["question"] = "Что?", ["options"] = new JArray("A") }.ToString(),
            out _, out err));
        Assert.Contains("options", err, StringComparison.OrdinalIgnoreCase);

        Assert.True(AskUserParser.TryParse(
            new JObject
            {
                ["question"] = "Что проектируем?",
                ["options"] = new JArray("Жилой дом", "Офис", "Школа"),
                ["allowFreeText"] = false,
            }.ToString(),
            out var pending, out _));
        Assert.Equal("Что проектируем?", pending.Question);
        Assert.Equal(3, pending.Options.Count);
        Assert.False(pending.AllowFreeText);
    }

    [Fact]
    public void AskUser_success_payload_includes_answer()
    {
        var payload = AskUserParser.ToSuccessPayload(new AskUserAnswer { SelectedOption = "Офис" });
        Assert.True(payload["success"]!.Value<bool>());
        Assert.Equal("Офис", payload["answer"]!.ToString());
        Assert.Contains("Офис", payload["summary"]!.ToString());
    }

    [Fact]
    public void DeleteConfirmSummary_counts_and_formats_categories()
    {
        var ids = Enumerable.Range(1, 25).Select(i => i.ToString()).ToList();
        var args = new JObject { ["elementIds"] = new JArray(ids) }.ToString();
        Assert.Equal(25, DeleteConfirmSummary.CountTargets("delete_element", args));

        var text = DeleteConfirmSummary.Format(25, new Dictionary<string, int>
        {
            ["Оси"] = 12,
            ["Размеры"] = 13,
        });
        Assert.Contains("Удалить 25", text);
        Assert.Contains("Оси: 12", text);
        Assert.Contains("Размеры: 13", text);
    }

    [Fact]
    public void DeleteConfirmSummary_resolve_categories_via_callback()
    {
        var args = new JObject
        {
            ["elementIds"] = new JArray("1", "2", "3"),
        }.ToString();
        var text = DeleteConfirmSummary.Format("delete_element", args, id =>
            id == "3" ? "Размеры" : "Оси");
        Assert.Contains("Оси: 2", text);
        Assert.Contains("Размеры: 1", text);
    }

    [Fact]
    public void ShouldConfirm_respects_threshold_and_master_switch()
    {
        var many = new JObject
        {
            ["elementIds"] = new JArray(Enumerable.Range(1, 25).Select(i => i.ToString()).ToArray()),
        }.ToString();
        var few = new JObject
        {
            ["elementIds"] = new JArray("1", "2", "3"),
        }.ToString();

        Assert.False(ToolCatalog.ShouldConfirm("delete_element", many, requireConfirmations: false, deleteThreshold: 20));
        Assert.True(ToolCatalog.ShouldConfirm("delete_element", many, requireConfirmations: true, deleteThreshold: 20));
        Assert.False(ToolCatalog.ShouldConfirm("delete_element", few, requireConfirmations: true, deleteThreshold: 20));
        Assert.True(ToolCatalog.ShouldConfirm("delete_element", few, requireConfirmations: true, deleteThreshold: 3));
        Assert.True(ToolCatalog.ShouldConfirm("send_code_to_revit", "{}", requireConfirmations: true, deleteThreshold: 20));
        Assert.False(ToolCatalog.ShouldConfirm("send_code_to_revit", "{}", requireConfirmations: false, deleteThreshold: 20));
        Assert.False(ToolCatalog.ShouldConfirm("create_room", "{}", requireConfirmations: true, deleteThreshold: 20));
    }

    [Fact]
    public void ShouldConfirm_operate_element_delete_uses_data_elementIds()
    {
        var ids = Enumerable.Range(1, 22).Select(i => (object)i).ToArray();
        var args = new JObject
        {
            ["data"] = new JObject
            {
                ["action"] = "Delete",
                ["elementIds"] = new JArray(ids),
            },
        }.ToString();
        Assert.True(ToolCatalog.ShouldConfirm("operate_element", args, true, 20));
        Assert.Equal(22, DeleteConfirmSummary.CountTargets("operate_element", args));
    }

    [Fact]
    public void Typology_match_school_and_residential_from_ask_answer()
    {
        var school = TypologyPrograms.MatchFromAnswer("Школа");
        Assert.NotNull(school);
        Assert.Equal("school_wing", school!.Id);
        Assert.True(school.Rooms.Count >= 5);
        Assert.Contains("Класс", string.Join(" ", school.Rooms.Select(r => r.Name)));

        var hint = TypologyPrograms.BuildHintForAnswer("Школа");
        Assert.Contains("ПРОГРАММА", hint);
        Assert.Contains("не своди к двум комнатам", hint, StringComparison.OrdinalIgnoreCase);

        Assert.Equal("residential_flat", TypologyPrograms.MatchFromAnswer("Жилой дом")!.Id);
        Assert.Equal("office_open", TypologyPrograms.MatchFromAnswer("Офис")!.Id);
    }

    [Fact]
    public void Ask_user_is_in_core_tools_and_schema()
    {
        Assert.Contains(ToolCatalog.CoreTools, t => t.Equals("ask_user", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("вопрос", ToolCatalog.FriendlyName("ask_user"));
        var tools = ToolCatalog.GetOpenAiTools();
        var ask = tools.OfType<JObject>().First(t => t["function"]?["name"]?.ToString() == "ask_user");
        var required = ask["function"]!["parameters"]!["required"] as JArray;
        Assert.Contains(required!.Select(x => x.ToString()), x => x == "question");
        Assert.Contains(required!.Select(x => x.ToString()), x => x == "options");
    }
}
