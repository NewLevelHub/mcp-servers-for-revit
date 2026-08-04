using System.Linq;
using revit_mcp_plugin.Core.Assistant;
using Xunit;

namespace revit_mcp_plugin.Tests.Assistant
{
    public class SseChatAssemblerTests
    {
        [Fact]
        public void Content_only_chunks_accumulate_text()
        {
            var a = new SseChatAssembler();
            a.ApplyChunkJson("{\"choices\":[{\"delta\":{\"content\":\"Привет\"}}]}");
            a.ApplyChunkJson("{\"choices\":[{\"delta\":{\"content\":\", мир\"},\"finish_reason\":\"stop\"}]}");

            Assert.Equal("Привет, мир", a.ContentSoFar);
            Assert.False(a.HasToolCalls);

            var completion = a.ToCompletion();
            Assert.Equal("Привет, мир", completion["choices"]![0]!["message"]!["content"]!.ToString());
            Assert.Equal("stop", completion["choices"]![0]!["finish_reason"]!.ToString());
        }

        [Fact]
        public void Tool_call_arguments_are_concatenated_across_chunks()
        {
            var a = new SseChatAssembler();
            a.ApplyChunkJson(
                "{\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call_1\",\"type\":\"function\"," +
                "\"function\":{\"name\":\"export_room_data\",\"arguments\":\"{\\\"lev\"}}]}}]}");
            a.ApplyChunkJson(
                "{\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"function\":" +
                "{\"arguments\":\"elName\\\":\\\"2 этаж\\\"}\"}}]},\"finish_reason\":\"tool_calls\"}]}");

            Assert.True(a.HasToolCalls);
            var msg = a.ToCompletion()["choices"]![0]!["message"]!;
            var content = msg["content"]?.ToString();
            Assert.True(string.IsNullOrEmpty(content));
            var tc = msg["tool_calls"]![0]!;
            Assert.Equal("call_1", tc["id"]!.ToString());
            Assert.Equal("export_room_data", tc["function"]!["name"]!.ToString());
            Assert.Equal("{\"levelName\":\"2 этаж\"}", tc["function"]!["arguments"]!.ToString());
            Assert.Equal("tool_calls", a.ToCompletion()["choices"]![0]!["finish_reason"]!.ToString());
        }

        [Fact]
        public void Multiple_tool_call_indexes_are_sorted()
        {
            var a = new SseChatAssembler();
            a.ApplyChunkJson(
                "{\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":1,\"id\":\"b\"," +
                "\"function\":{\"name\":\"second\",\"arguments\":\"{}\"}}]}}]}");
            a.ApplyChunkJson(
                "{\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"a\"," +
                "\"function\":{\"name\":\"first\",\"arguments\":\"{}\"}}]}}]}");

            var tools = a.ToCompletion()["choices"]![0]!["message"]!["tool_calls"]!;
            Assert.Equal(2, tools.Count());
            Assert.Equal("first", tools[0]!["function"]!["name"]!.ToString());
            Assert.Equal("second", tools[1]!["function"]!["name"]!.ToString());
        }

        [Fact]
        public void Done_payload_is_ignored()
        {
            var a = new SseChatAssembler();
            a.ApplyChunkJson("{\"choices\":[{\"delta\":{\"content\":\"ok\"}}]}");
            Assert.Equal("", a.ApplyChunkJson("[DONE]"));
            Assert.Equal("ok", a.ContentSoFar);
        }

        [Fact]
        public void Usage_is_captured_when_present()
        {
            var a = new SseChatAssembler();
            a.ApplyChunkJson(
                "{\"choices\":[{\"delta\":{\"content\":\"x\"}}]," +
                "\"usage\":{\"prompt_tokens\":10,\"completion_tokens\":3}}");
            var c = a.ToCompletion();
            Assert.True(OpenAiCompatibleClient.TryReadUsage(c, out var p, out var t));
            Assert.Equal(10, p);
            Assert.Equal(3, t);
        }
    }
}
