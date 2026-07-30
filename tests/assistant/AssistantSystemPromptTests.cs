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
        Assert.True(
            dataOnly.Length < AssistantSystemPrompt.LegacyMonolithLength * 3 / 5,
            $"data prompt {dataOnly.Length} should be < {AssistantSystemPrompt.LegacyMonolithLength * 3 / 5}");
        Assert.Contains("filterByActiveView", dataOnly);
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
