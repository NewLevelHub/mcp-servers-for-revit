using System;
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

    /// <summary>
    /// Optional SSE streaming (REV-127). Golden stubs keep <see cref="IChatCompletionsClient"/> only.
    /// </summary>
    public interface IStreamingChatCompletionsClient : IChatCompletionsClient
    {
        /// <summary>
        /// Stream chat.completions; <paramref name="onContentDelta"/> receives cumulative assistant text
        /// (empty when only tool_calls). Returns the same shape as non-stream <c>ChatCompletionsAsync</c>.
        /// </summary>
        Task<JObject> ChatCompletionsStreamingAsync(
            JArray messages,
            JArray tools,
            Action<string> onContentDelta,
            CancellationToken cancellationToken);
    }
}
