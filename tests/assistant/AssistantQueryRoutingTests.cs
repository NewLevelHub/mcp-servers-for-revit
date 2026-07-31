using revit_mcp_plugin.Core.Assistant;
using Xunit;

namespace revit_mcp_plugin.Tests.Assistant;

public class AssistantQueryRoutingTests
{
    [Theory]
    [InlineData("Сколько глубина этого помещения на этаже?")]
    [InlineData("Какая глубина комнат на плане?")]
    public void Room_depth_queries_are_detected(string text) =>
        Assert.True(AssistantQueryRouting.WantsRoomDepthMetrics(text));

    [Theory]
    [InlineData("Проверь глубину жилых комнат по норме")]
    [InlineData("Сколько помещений на этаже?")]
    public void Non_metrics_depth_queries_are_not_detected(string text) =>
        Assert.False(AssistantQueryRouting.WantsRoomDepthMetrics(text));

    [Fact]
    public void Gost_door_schedule_query_is_detected() =>
        Assert.True(AssistantQueryRouting.WantsGostDoorSchedule(
            "На активном виде сделай ведомость по ГОСТ 21.501"));

    [Fact]
    public void Generic_schedule_without_gost_is_not_detected() =>
        Assert.False(AssistantQueryRouting.WantsGostDoorSchedule("Сделай спецификацию окон"));
}
