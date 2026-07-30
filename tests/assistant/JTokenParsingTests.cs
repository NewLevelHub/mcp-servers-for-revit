using Newtonsoft.Json.Linq;
using revit_mcp_plugin.Core.Assistant;
using Xunit;

namespace revit_mcp_plugin.Tests.Assistant;

public class JTokenParsingTests
{
    [Theory]
    [InlineData("209245", 209245L)]
    [InlineData(209245, 209245L)]
    [InlineData("abc", null)]
    public void GetLong_accepts_numeric_and_string_tokens(object raw, long? expected)
    {
        var token = raw is string s ? JValue.FromObject(s) : JToken.FromObject(raw);
        Assert.Equal(expected, JTokenParsing.GetLong(token));
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData(1, true)]
    [InlineData(0, false)]
    public void GetBool_accepts_bool_string_and_numeric_tokens(object raw, bool expected)
    {
        var token = raw is string s ? JValue.FromObject(s) : JToken.FromObject(raw);
        Assert.Equal(expected, JTokenParsing.GetBool(token));
    }

    [Fact]
    public void FirstLong_reads_string_element_id_from_finding_shape()
    {
        var finding = JObject.Parse("""
            {"status":"violation","checkType":"check_min_dimensions","elementId":"209245"}
            """);

        var id = JTokenParsing.FirstLong(finding, "elementId", "ElementId", "id", "Id");

        Assert.Equal(209245L, id);
    }
}
