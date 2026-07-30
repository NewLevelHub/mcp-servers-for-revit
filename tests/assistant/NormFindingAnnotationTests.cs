using Newtonsoft.Json.Linq;
using revit_mcp_plugin.Core.Assistant;
using Xunit;

namespace revit_mcp_plugin.Tests.Assistant;

/// <summary>REV-130 regression: callouts must not be bare room names.</summary>
public class NormFindingAnnotationTests
{
    [Fact]
    public void Room_depth_violation_maps_depth_and_parent_max_and_source()
    {
        var parent = JObject.Parse("""
            {
              "maxDepthMm": 6000,
              "source": {
                "document": "СП РК 3.02-101-2012",
                "clause": "п. 4.4.10.22",
                "quote": "Глубина жилых комнат … не более 6 м."
              }
            }
            """);
        var item = JObject.Parse("""
            {
              "id": 209245,
              "name": "Гостиная",
              "depthMm": 6500,
              "widthMm": 3200
            }
            """);

        var finding = NormFindingMapper.Normalize(item, "check_room_depth", "violation", parent);

        Assert.Equal(6500d, finding["actualMm"]!.Value<double>());
        Assert.Equal(6000d, finding["requiredMm"]!.Value<double>());
        Assert.Equal("СП РК 3.02-101-2012", finding["source"]!["document"]!.ToString());

        var text = NormAnnotationText.Format(finding);
        Assert.Contains("Гостиная", text);
        Assert.Contains("6500", text);
        Assert.Contains("6000", text);
        Assert.Contains("мм", text);
        Assert.Contains("СП РК", text);
        Assert.True(NormAnnotationText.HasUsefulContent(finding));
    }

    [Fact]
    public void Prefer_depth_over_width_for_room_depth()
    {
        var item = new JObject
        {
            ["id"] = 1,
            ["name"] = "Гостиная",
            ["widthMm"] = 3000,
            ["depthMm"] = 7200
        };
        var parent = new JObject { ["maxDepthMm"] = 6000 };
        var finding = NormFindingMapper.Normalize(item, "check_room_depth", "violation", parent);
        Assert.Equal(7200d, finding["actualMm"]!.Value<double>());
    }

    [Fact]
    public void Bare_name_without_limits_is_not_useful_annotation()
    {
        var finding = new JObject
        {
            ["name"] = "Гостиная",
            ["elementId"] = 1,
            ["status"] = "violation"
        };
        Assert.Equal("Гостиная", NormAnnotationText.Format(finding));
        Assert.False(NormAnnotationText.HasUsefulContent(finding));
    }
}
