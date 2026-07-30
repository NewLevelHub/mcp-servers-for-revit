using System.Linq;
using revit_mcp_plugin.Core.Assistant;
using Xunit;

namespace revit_mcp_plugin.Tests.Assistant;

public class ToolProfileTests
{
    [Theory]
    [InlineData("annotation")]
    [InlineData("modeling")]
    [InlineData("schedules")]
    [InlineData("sheets")]
    [InlineData("norms")]
    [InlineData("data")]
    public void Single_profile_plus_core_stays_within_cap(string profile)
    {
        var count = ToolCatalog.CountTools(new[] { profile });
        Assert.True(count <= ToolCatalog.MaxToolsPerRequest,
            $"{profile}: {count} tools > {ToolCatalog.MaxToolsPerRequest}");
        Assert.True(count >= ToolCatalog.CoreTools.Count);
    }

    [Fact]
    public void Core_only_when_profiles_null_or_empty()
    {
        Assert.Equal(ToolCatalog.CoreTools.Count, ToolCatalog.CountTools(null));
        Assert.Equal(ToolCatalog.CoreTools.Count, ToolCatalog.CountTools(System.Array.Empty<string>()));
    }

    [Fact]
    public void Full_catalog_unfiltered_exceeds_cap()
    {
        var all = ToolCatalog.GetOpenAiTools();
        Assert.True(all.Count > ToolCatalog.MaxToolsPerRequest);
        Assert.Equal(70, all.Count);
    }

    [Fact]
    public void Schedules_plus_sheets_under_cap()
    {
        var n = ToolCatalog.CountTools(new[]
        {
            ToolCatalog.Profiles.Schedules,
            ToolCatalog.Profiles.Sheets,
        });
        Assert.True(n <= ToolCatalog.MaxToolsPerRequest, $"got {n}");
        Assert.True(ToolCatalog.IsToolAllowed("render_tep_table",
            new[] { ToolCatalog.Profiles.Schedules, ToolCatalog.Profiles.Sheets }));
        Assert.True(ToolCatalog.IsToolAllowed("auto_layout_sheet",
            new[] { ToolCatalog.Profiles.Schedules, ToolCatalog.Profiles.Sheets }));
    }

    [Fact]
    public void Layout_triple_profile_is_capped_at_30()
    {
        var names = ToolCatalog.SelectToolNames(new[]
        {
            ToolCatalog.Profiles.Modeling,
            ToolCatalog.Profiles.Annotation,
            ToolCatalog.Profiles.Norms,
        });
        Assert.Equal(ToolCatalog.MaxToolsPerRequest, names.Count);
        // Modeling tools come before annotation/norms — walls stay available.
        Assert.Contains("create_line_based_element", names);
    }

    [Fact]
    public void Missing_profiles_for_tag_rooms_from_core_only()
    {
        var missing = ToolCatalog.GetMissingProfiles("tag_rooms", System.Array.Empty<string>());
        Assert.Contains(ToolCatalog.Profiles.Annotation, missing);
        Assert.False(ToolCatalog.IsToolAllowed("tag_rooms", System.Array.Empty<string>()));

        var merged = ToolCatalog.MergeProfiles(System.Array.Empty<string>(), missing);
        Assert.True(ToolCatalog.IsToolAllowed("tag_rooms", merged));
    }

    [Fact]
    public void Serialized_profile_catalog_much_smaller_than_full()
    {
        var fullLen = ToolCatalog.GetOpenAiTools().ToString(Newtonsoft.Json.Formatting.None).Length;
        var profileLen = ToolCatalog.GetOpenAiTools(new[] { ToolCatalog.Profiles.Annotation })
            .ToString(Newtonsoft.Json.Formatting.None).Length;
        // Schemas grew with required/enum (REV-113); still expect a clear profile win vs full catalog.
        Assert.True(profileLen * 1.6 < fullLen,
            $"profile {profileLen} should be clearly smaller than full {fullLen}");
    }

