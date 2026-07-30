using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace revit_mcp_plugin.Core.Assistant
{
    /// <summary>
    /// OpenAI-compatible chat completions. Injectable for golden dry-run (REV-111).
    /// </summary>
    public interface IChatCompletionsClient
    {
        Task<JObject> ChatCompletionsAsync(
            JArray messages,
            JArray tools,
            CancellationToken cancellationToken);
    }
}
