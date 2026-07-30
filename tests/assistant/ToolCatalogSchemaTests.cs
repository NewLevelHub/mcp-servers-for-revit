using System.Linq;
using Newtonsoft.Json.Linq;
using revit_mcp_plugin.Core.Assistant;
using Xunit;

namespace revit_mcp_plugin.Tests.Assistant;

public class ToolCatalogSchemaTests
{
    private static JObject ParametersOf(string toolName)
    {
        var tools = ToolCatalog.GetOpenAiTools();
        var match = tools.OfType<JObject>().FirstOrDefault(t =>
            t["function"]?["name"]?.ToString() == toolName);
        Assert.NotNull(match);
        var parameters = match!["function"]!["parameters"] as JObject;
        Assert.NotNull(parameters);
        return parameters!;
    }

    private static JArray? RequiredOf(JObject schema) =>
        schema["required"] as JArray;

    private static JObject Prop(JObject schema, string name)
    {
        var prop = schema["properties"]?[name] as JObject;
        Assert.NotNull(prop);
        return prop!;
    }

    [Theory]
    [InlineData("create_line_based_element", new[] { "data" })]
    [InlineData("create_point_based_element", new[] { "data" })]
    [InlineData("create_surface_based_element", new[] { "data" })]
    [InlineData("create_room", new[] { "data" })]
    [InlineData("operate_element", new[] { "data" })]
    [InlineData("set_element_parameter", new[] { "elementId", "parameterName", "value" })]
    [InlineData("get_element_parameters", new[] { "elementId" })]
    [InlineData("delete_element", new[] { "elementIds" })]
    [InlineData("place_view_on_sheet", new[] { "sheetId", "viewId" })]
    [InlineData("query_norm_rules", new[] { "topic" })]
    [InlineData("create_stair", new[] { "typeId" })]
    [InlineData("create_railing", new[] { "typeId" })]
    [InlineData("create_level", new[] { "name", "elevationMm" })]
    [InlineData("send_code_to_revit", new[] { "code" })]
    [InlineData("dimension_room_walls", new[] { "roomId" })]
    [InlineData("create_floor_opening", new[] { "mode" })]
    [InlineData("validate_schedule", new[] { "category" })]
    public void Tools_with_mandatory_args_declare_required(string toolName, string[] expected)
    {
        var parameters = ParametersOf(toolName);
        Assert.Equal(false, parameters["additionalProperties"]?.Value<bool>());
        var required = RequiredOf(parameters);
        Assert.NotNull(required);
        foreach (var key in expected)
            Assert.Contains(required!.Select(t => t.ToString()), x => x == key);
    }

    [Theory]
    [InlineData("run_norm_audit", "mode", new[] { "report", "highlight" })]
    [InlineData("validate_schedule", "category", new[] { "Doors", "Windows", "Floors", "CurtainWalls" })]
    [InlineData("create_floor_opening", "mode", new[] { "floor", "shaft" })]
    [InlineData("create_stair", "layout", new[] { "straight", "L", "U" })]
    [InlineData("dimension_room_walls", "placement", new[] { "interior", "exterior" })]
    [InlineData("create_grid", "bubbleEnd", new[] { "bottomLeft", "topRight", "both" })]
    [InlineData("configure_grid_display", "bubbleEnd", new[] { "bottomLeft", "topRight", "both" })]
    [InlineData("create_filled_regions", "colorPreset", new[] { "red", "green", "blue", "grey", "gray" })]
    [InlineData("check_evacuation_width", "mode", new[] { "report", "highlight" })]
    public void Enum_parameters_are_declared(string toolName, string propName, string[] values)
    {
        var prop = Prop(ParametersOf(toolName), propName);
        var en = prop["enum"] as JArray;
        Assert.NotNull(en);
        Assert.Equal(values.OrderBy(x => x), en!.Select(t => t.ToString()).OrderBy(x => x));
    }

    [Fact]
    public void Operate_element_action_has_enum()
    {
        var data = Prop(ParametersOf("operate_element"), "data");
        Assert.Equal(false, data["additionalProperties"]?.Value<bool>());
        var action = Prop(data, "action");
        var en = action["enum"] as JArray;
        Assert.NotNull(en);
        Assert.Contains(en!.Select(t => t.ToString()), x => x == "SetColor");
        Assert.Contains(en.Select(t => t.ToString()), x => x == "ResetOverrides");
        var required = RequiredOf(data);
        Assert.NotNull(required);
        Assert.Contains(required!.Select(t => t.ToString()), x => x == "action");
    }

    [Fact]
    public void Create_line_item_requires_typeId_and_locationLine()
    {
        var data = Prop(ParametersOf("create_line_based_element"), "data");
        var item = data["items"] as JObject;
        Assert.NotNull(item);
        var required = RequiredOf(item!);
        Assert.NotNull(required);
        Assert.Contains(required!.Select(t => t.ToString()), x => x == "category");
        Assert.Contains(required.Select(t => t.ToString()), x => x == "typeId");
        Assert.Contains(required.Select(t => t.ToString()), x => x == "locationLine");
    }

    [Fact]
    public void Create_point_item_requires_typeId_hostWallId_locationPoint()
    {
        var data = Prop(ParametersOf("create_point_based_element"), "data");
        var item = data["items"] as JObject;
        Assert.NotNull(item);
        var required = RequiredOf(item!);
        Assert.NotNull(required);
        Assert.Contains(required!.Select(t => t.ToString()), x => x == "typeId");
        Assert.Contains(required.Select(t => t.ToString()), x => x == "hostWallId");
        Assert.Contains(required.Select(t => t.ToString()), x => x == "locationPoint");
    }

    [Theory]
    [InlineData("analyze_model_statistics")]
    [InlineData("create_door_schedule")]
    [InlineData("create_window_schedule")]
    [InlineData("create_floor_schedule")]
    [InlineData("get_room_geometry_metrics")]
    [InlineData("get_material_quantities")]
    [InlineData("export_apartment_data")]
    [InlineData("create_curtain_wall_schedule")]
    [InlineData("tag_walls")]
    [InlineData("get_selected_elements")]
    [InlineData("get_document_styles")]
    [InlineData("say_hello")]
    [InlineData("get_current_view_info")]
    public void Empty_arg_tools_have_substantive_descriptions(string toolName)
    {
        var tools = ToolCatalog.GetOpenAiTools();
        var match = tools.OfType<JObject>().First(t =>
            t["function"]?["name"]?.ToString() == toolName);
        var desc = match["function"]?["description"]?.ToString() ?? "";
        Assert.True(desc.Length >= 40, $"{toolName} description too short: '{desc}'");
        Assert.NotEqual("Model statistics.", desc);
    }

    [Fact]
    public void All_parameter_objects_set_additionalProperties_false()
    {
        foreach (var tool in ToolCatalog.GetOpenAiTools().OfType<JObject>())
        {
            var name = tool["function"]?["name"]?.ToString();
            var parameters = tool["function"]?["parameters"] as JObject;
            Assert.NotNull(parameters);
            Assert.Equal(false, parameters!["additionalProperties"]?.Value<bool>());
            Assert.Equal("object", parameters["type"]?.ToString());
            Assert.NotNull(name);
        }
    }
}
