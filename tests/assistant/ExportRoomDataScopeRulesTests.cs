using revit_mcp_plugin.Core.Assistant;
using Xunit;

namespace revit_mcp_plugin.Tests.Assistant;

public class ExportRoomDataScopeRulesTests
{
    [Theory]
    [InlineData("Сколько помещений на этаже?")]
    [InlineData("Какие площади на плане?")]
    public void Floor_scoped_queries_inject_filter(string text) =>
        Assert.True(ExportRoomDataScopeRules.ShouldInjectActiveViewFilter(text));

    [Theory]
    [InlineData("Сколько всего помещений в проекте?")]
    [InlineData("Сколько комнат в здании целиком?")]
    [InlineData("Площади по всему зданию")]
    public void Project_wide_queries_do_not_inject_filter(string text) =>
        Assert.False(ExportRoomDataScopeRules.ShouldInjectActiveViewFilter(text));

    [Fact]
    public void Project_scope_wins_when_both_phrases_present() =>
        Assert.False(ExportRoomDataScopeRules.ShouldInjectActiveViewFilter(
            "Сколько помещений на этаже и сколько всего в проекте?"));
}
