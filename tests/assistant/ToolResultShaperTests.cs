using System.Linq;
using Newtonsoft.Json.Linq;
using revit_mcp_plugin.Core.Assistant;
using Xunit;

namespace revit_mcp_plugin.Tests.Assistant;

public class ToolResultShaperTests
{
    [Fact]
    public void ExportRoomData_SummaryHasAggregates_ItemsCapped()
    {
        var rooms = new JArray();
        for (var i = 1; i <= 120; i++)
        {
            rooms.Add(new JObject
            {
                ["id"] = 1000 + i,
                ["name"] = $"Комната {i}",
                ["number"] = i.ToString(),
                ["level"] = "2 этаж",
                ["area"] = 10.5,
                ["volume"] = 30.0,
                ["perimeter"] = 12000,
                ["comments"] = new string('x', 200)
            });
        }

        var raw = new JObject
        {
            ["success"] = true,
            ["totalRooms"] = 120,
            ["totalArea"] = 1260.0,
            ["levelName"] = "2 этаж",
            ["filteredBy"] = "levelName",
            ["totalInProject"] = 572,
            ["rooms"] = rooms
        };

        var shaped = ToolResultShaper.Shape("export_room_data", raw);
        var json = ToolResultShaper.EnsureUnderBudget(shaped);

        Assert.True(shaped.Value<bool>("ok"));
        Assert.Contains("120", shaped["summary"]!.ToString());
        Assert.Equal(1260.0, shaped.Value<double>("totalArea"));
        Assert.Contains("2 этаж", shaped["summary"]!.ToString());
        Assert.Equal(120, shaped.Value<int>("count"));
        Assert.True(((JArray)shaped["items"]!).Count <= 20);
        Assert.Equal(120, shaped["truncated"]!["total"]!.Value<int>());
        Assert.NotNull(JObject.Parse(json));
        Assert.True(json.Length <= ToolResultShaper.DefaultMaxChars);
    }

    [Fact]
    public void ViewElements_SummaryUsesFilteredCount_NotItemRecount()
    {
        var elements = new JArray();
        for (var i = 0; i < 80; i++)
            elements.Add(Element(i, "Walls", "Стены"));
        for (var i = 0; i < 40; i++)
            elements.Add(Element(1000 + i, "Dimensions", "Размеры"));
        for (var i = 0; i < 20; i++)
            elements.Add(Element(2000 + i, "Rooms", "Помещения"));

        var raw = new JObject
        {
            ["ViewId"] = 42,
            ["ViewName"] = "2 этаж",
            ["TotalElementsInView"] = 959,
            ["FilteredElementCount"] = 140,
            ["TotalCount"] = 140,
            ["HasMore"] = false,
            ["Elements"] = elements
        };

        var shaped = ToolResultShaper.Shape("get_current_view_elements", raw);
        var json = ToolResultShaper.EnsureUnderBudget(shaped);

        Assert.Equal(140, shaped.Value<int>("count"));
        Assert.Equal(140, shaped.Value<int>("filteredElementCount"));
        Assert.Contains("140", shaped["summary"]!.ToString());
        Assert.Contains("Стены", shaped["summary"]!.ToString());
        Assert.Equal(80, shaped["categoryCounts"]!["Стены"]!.Value<int>());
        Assert.Equal(40, shaped["categoryCounts"]!["Размеры"]!.Value<int>());
        Assert.True(((JArray)shaped["items"]!).Count <= 20);
        Assert.Null(shaped["items"]![0]?["Properties"]);
        Assert.True(json.Length <= ToolResultShaper.DefaultMaxChars);
        Assert.NotNull(JObject.Parse(json));
    }

    [Fact]
    public void EnsureUnderBudget_NeverReturnsInvalidJson()
    {
        var huge = new JObject
        {
            ["ok"] = true,
            ["summary"] = "test",
            ["count"] = 500,
            ["items"] = new JArray(Enumerable.Range(0, 500).Select(i => new JObject
            {
                ["id"] = i,
                ["name"] = new string('A', 100),
                ["blob"] = new string('B', 200)
            }))
        };

        // Force over budget without prior shape trim of fields.
        huge["truncated"] = new JObject { ["shown"] = 500, ["total"] = 500, ["hint"] = "x" };
        Assert.True(huge.ToString(Newtonsoft.Json.Formatting.None).Length > ToolResultShaper.DefaultMaxChars);

        var json = ToolResultShaper.EnsureUnderBudget(huge);
        var parsed = JObject.Parse(json);
        Assert.True(parsed.Value<bool>("ok"));
        Assert.True(json.Length <= ToolResultShaper.DefaultMaxChars);
    }

    [Fact]
    public void FailurePayload_HasErrorAndFix()
    {
        var hint = ToolCatalog.DescribeFailure("create_line_based_element", "typeId is required");
        var fail = ToolResultShaper.FailurePayload(hint);

        Assert.False(fail.Value<bool>("ok"));
        Assert.False(string.IsNullOrWhiteSpace(fail["error"]?.ToString()));
        Assert.False(string.IsNullOrWhiteSpace(fail["fix"]?.ToString()));
        Assert.Contains("typeId", fail["error"]!.ToString() + fail["fix"]!.ToString());
    }

