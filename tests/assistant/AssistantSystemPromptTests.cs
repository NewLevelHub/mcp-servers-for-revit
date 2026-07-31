using System;
using System.Linq;
using revit_mcp_plugin.Core.Assistant;
using Xunit;

namespace revit_mcp_plugin.Tests.Assistant;

public class AssistantSystemPromptTests
{
    [Fact]
    public void Core_is_at_most_800_characters()
    {
        Assert.True(
            AssistantSystemPrompt.Core.Length <= 800,
            $"Core length {AssistantSystemPrompt.Core.Length} > 800");
    }

    [Fact]
    public void Simple_read_prompt_is_much_smaller_than_legacy_monolith()
    {
        var dataOnly = AssistantSystemPrompt.Build(new[] { ToolCatalog.Profiles.Data }, "Сколько помещений?");
        // Core + always-on Clarification (REV-125) + Data — still well under the old monolith.
        Assert.True(
            dataOnly.Length < AssistantSystemPrompt.LegacyMonolithLength,
            $"data prompt {dataOnly.Length} should be < {AssistantSystemPrompt.LegacyMonolithLength}");
        Assert.Contains("filterByActiveView", dataOnly);
        Assert.Contains("УТОЧНЕНИЯ", dataOnly);
        Assert.DoesNotContain("ПЛАНИРОВКА", dataOnly);
    }

    [Fact]
    public void Build_always_includes_clarification_playbook()
    {
        var empty = AssistantSystemPrompt.Build(Array.Empty<string>(), null);
        Assert.Contains("УТОЧНЕНИЯ", empty);
        Assert.Contains("ask_user", empty);

        var norms = AssistantSystemPrompt.Build(new[] { ToolCatalog.Profiles.Norms }, "Покажи нарушения");
        Assert.Contains("УТОЧНЕНИЯ", norms);
        Assert.Contains("НОРМОКОНТРОЛЬ", norms);
    }

    [Fact]
    public void Clarification_lists_five_ask_cases_and_named_defaults()
    {
        Assert.Contains("ask_user", AssistantPlaybooks.Clarification);
        Assert.Contains("планировку", AssistantPlaybooks.Clarification, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("applyToAllFloorPlans", AssistantPlaybooks.Clarification);
        Assert.Contains("дефолт", AssistantPlaybooks.Clarification, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("двум комнатам", AssistantPlaybooks.Clarification, StringComparison.OrdinalIgnoreCase);
        for (var i = 1; i <= 5; i++)
            Assert.Contains(i + ")", AssistantPlaybooks.Clarification);
    }

    [Fact]
    public void Modeling_forbids_two_room_template_for_typology()
    {
        Assert.Contains("ТОЛЬКО если пользователь явно сказал", AssistantPlaybooks.Modeling);
        Assert.Contains("Школа", AssistantPlaybooks.Modeling);
        Assert.DoesNotContain("«Две комнаты»: ровно 5", AssistantPlaybooks.Modeling);
    }

    [Fact]
    public void Build_includes_only_matching_playbooks()
    {
        var norms = AssistantSystemPrompt.Build(new[] { ToolCatalog.Profiles.Norms }, "Покажи нарушения");
        Assert.Contains("НОРМОКОНТРОЛЬ", norms);
        Assert.DoesNotContain("ПЛАНИРОВКА", norms);

        var modeling = AssistantSystemPrompt.Build(new[] { ToolCatalog.Profiles.Modeling }, "Построй стены");
        Assert.Contains("ПЛАНИРОВКА", modeling);
        Assert.DoesNotContain("НОРМОКОНТРОЛЬ", modeling);
    }

    [Fact]
    public void Typology_playbook_attached_for_commercial_modeling()
    {
        var withCafe = AssistantSystemPrompt.Build(
            new[] { ToolCatalog.Profiles.Modeling },
            "Спроектируй кафе на 40 мест");
        Assert.Contains("ТИПОЛОГИИ", withCafe);

        var without = AssistantSystemPrompt.Build(
            new[] { ToolCatalog.Profiles.Modeling },
            "Построй перегородку в коридоре");
        Assert.DoesNotContain("ТИПОЛОГИИ", without);
    }

    [Fact]
    public void Norms_playbook_documents_report_plus_regions_not_highlight_only()
    {
        Assert.Contains("mode=report", AssistantPlaybooks.Norms);
        Assert.Contains("create_filled_regions", AssistantPlaybooks.Norms);
        Assert.DoesNotContain("mode=highlight", AssistantPlaybooks.Norms);
    }

    [Fact]
    public void Model_stats_prompt_forbids_export_room_data()
    {
        var prompt = AssistantSystemPrompt.Build(
            new[] { ToolCatalog.Profiles.Data },
            "Дай статистику модели: сколько стен, дверей, помещений.");
        Assert.Contains("analyze_model_statistics", prompt);
        Assert.Contains("не export_room_data", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.True(ExportRoomDataScopeRules.WantsModelStatistics(
            "Дай статистику модели: сколько стен, дверей, помещений."));
    }

    [Fact]
    public void Read_hints_playbook_attached_for_floor_room_count_queries()
    {
        var prompt = AssistantSystemPrompt.Build(Array.Empty<string>(), "Сколько помещений на этаже?");
        Assert.Contains("filterByActiveView", prompt);
    }

    [Fact]
    public void Prompt_schema_guardian_covers_playbooks()
    {
        var violations = PromptSchemaAlignment.FindPromptMismatches(
            PromptSchemaAlignment.CollectInstructionTexts());
        Assert.Empty(violations);
    }
}
