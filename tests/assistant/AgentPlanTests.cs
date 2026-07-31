using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using revit_mcp_plugin.Core.Assistant;
using Xunit;

namespace revit_mcp_plugin.Tests.Assistant;

public class AgentPlanTests
{
    [Fact]
    public void TryParse_reads_goal_and_steps()
    {
        var ok = AgentPlan.TryParse(
            """{"goal":"Кафе 60 м²","steps":[{"n":1,"what":"Типы стен","tool":"get_available_family_types"},{"n":2,"what":"Стены","tool":"create_line_based_element"}]}""",
            out var plan,
            out var error);

        Assert.True(ok, error);
        Assert.Equal("Кафе 60 м²", plan!.Goal);
        Assert.Equal(2, plan.Steps.Count);
        Assert.Equal("create_line_based_element", plan.Steps[1].Tool);
    }

    [Fact]
    public void TryParse_rejects_empty_goal()
    {
        var ok = AgentPlan.TryParse("""{"goal":"","steps":[{"what":"x"}]}""", out _, out var error);
        Assert.False(ok);
        Assert.Contains("goal", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ExecuteAsTool_returns_shaped_ok_payload()
    {
        var raw = AgentPlan.ExecuteAsTool(
            """{"goal":"План","steps":[{"n":1,"what":"Вид","tool":"get_current_view_info"}]}""");
        var jo = JObject.Parse(raw);
        Assert.True(jo["ok"]!.Value<bool>());
        Assert.Contains("План", jo["summary"]!.ToString());
        Assert.Equal(1, jo["count"]!.Value<int>());
        Assert.NotNull(jo["nextStep"]);
    }

    [Fact]
    public void TryMarkTool_marks_matching_pending_step()
    {
        Assert.True(AgentPlan.TryParse(
            """{"goal":"G","steps":[{"n":1,"what":"A","tool":"get_current_view_info"},{"n":2,"what":"B","tool":"create_room"}]}""",
            out var plan, out _));

        Assert.True(plan!.TryMarkTool("get_current_view_info", ok: true));
        Assert.Equal("done", plan.Steps[0].Status);
        Assert.Equal("pending", plan.Steps[1].Status);

        Assert.True(plan.TryMarkTool("create_room", ok: false));
        Assert.Equal("failed", plan.Steps[1].Status);
    }

    [Fact]
    public void TryMarkTool_success_recovers_failed_step()
    {
        Assert.True(AgentPlan.TryParse(
            """{"goal":"G","steps":[{"n":1,"what":"Двери","tool":"create_point_based_element"},{"n":2,"what":"Комнаты","tool":"create_room"}]}""",
            out var plan, out _));

        Assert.True(plan!.TryMarkTool("create_point_based_element", ok: false));
        Assert.Equal("failed", plan.Steps[0].Status);

        Assert.True(plan.TryMarkTool("create_point_based_element", ok: true));
        Assert.Equal("done", plan.Steps[0].Status);
        Assert.Equal("pending", plan.Steps[1].Status);
    }

    [Fact]
    public void BuildPartialReply_lists_done_and_pending()
    {
        Assert.True(AgentPlan.TryParse(
            """{"goal":"G","steps":[{"n":1,"what":"Стены","tool":"create_line_based_element"},{"n":2,"what":"Двери","tool":"create_point_based_element"}]}""",
            out var plan, out _));
        plan!.TryMarkTool("create_line_based_element", true);

        var reply = AgentPlan.BuildPartialReply(
            "Слишком много шагов.",
            new[] { "стены / линейные элементы" },
            plan);

        Assert.Contains("Слишком много шагов", reply);
        Assert.Contains("Успели:", reply);
        Assert.Contains("Не успели:", reply);
        Assert.Contains("Двери", reply);
        Assert.Contains("Продолжить с шага 2", reply);
    }
}

public class RoundBudgetTests
{
    [Fact]
    public void Modeling_gets_complex_budget()
    {
        Assert.Equal(RoundBudget.Complex,
            RoundBudget.Resolve(new[] { ToolCatalog.Profiles.Modeling }, "Построй стены"));
    }

    [Fact]
    public void Norms_gets_normal_budget()
    {
        Assert.Equal(RoundBudget.Normal,
            RoundBudget.Resolve(new[] { ToolCatalog.Profiles.Norms }, "Покажи нарушения"));
    }

    [Fact]
    public void Core_or_data_gets_simple_budget()
    {
        Assert.Equal(RoundBudget.Simple, RoundBudget.Resolve(Array.Empty<string>(), "Сколько комнат?"));
        Assert.Equal(RoundBudget.Simple,
            RoundBudget.Resolve(new[] { ToolCatalog.Profiles.Data }, "Сколько помещений на этаже?"));
    }
}

public class ToolCallLoopGuardTests
{
    [Fact]
    public void Allows_first_two_identical_calls_blocks_third()
    {
        var guard = new ToolCallLoopGuard();
        Assert.True(guard.TryAllow("export_room_data", """{"filterByActiveView":true}""", out var c1));
        Assert.Equal(1, c1);
        Assert.True(guard.TryAllow("export_room_data", """{"filterByActiveView":true}""", out var c2));
        Assert.Equal(2, c2);
        Assert.False(guard.TryAllow("export_room_data", """{"filterByActiveView":true}""", out var c3));
        Assert.Equal(3, c3);
    }

    [Fact]
    public void Different_args_do_not_share_counter()
    {
        var guard = new ToolCallLoopGuard();
        Assert.True(guard.TryAllow("get_available_family_types", """{"categoryName":"OST_Walls"}""", out _));
        Assert.True(guard.TryAllow("get_available_family_types", """{"categoryName":"OST_Doors"}""", out _));
        Assert.True(guard.TryAllow("get_available_family_types", """{"categoryName":"OST_Walls"}""", out var c));
        Assert.Equal(2, c);
    }

    [Fact]
    public void BlockPayload_is_shaped_failure()
    {
        var jo = ToolCallLoopGuard.BlockPayload("export_room_data");
        Assert.False(jo["ok"]!.Value<bool>());
        Assert.Contains("Повторный", jo["error"]!.ToString());
        Assert.False(string.IsNullOrWhiteSpace(jo["fix"]?.ToString()));
    }
}