    [Fact]
    public void AutoHighlight_AppendedToSummary()
    {
        var raw = new JObject
        {
            ["success"] = true,
            ["Message"] = "3 violations",
            ["violations"] = new JArray(),
            ["autoHighlight"] = new JObject
            {
                ["roomCount"] = 5,
                ["doorCount"] = 2
            }
        };

        var shaped = ToolResultShaper.Shape("check_room_depth", raw);
        Assert.Contains("залито 5", shaped["summary"]!.ToString());
        Assert.Equal(5, shaped["autoHighlight"]!["roomCount"]!.Value<int>());
        Assert.Equal(2, shaped["autoHighlight"]!["doorCount"]!.Value<int>());
    }

    [Fact]
    public void NormAudit_SlimFindings_NoLongQuote()
    {
        var findings = new JArray();
        for (var i = 0; i < 40; i++)
        {
            findings.Add(new JObject
            {
                ["checkType"] = "corridor_width",
                ["status"] = "violation",
                ["elementId"] = 3000 + i,
                ["name"] = $"Коридор {i}",
                ["actualMm"] = 1000,
                ["requiredMm"] = 1200,
                ["source"] = new JObject
                {
                    ["document"] = "СП",
                    ["clause"] = "4.1",
                    ["quote"] = new string('Q', 500)
                }
            });
        }

        var raw = new JObject
        {
            ["success"] = true,
            ["summary"] = new JObject
            {
                ["violations"] = 40,
                ["nearLimit"] = 0,
                ["compliant"] = 10,
                ["skipped"] = 1
            },
            ["findings"] = findings,
            ["roomIds"] = new JArray(1, 2, 3),
            ["displayHint"] = "Для подсветки: create_filled_regions"
        };

        var shaped = ToolResultShaper.Shape("run_norm_audit", raw);
        var json = ToolResultShaper.EnsureUnderBudget(shaped);

        Assert.Contains("Нарушений: 40", shaped["summary"]!.ToString());
        Assert.True(((JArray)shaped["items"]!).Count <= 30);
        Assert.Null(shaped["items"]![0]?["source"]?["quote"]);
        Assert.Contains("create_filled_regions", shaped["nextStep"]!.ToString());
        Assert.True(json.Length <= ToolResultShaper.DefaultMaxChars);
    }

    [Fact]
    public void FamilyTypes_SuggestsWallType()
    {
        var types = new JArray
        {
            new JObject { ["typeId"] = 11, ["name"] = "Дверь 900", ["familyName"] = "Door", ["category"] = "Doors" },
            new JObject { ["typeId"] = 22, ["name"] = "Стена 200", ["familyName"] = "Basic Wall", ["category"] = "Walls" }
        };

        var shaped = ToolResultShaper.Shape("get_available_family_types", types);
        Assert.Equal(22, shaped["suggestedWallTypeId"]!.Value<int>());
        Assert.Equal(2, shaped.Value<int>("count"));
    }

    [Fact]
    public void ModelStatistics_KeepsKeyCategories()
    {
        var cats = new JArray
        {
            new JObject { ["categoryName"] = "Стены", ["elementCount"] = 239 },
            new JObject { ["categoryName"] = "Помещения", ["elementCount"] = 572 },
            new JObject { ["categoryName"] = "Двери", ["elementCount"] = 80 },
            new JObject
            {
                ["categoryName"] = "Other",
                ["elementCount"] = 1,
                ["types"] = new JArray(Enumerable.Range(0, 100).Select(i => new JObject
                {
                    ["typeName"] = $"T{i}",
                    ["instanceCount"] = i
                }))
            }
        };

        var raw = new JObject
        {
            ["projectName"] = "Test",
            ["totalElements"] = 900,
            ["categories"] = cats
        };

        var shaped = ToolResultShaper.Shape("analyze_model_statistics", raw);
        var json = ToolResultShaper.EnsureUnderBudget(shaped);

        Assert.Contains("Стены: 239", shaped["summary"]!.ToString());
        Assert.Contains("Помещения: 572", shaped["summary"]!.ToString());
        Assert.DoesNotContain("types", shaped["categories"]![0]!.ToString());
        Assert.True(json.Length <= ToolResultShaper.DefaultMaxChars);
    }

    private static JObject Element(int id, string categoryEn, string categoryRu)
    {
        return new JObject
        {
            ["Id"] = id,
            ["UniqueId"] = $"uid-{id}",
            ["Name"] = $"{categoryEn} {id}",
            ["Category"] = categoryRu,
            ["Properties"] = new JObject
            {
                ["Mark"] = "M",
                ["Comments"] = new string('c', 80),
                ["StartMm"] = new JObject { ["X"] = 0, ["Y"] = 0, ["Z"] = 0 }
            }
        };
    }
}
