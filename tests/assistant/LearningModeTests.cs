using System;
using System.Linq;
using revit_mcp_plugin.Core.Assistant;
using Xunit;

namespace revit_mcp_plugin.Tests.Assistant;

/// <summary>
/// REV-153: режим наставника держится на каталоге, а не на уговорах в промпте.
/// Эти тесты проверяют то, что нельзя доверить формулировке: новичку физически
/// не выдаются инструменты, которые меняют его модель.
/// </summary>
public class LearningModeTests
{
    private static readonly string[] Learning = { ToolCatalog.Profiles.Learning };

    [Theory]
    [InlineData("delete_element")]
    [InlineData("operate_element")]
    [InlineData("set_element_parameter")]
    [InlineData("create_line_based_element")]
    [InlineData("create_point_based_element")]
    [InlineData("create_room")]
    [InlineData("send_code_to_revit")]
    public void Writing_tools_are_unavailable_in_learning_mode(string tool)
    {
        Assert.False(ToolCatalog.IsToolAllowed(tool, Learning),
            $"{tool} доступен новичку — режим наставника держится только на промпте");
    }

    [Fact]
    public void Learning_mode_replaces_core_instead_of_extending_it()
    {
        var names = ToolCatalog.SelectToolNames(Learning);

        // Пишущие инструменты живут в core и попали бы сюда при обычном сложении профилей.
        Assert.DoesNotContain("delete_element", names);
        Assert.DoesNotContain("operate_element", names);
        Assert.DoesNotContain("set_element_parameter", names);

        // А читающие — на месте, иначе шаг урока нечем проверить.
        Assert.Contains("get_current_view_info", names);
        Assert.Contains("export_room_data", names);
        Assert.Contains("ask_user", names);
    }

    [Fact]
    public void Every_learning_tool_exists_in_the_catalog()
    {
        // Опечатка в белом списке молча урезала бы набор наставника.
        var names = ToolCatalog.SelectToolNames(Learning);
        Assert.Equal(ToolCatalog.LearningTools.Count, names.Count);
    }

    [Fact]
    public void Learning_mode_cannot_be_escaped_by_calling_a_writing_tool()
    {
        // Иначе агент сам выдаст себе профиль modeling и построит стену за новичка.
        Assert.Empty(ToolCatalog.GetMissingProfiles("create_line_based_element", Learning));
        Assert.False(
            ToolCatalog.IsToolAllowed(
                "create_line_based_element",
                ToolCatalog.PrioritizeProfilesForTool("create_line_based_element", Learning)));
    }

    [Fact]
    public void Full_profile_does_not_silently_enable_learning()
    {
        var normalized = ToolCatalog.NormalizeProfiles(new[] { "full" });
        Assert.False(ToolCatalog.IsLearningMode(normalized));
        Assert.True(ToolCatalog.IsToolAllowed("create_room", normalized));
    }

    [Theory]
    [InlineData("Где найти кнопку стены?")]
    [InlineData("Как поставить дверь?")]
    [InlineData("Не могу найти, куда нажать для размеров")]
    [InlineData("Объясни, что такое уровень")]
    [InlineData("Научи меня делать помещения")]
    [InlineData("В какой вкладке находится марка?")]
    public void Questions_about_how_and_where_go_to_the_tutor(string userText)
    {
        var profiles = IntentRouter.ResolveHeuristic(userText);
        Assert.Equal(new[] { ToolCatalog.Profiles.Learning }, profiles.ToArray());
    }

    [Theory]
    [InlineData("Построй две комнаты")]
    [InlineData("Поставь дверь между комнатами")]
    [InlineData("Проставь размеры на этаже")]
    [InlineData("Сделай ТЭП")]
    public void Orders_still_go_to_work_profiles(string userText)
    {
        // «Сделай» — это поручение. Урок вместо работы разозлит проектировщика.
        var profiles = IntentRouter.ResolveHeuristic(userText);
        Assert.False(ToolCatalog.IsLearningMode(profiles), $"«{userText}» ушло в обучение");
        Assert.NotEmpty(profiles);
    }

    [Fact]
    public void Tutor_playbook_replaces_other_manners()
    {
        var playbook = AssistantPlaybooks.Build(Learning);
        Assert.Equal(AssistantPlaybooks.Tutor, playbook);
        Assert.DoesNotContain("declare_plan", playbook);
    }

    [Fact]
    public void Tutor_toggle_overrides_whatever_the_router_or_a_chip_asked_for()
    {
        // REV-154: иначе достаточно нажать чип «Оси и размеры», чтобы обойти обучение.
        var fromChip = new[] { ToolCatalog.Profiles.Annotation, ToolCatalog.Profiles.Modeling };
        var resolved = TutorMode.ResolveProfiles(enabled: true, requested: fromChip);

        Assert.Equal(new[] { ToolCatalog.Profiles.Learning }, resolved.ToArray());
        Assert.False(ToolCatalog.IsToolAllowed("create_line_based_element", resolved));
    }

    [Fact]
    public void Turning_the_toggle_off_removes_learning_from_the_request()
    {
        // Обычная работа с урезанным до чтения каталогом выглядела бы как поломка.
        var resolved = TutorMode.ResolveProfiles(
            enabled: false,
            requested: new[] { ToolCatalog.Profiles.Learning, ToolCatalog.Profiles.Modeling });

        Assert.NotNull(resolved);
        Assert.False(ToolCatalog.IsLearningMode(resolved));
        Assert.True(ToolCatalog.IsToolAllowed("create_line_based_element", resolved));
    }

    [Fact]
    public void Toggle_off_with_nothing_requested_leaves_routing_alone()
    {
        // null = «профили не заданы, пусть решает роутер», и это не то же самое,
        // что пустой список: пустой список означал бы «только core».
        Assert.Null(TutorMode.ResolveProfiles(enabled: false, requested: null));
        Assert.Null(TutorMode.ResolveProfiles(
            enabled: false,
            requested: new[] { ToolCatalog.Profiles.Learning }));
    }

    [Fact]
    public void Tutor_chip_asks_for_the_learning_profile()
    {
        var chip = ScenarioPresets.Pilot.Single(p => p.Id == "learn_revit");
        Assert.Equal(new[] { ToolCatalog.Profiles.Learning }, chip.Profiles);
        Assert.False(ToolCatalog.IsToolAllowed("create_room", chip.Profiles));
    }

    [Fact]
    public void System_prompt_in_learning_mode_drops_the_clarification_block()
    {
        var prompt = AssistantSystemPrompt.Build(Learning);
        Assert.Contains("РЕЖИМ НАСТАВНИКА", prompt);
        Assert.DoesNotContain(AssistantPlaybooks.Clarification, prompt);

        // А в обычном режиме блок уточнений остаётся на месте.
        var normal = AssistantSystemPrompt.Build(new[] { ToolCatalog.Profiles.Modeling });
        Assert.Contains(AssistantPlaybooks.Clarification, normal);
        Assert.DoesNotContain("РЕЖИМ НАСТАВНИКА", normal);
    }
}