    [Theory]
    [InlineData("Поставь марки на все комнаты с площадью", "annotation")]
    [InlineData("Сделай ТЭП проекта на лист", "schedules")]
    [InlineData("Проверь этаж по нормам", "norms")]
    [InlineData("Покажи нарушения на этаже — покрась цветовой областью", "norms")]
    [InlineData("Дай статистику модели: сколько стен, дверей, помещений.", "data")]
    [InlineData("Проставь размеры помещений внутри комнат", "annotation")]
    [InlineData("Спроектируй планировку по нормам на этаже", "modeling")]
    public void Heuristic_routes_golden_phrases(string userText, string expectedProfile)
    {
        var profiles = IntentRouter.ResolveHeuristic(userText);
        Assert.Contains(expectedProfile, profiles);
        // Trap: layout-by-norms must NOT open norms audit profile.
        if (userText.Contains("планировку по нормам"))
            Assert.DoesNotContain(ToolCatalog.Profiles.Norms, profiles);
    }

    [Fact]
    public void Prioritize_brings_capped_tool_into_catalog()
    {
        var profiles = new[]
        {
            ToolCatalog.Profiles.Modeling,
            ToolCatalog.Profiles.Annotation,
            ToolCatalog.Profiles.Norms,
        };
        // modeling + annotation fill the 30-tool cap; norms tools are truncated.
        Assert.Equal(ToolCatalog.MaxToolsPerRequest, ToolCatalog.SelectToolNames(profiles).Count);
        Assert.False(ToolCatalog.IsToolAllowed("get_vertical_circulation_info", profiles));

        var reordered = ToolCatalog.PrioritizeProfilesForTool("get_vertical_circulation_info", profiles);
        Assert.True(ToolCatalog.IsToolAllowed("get_vertical_circulation_info", reordered));
    }

    [Fact]
    public void Catalog_has_no_exact_duplicates_or_internal_egress()
    {
        var names = ToolCatalog.GetOpenAiTools()
            .OfType<Newtonsoft.Json.Linq.JObject>()
            .Select(t => t["function"]?["name"]?.ToString())
            .Where(n => n != null)
            .ToList();
        Assert.True(names.Count <= 71);
        Assert.DoesNotContain("tag_all_rooms", names);
        Assert.DoesNotContain("tag_all_walls", names);
        Assert.DoesNotContain("color_elements", names);
        Assert.DoesNotContain("export_egress_graph", names);
        Assert.Contains("tag_rooms", names);
        Assert.Contains("tag_walls", names);
        Assert.Contains("color_splash", names);
    }

    [Theory]
    [InlineData("tag_all_rooms", "tag_rooms")]
    [InlineData("tag_all_walls", "tag_walls")]
    [InlineData("color_elements", "color_splash")]
    [InlineData("tag_rooms", "tag_rooms")]
    public void ResolveToolAlias_maps_legacy_names(string input, string expected)
    {
        Assert.Equal(expected, ToolCatalog.ResolveToolAlias(input));
    }

    [Fact]
    public void Alias_is_allowed_when_canonical_profile_active()
    {
        var profiles = new[] { ToolCatalog.Profiles.Annotation };
        Assert.True(ToolCatalog.IsToolAllowed("tag_all_rooms", profiles));
        Assert.True(ToolCatalog.IsToolAllowed("color_elements", profiles));
        Assert.Contains(ToolCatalog.Profiles.Annotation,
            ToolCatalog.GetMissingProfiles("tag_all_rooms", System.Array.Empty<string>()));
    }

    [Theory]
    [InlineData("fill_title_block", "штамп")]
    [InlineData("number_rooms", "нумерация")]
    [InlineData("export_egress_graph", "граф эвакуации")]
    public void DescribeUnavailableTool_explains_server_only(string tool, string hintFragment)
    {
        var msg = ToolCatalog.DescribeUnavailableTool(tool);
        Assert.Contains(hintFragment, msg, System.StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Инструмент недоступен в каталоге ассистента.", msg);
    }

    [Fact]
    public void ParseProfileReply_extracts_known_names()
    {
        var p = IntentRouter.ParseProfileReply("modeling annotation\nextra junk");
        Assert.Equal(2, p.Count);
        Assert.Equal("modeling", p[0]);
        Assert.Equal("annotation", p[1]);
    }
}
