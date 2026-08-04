using System;
using revit_mcp_plugin.Configuration;
using revit_mcp_plugin.Core.Assistant;
using Xunit;

namespace revit_mcp_plugin.Tests.Assistant;

public class ModelRouterTests
{
    [Fact]
    public void Data_and_core_use_fast_model()
    {
        var model = ModelRouter.Resolve("gpt-4o-mini", "gpt-4o", new[] { "data" }, out var smart);
        Assert.Equal("gpt-4o-mini", model);
        Assert.False(smart);

        model = ModelRouter.Resolve("gpt-4o-mini", "gpt-4o", Array.Empty<string>(), out smart);
        Assert.Equal("gpt-4o-mini", model);
        Assert.False(smart);
    }

    [Theory]
    [InlineData("modeling")]
    [InlineData("annotation")]
    [InlineData("norms")]
    [InlineData("schedules")]
    [InlineData("sheets")]
    public void Smart_profiles_use_smart_model(string profile)
    {
        var model = ModelRouter.Resolve("gpt-4o-mini", "gpt-4o", new[] { "data", profile }, out var smart);
        Assert.Equal("gpt-4o", model);
        Assert.True(smart);
    }

    [Fact]
    public void Empty_smart_keeps_fast_even_for_modeling()
    {
        var model = ModelRouter.Resolve("gpt-4o-mini", "", new[] { "modeling" }, out var smart);
        Assert.Equal("gpt-4o-mini", model);
        Assert.False(smart);

        model = ModelRouter.Resolve("gpt-4o-mini", string.Empty, new[] { "norms" }, out smart);
        Assert.Equal("gpt-4o-mini", model);
        Assert.False(smart);
    }

    [Fact]
    public void Blank_fast_falls_back_to_default()
    {
        var model = ModelRouter.Resolve("  ", "gpt-4o", new[] { "data" }, out var smart);
        Assert.Equal(ModelRouter.DefaultFastModel, model);
        Assert.False(smart);
    }

    [Fact]
    public void CanEscalate_only_from_fast_when_smart_configured()
    {
        Assert.True(ModelRouter.CanEscalate("gpt-4o", currentlySmart: false));
        Assert.False(ModelRouter.CanEscalate("gpt-4o", currentlySmart: true));
        Assert.False(ModelRouter.CanEscalate("", currentlySmart: false));
        Assert.False(ModelRouter.CanEscalate((string)null!, currentlySmart: false));
    }

    [Fact]
    public void ServiceSettings_overload_reads_both_fields()
    {
        var settings = new ServiceSettings
        {
            AssistantModel = "mini-custom",
            AssistantModelSmart = "smart-custom",
        };

        var model = ModelRouter.Resolve(settings, new[] { "sheets" }, out var smart);
        Assert.Equal("smart-custom", model);
        Assert.True(smart);

        Assert.True(ModelRouter.CanEscalate(settings, currentlySmart: false));
        Assert.False(ModelRouter.CanEscalate(settings, currentlySmart: true));
    }

    [Fact]
    public void RequiresSmart_is_profile_based_not_text()
    {
        Assert.True(ModelRouter.RequiresSmart(new[] { ToolCatalog.Profiles.Norms }));
        Assert.False(ModelRouter.RequiresSmart(new[] { ToolCatalog.Profiles.Data }));
        Assert.False(ModelRouter.RequiresSmart(Array.Empty<string>()));
    }
}
