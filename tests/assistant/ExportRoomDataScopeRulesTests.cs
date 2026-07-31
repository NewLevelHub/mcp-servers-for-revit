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

    [Theory]
    [InlineData("Дай статистику модели: сколько стен, дверей, помещений.")]
    [InlineData("Статистика модели по проекту")]
    public void Model_statistics_do_not_inject_floor_filter(string text)
    {
        Assert.True(ExportRoomDataScopeRules.WantsModelStatistics(text));
        Assert.False(ExportRoomDataScopeRules.ShouldInjectActiveViewFilter(text));
    }

    [Fact]
    public void Room_depth_metrics_do_not_inject_export_room_data_filter() =>
        Assert.False(ExportRoomDataScopeRules.ShouldInjectActiveViewFilter(
            "Сколько глубина этого помещения на этаже?"));
}
