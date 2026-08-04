using System;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json.Linq;

namespace revit_mcp_plugin.Core.Assistant
{
    /// <summary>
    /// Assembles OpenAI chat.completions SSE chunks into a final message object (REV-127).
    /// Handles incremental content and fragmented <c>tool_calls</c> arguments.
    /// </summary>
    public sealed class SseChatAssembler
    {
        private readonly StringBuilder _content = new StringBuilder();
        private readonly Dictionary<int, ToolCallBuilder> _tools = new Dictionary<int, ToolCallBuilder>();
        private string _finishReason;
        private string _model;
        private int _promptTokens;
        private int _completionTokens;
        private bool _sawUsage;

        /// <summary>Accumulated assistant text so far.</summary>
        public string ContentSoFar => _content.ToString();

        public bool HasToolCalls => _tools.Count > 0;

        /// <summary>
        /// Apply one SSE <c>data:</c> JSON payload (not the raw <c>data: </c> prefix).
        /// Returns content delta appended this chunk (may be empty).
        /// </summary>
        public string ApplyChunkJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json) || json == "[DONE]")
                return "";

            JObject chunk;
            try
            {
                chunk = JObject.Parse(json);
            }
            catch
            {
                return "";
            }

            if (chunk["model"] != null)
                _model = chunk["model"]?.ToString() ?? _model;

            if (chunk["usage"] is JObject usage)
            {
                _sawUsage = true;
                _promptTokens = usage["prompt_tokens"]?.Value<int>() ?? _promptTokens;
                _completionTokens = usage["completion_tokens"]?.Value<int>() ?? _completionTokens;
            }

            var choice = chunk["choices"]?[0] as JObject;
            if (choice == null)
                return "";

            var finish = choice["finish_reason"]?.ToString();
            if (!string.IsNullOrEmpty(finish) && finish != "null")
                _finishReason = finish;

            var delta = choice["delta"] as JObject;
            if (delta == null)
                return "";

            var contentDelta = delta["content"]?.ToString();
            if (!string.IsNullOrEmpty(contentDelta))
                _content.Append(contentDelta);

            if (delta["tool_calls"] is JArray toolDeltas)
            {
                foreach (var td in toolDeltas)
                {
                    var obj = td as JObject;
                    if (obj == null) continue;
                    var index = obj["index"]?.Value<int>() ?? 0;
                    if (!_tools.TryGetValue(index, out var builder))
                    {
                        builder = new ToolCallBuilder();
                        _tools[index] = builder;
                    }

                    if (obj["id"] != null)
                        builder.Id = obj["id"]?.ToString() ?? builder.Id;

                    if (obj["type"] != null)
                        builder.Type = obj["type"]?.ToString() ?? builder.Type;

                    var fn = obj["function"] as JObject;
                    if (fn != null)
                    {
                        if (fn["name"] != null)
                            builder.Name = fn["name"]?.ToString() ?? builder.Name;
                        if (fn["arguments"] != null)
                            builder.Arguments.Append(fn["arguments"]?.ToString() ?? "");
                    }
                }
            }

            return contentDelta ?? "";
        }

        /// <summary>
        /// Build a chat.completions-shaped <see cref="JObject"/> compatible with the non-stream parser.
        /// </summary>
        public JObject ToCompletion()
        {
            var message = new JObject
            {
                ["role"] = "assistant",
            };

            var text = _content.ToString();
            if (!string.IsNullOrEmpty(text))
                message["content"] = text;
            else if (!HasToolCalls)
                message["content"] = "";
            // tool_calls-only: omit content (OpenAI allows null/absent)

            if (HasToolCalls)
            {
                var arr = new JArray();
                foreach (var kv in SortedTools())
                {
                    var b = kv.Value;
                    arr.Add(new JObject
                    {
                        ["id"] = string.IsNullOrEmpty(b.Id) ? ("call_" + kv.Key) : b.Id,
                        ["type"] = string.IsNullOrEmpty(b.Type) ? "function" : b.Type,
                        ["function"] = new JObject
                        {
                            ["name"] = b.Name ?? "",
                            ["arguments"] = b.Arguments.ToString(),
                        }
                    });
                }
                message["tool_calls"] = arr;
            }

            var result = new JObject
            {
                ["id"] = "chatcmpl-stream",
                ["object"] = "chat.completion",
                ["model"] = _model ?? "",
                ["choices"] = new JArray
                {
                    new JObject
                    {
                        ["index"] = 0,
                        ["message"] = message,
                        ["finish_reason"] = _finishReason
                            ?? (HasToolCalls ? "tool_calls" : "stop"),
                    }
                }
            };

            if (_sawUsage)
            {
                result["usage"] = new JObject
                {
                    ["prompt_tokens"] = _promptTokens,
                    ["completion_tokens"] = _completionTokens,
                    ["total_tokens"] = _promptTokens + _completionTokens,
                };
            }

            return result;
        }

        private List<KeyValuePair<int, ToolCallBuilder>> SortedTools()
        {
            var list = new List<KeyValuePair<int, ToolCallBuilder>>(_tools);
            list.Sort((a, b) => a.Key.CompareTo(b.Key));
            return list;
        }

        private sealed class ToolCallBuilder
        {
            public string Id;
            public string Type;
            public string Name;
            public readonly StringBuilder Arguments = new StringBuilder();
        }
    }
}
