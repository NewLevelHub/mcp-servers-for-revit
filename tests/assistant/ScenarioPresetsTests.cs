using revit_mcp_plugin.Core.Assistant;
using Xunit;

namespace revit_mcp_plugin.Tests.Assistant;

public class ScenarioPresetsTests
{
    private static readonly string[] ExpectedIds =
    {
        "axes_dims",
        "rooms_tags",
        "norm_audit",
    };

    [Fact]
    public void Pilot_ContainsOnlyThreeChips()
    {
        var ids = ScenarioPresets.Pilot.Select(p => p.Id).ToList();
        Assert.Equal(ExpectedIds, ids);
    }

    [Fact]
    public void Pilot_EachPreset_HasHintProfilesAndPrompt()
    {
        foreach (var p in ScenarioPresets.Pilot)
        {
            Assert.False(string.IsNullOrWhiteSpace(p.Label), p.Id);
            Assert.False(string.IsNullOrWhiteSpace(p.Prompt), p.Id);
            Assert.False(string.IsNullOrWhiteSpace(p.Hint), p.Id);
            Assert.NotNull(p.Profiles);
            Assert.NotEmpty(p.Profiles);
        }
    }

    [Fact]
    public void BuildAgentMessage_UsesEditedUserPrompt()
    {
        var preset = ScenarioPresets.Pilot.First(p => p.Id == "rooms_tags");
        var msg = ScenarioPresets.BuildAgentMessage(preset, "Только марки с площадью");
        Assert.StartsWith("Только марки с площадью", msg);
        Assert.Contains("tag_rooms", msg);
    }

    [Fact]
    public void NormAudit_AgentInstruction_HasNoAnnotateTrue()
    {
        var preset = ScenarioPresets.Pilot.First(p => p.Id == "norm_audit");
        Assert.DoesNotContain("annotate=true", preset.AgentInstruction ?? "");
        Assert.Contains("mode=report", preset.AgentInstruction);
    }
}
