using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using revit_mcp_plugin.Core.Assistant;
using Xunit;

namespace revit_mcp_plugin.Tests.Assistant;

public class MissingTypeIdGuardTests
{
    [Fact]
    public void Wall_without_typeId_returns_teaching_error_with_wall_candidates()
    {
        var cache = FamilyTypesCache(
            Wall(101, "Базовая стена 200"),
            Wall(102, "Перегородка 100"),
            Curtain(999, "Витраж"));

        var check = MissingTypeIdGuard.Check(
            cache,
            "create_line_based_element",
            """{"data":[{"category":"OST_Walls","locationLine":{"p0":{"x":0,"y":0},"p1":{"x":3000,"y":0}},"height":3000}]}""");

        Assert.True(check.Missing);
        Assert.False(check.Payload!.Value<bool>("ok"));
        Assert.Contains("typeId", check.Error, System.StringComparison.OrdinalIgnoreCase);
        var candidates = (JArray)check.Payload["candidates"]!;
        Assert.NotEmpty(candidates);
        Assert.Contains(candidates.OfType<JObject>(), c => c.Value<long>("typeId") == 101);
        Assert.DoesNotContain(candidates.OfType<JObject>(), c => c.Value<long>("typeId") == 999);
    }

    [Fact]
    public void Point_without_typeId_never_offers_wall_candidates()
    {
        var cache = FamilyTypesCache(
            Wall(101, "Базовая стена 200"),
            Door(501, "Дверь однопольная 900"),
            Window(601, "Окно 1200"));

        var check = MissingTypeIdGuard.Check(
            cache,
            "create_point_based_element",
            """{"data":[{"hostWallId":42,"locationPoint":{"x":1000,"y":0,"z":0}}]}""");

        Assert.True(check.Missing);
        Assert.Contains("двер", check.Error, System.StringComparison.OrdinalIgnoreCase);
        var candidates = (JArray)check.Payload!["candidates"]!;
        Assert.NotEmpty(candidates);
        Assert.DoesNotContain(candidates.OfType<JObject>(), c => c.Value<long>("typeId") == 101);
        Assert.Contains(candidates.OfType<JObject>(), c => c.Value<long>("typeId") == 501);
        Assert.Contains(candidates.OfType<JObject>(), c => c.Value<long>("typeId") == 601);
    }

    [Fact]
    public void Point_without_typeId_and_only_walls_in_cache_has_empty_candidates()
    {
        var cache = FamilyTypesCache(Wall(101, "Базовая стена 200"));

        var check = MissingTypeIdGuard.Check(
            cache,
            "create_point_based_element",
            """{"data":[{"hostWallId":42,"locationPoint":{"x":0,"y":0,"z":0}}]}""");

        Assert.True(check.Missing);
        var candidates = (JArray)check.Payload!["candidates"]!;
        Assert.Empty(candidates);
        Assert.Contains("Не подставляй typeId стены", check.Payload["fix"]!.ToString());
    }

    [Fact]
    public void With_typeId_present_allows_call()
    {
        var cache = FamilyTypesCache(Wall(101, "Базовая стена 200"));

        var check = MissingTypeIdGuard.Check(
            cache,
            "create_line_based_element",
            """{"data":[{"category":"OST_Walls","typeId":101,"locationLine":{"p0":{"x":0,"y":0},"p1":{"x":1000,"y":0}}}]}""");

        Assert.False(check.Missing);
        Assert.Null(check.Payload);
    }

    [Fact]
    public void Non_create_tools_are_ignored()
    {
        var check = MissingTypeIdGuard.Check(
            new Dictionary<string, string>(),
            "create_room",
            """{"data":[{"name":"Кухня"}]}""");
        Assert.False(check.Missing);
    }

    [Fact]
    public void Surface_without_typeId_prefers_floor_candidates()
    {
        var cache = FamilyTypesCache(
            Wall(101, "Базовая стена"),
            Floor(701, "Пол типовой"));

        var check = MissingTypeIdGuard.Check(
            cache,
            "create_surface_based_element",
            """{"data":[{"boundary":[]}]}""");

        Assert.True(check.Missing);
        var candidates = (JArray)check.Payload!["candidates"]!;
        Assert.Single(candidates);
        Assert.Equal(701, candidates[0]!.Value<long>("typeId"));
    }

    private static Dictionary<string, string> FamilyTypesCache(params JObject[] types)
    {
        var payload = new JObject
        {
            ["ok"] = true,
            ["types"] = new JArray(types)
        };
        return new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase)
        {
            ["get_available_family_types|{}"] = payload.ToString(Newtonsoft.Json.Formatting.None)
        };
    }

    private static JObject Wall(long id, string name) => new()
    {
        ["typeId"] = id,
        ["name"] = name,
        ["familyName"] = "Базовая стена",
        ["category"] = "OST_Walls"
    };

    private static JObject Curtain(long id, string name) => new()
    {
        ["typeId"] = id,
        ["name"] = name,
        ["familyName"] = "Витражная система",
        ["category"] = "OST_Walls"
    };

    private static JObject Door(long id, string name) => new()
    {
        ["typeId"] = id,
        ["name"] = name,
        ["familyName"] = "Дверь",
        ["category"] = "OST_Doors"
    };

    private static JObject Window(long id, string name) => new()
    {
        ["typeId"] = id,
        ["name"] = name,
        ["familyName"] = "Окно",
        ["category"] = "OST_Windows"
    };

    private static JObject Floor(long id, string name) => new()
    {
        ["typeId"] = id,
        ["name"] = name,
        ["familyName"] = "Перекрытие",
        ["category"] = "OST_Floors"
    };
}
