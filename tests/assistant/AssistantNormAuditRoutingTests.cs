using System.Linq;
using revit_mcp_plugin.Core.Assistant;
using Xunit;

namespace revit_mcp_plugin.Tests.Assistant;

public class AssistantNormAuditRoutingTests
{
    [Fact]
    public void Norm_audit_chip_runs_direct_preset()
    {
        Assert.True(AssistantNormAuditRouting.ShouldRunDirectNormAudit(AssistantNormAuditRouting.NormAuditChipId));
        Assert.False(AssistantNormAuditRouting.ShouldRunDirectNormAudit("axes_dims"));
    }

    [Theory]
    [MemberData(nameof(RoutingTrapCases))]
    public void Routing_traps_never_bypass_llm(string query)
    {
        Assert.False(AssistantNormAuditRouting.ShouldBypassLlmForUserText(query, hasAttachments: false));
        Assert.False(AssistantNormAuditRouting.ShouldBypassLlmForUserText(query, hasAttachments: true));
    }

    [Theory]
    [MemberData(nameof(RoutingTrapCases))]
    public void Routing_traps_would_have_matched_legacy_heuristic(string query)
    {
        Assert.True(AssistantNormAuditRouting.LegacySubstringHeuristicWouldMatch(query));
    }

    [Theory]
    [InlineData("Проверь этаж и подпиши нарушения")]
    [InlineData("Покажи нарушения на активном этаже")]
    public void Explicit_norm_highlight_still_goes_to_llm(string query)
    {
        Assert.False(AssistantNormAuditRouting.ShouldBypassLlmForUserText(query, hasAttachments: false));
    }

    public static TheoryData<string> RoutingTrapCases() =>
        new(AssistantNormAuditRouting.RoutingTrapQueries.ToArray());
}
