using System.Linq;
using revit_mcp_plugin.Core.Assistant;
using Xunit;

namespace revit_mcp_plugin.Tests.Assistant;

public class PromptSchemaAlignmentTests
{
    [Fact]
    public void System_prompt_and_presets_only_reference_declared_tool_parameters()
    {
        var violations = PromptSchemaAlignment.FindPromptMismatches(
            PromptSchemaAlignment.CollectInstructionTexts());
        Assert.True(
            violations.Count == 0,
            "Prompt/schema mismatches:\n" + string.Join("\n", violations));
    }

    [Fact]
    public void NormCheckDefaults_enrichment_keys_are_declared_in_schemas()
    {
        var violations = PromptSchemaAlignment.FindEnrichmentMismatches();
        Assert.True(
            violations.Count == 0,
            "EnrichArgs/schema mismatches:\n" + string.Join("\n", violations));
    }

    [Fact]
    public void Guardian_catches_artificial_prompt_mismatch()
    {
        var violations = PromptSchemaAlignment.FindPromptMismatches(new[]
        {
            "run_norm_audit mode=highlight fakeParam=true"
        });
        Assert.Contains(violations, v => v.Contains("fakeParam"));
    }

    [Fact]
    public void Guardian_catches_artificial_enrichment_mismatch()
    {
        var fake = new Dictionary<string, IReadOnlyList<string>>
        {
            ["check_evacuation_width"] = new[] { "totallyFakeKey" }
        };
        var violations = PromptSchemaAlignment.FindEnrichmentMismatches(fake);
        Assert.Contains(violations, v => v.Contains("totallyFakeKey"));
    }

    [Theory]
    [InlineData("run_norm_audit", "annotate")]
    [InlineData("run_norm_audit", "filterByActiveView")]
    [InlineData("run_norm_audit", "levelId")]
    [InlineData("auto_layout_sheet", "items")]
    [InlineData("check_min_dimensions", "housingType")]
    [InlineData("check_evacuation_width", "filterByActiveView")]
    [InlineData("check_fire_doors", "viewId")]
    [InlineData("export_room_data", "filterByActiveView")]
    [InlineData("export_room_data", "levelName")]
    [InlineData("export_room_data", "levelId")]
    public void Rev117_mismatches_are_closed(string tool, string param)
    {
        var declared = ToolCatalog.GetParameterPropertyNames(tool);
        Assert.Contains(declared, p => p.Equals(param, System.StringComparison.OrdinalIgnoreCase));
    }
}
