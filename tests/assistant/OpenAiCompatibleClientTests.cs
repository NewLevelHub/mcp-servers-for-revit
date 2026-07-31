using Newtonsoft.Json.Linq;
using revit_mcp_plugin.Core.Assistant;
using Xunit;

namespace revit_mcp_plugin.Tests.Assistant;

public class OpenAiCompatibleClientTests
{
    [Fact]
    public void TryReadUsage_reads_prompt_and_completion_tokens()
    {
        var completion = new JObject
        {
            ["usage"] = new JObject
            {
                ["prompt_tokens"] = 1200,
                ["completion_tokens"] = 340,
                ["total_tokens"] = 1540
            }
        };

        Assert.True(OpenAiCompatibleClient.TryReadUsage(completion, out var prompt, out var completionTok));
        Assert.Equal(1200, prompt);
        Assert.Equal(340, completionTok);
    }

    [Fact]
    public void TryReadUsage_returns_false_without_usage()
    {
        Assert.False(OpenAiCompatibleClient.TryReadUsage(new JObject(), out _, out _));
        Assert.False(OpenAiCompatibleClient.TryReadUsage(null!, out _, out _));
    }
}
