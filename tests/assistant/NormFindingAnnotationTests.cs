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

    [Fact]
    public void Findings_on_one_element_stack_into_a_single_note()
    {
        // A stair failing both march width and tread used to get two notes, and
        // two leaders drawn on top of each other to the same point.
        var findings = JArray.Parse("""
            [
              {
                "status": "violation",
                "elementId": 77,
                "name": "Лестница",
                "metric": "ширина марша",
                "actualMm": 1200,
                "requiredMm": 1350,
                "source": { "document": "SP RK 3.06-31-2005", "clause": "п. 9.7" }
              },
              {
                "status": "violation",
                "elementId": 77,
                "name": "Лестница",
                "metric": "проступь",
                "actualMm": 250,
                "requiredMm": 300,
                "source": { "document": "СП РК 3.06-101", "clause": "п. 4.3.2.27" }
              }
            ]
            """);

        var grouped = NormAnnotationText.GroupByElement(findings);

        var note = Assert.Single(grouped);
        Assert.Equal(77, note.ElementId);
        Assert.Equal(
            new[]
            {
                "Лестница: ширина марша 1200 < 1350 мм · SP RK 3.06-31-2005 п. 9.7",
                // Name printed once — line 2 carries only the metric and its source.
                "проступь 250 < 300 мм · СП РК 3.06-101 п. 4.3.2.27"
            },
            note.Lines);
    }

    [Fact]
    public void Duplicate_finding_does_not_repeat_the_line()
    {
        var findings = JArray.Parse("""
            [
              { "status": "violation", "elementId": 5, "name": "Дверь",
                "metric": "ширина", "actualMm": 800, "requiredMm": 900 },
              { "status": "violation", "elementId": 5, "name": "Дверь",
                "metric": "ширина", "actualMm": 800, "requiredMm": 900 }
            ]
            """);

        var note = Assert.Single(NormAnnotationText.GroupByElement(findings));
        Assert.Single(note.Lines);
    }

    [Fact]
    public void Compliant_findings_and_missing_ids_are_left_out()
    {
        var findings = JArray.Parse("""
            [
              { "status": "compliant", "elementId": 1, "name": "OK" },
              { "status": "violation", "elementId": 0, "name": "Без id" },
              { "status": "nearLimit", "elementId": 9, "name": "Тамбур",
                "actualMm": 1600, "requiredMm": 1650 }
            ]
            """);

        var note = Assert.Single(NormAnnotationText.GroupByElement(findings));
        Assert.Equal(9, note.ElementId);
    }

    [Fact]
    public void Float_mm_noise_rounds_to_whole_millimeters()
    {
        var finding = new JObject
        {
            ["name"] = "Гостиная",
            ["actualMm"] = 6868.9999999995,
            ["requiredMm"] = 6000.0,
            ["source"] = new JObject
            {
                ["document"] = "СП РК 3.02-101-2012",
                ["clause"] = "п. 4.4.10-22"
            }
        };

        var text = NormAnnotationText.Format(finding);
        Assert.Equal("Гостиная: 6869 > 6000 мм · СП РК 3.02-101-2012 п. 4.4.10-22", text);
        Assert.DoesNotContain("99999", text);
        Assert.DoesNotContain(",", text); // no locale decimal junk in mm display
    }
}
