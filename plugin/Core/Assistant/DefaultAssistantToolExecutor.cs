using System;
using Newtonsoft.Json.Linq;
using revit_mcp_plugin.Core;

namespace revit_mcp_plugin.Core.Assistant
{
    /// <summary>
    /// Production tool executor: local norm tools + SocketService JSON-RPC.
    /// </summary>
    public sealed class DefaultAssistantToolExecutor : IAssistantToolExecutor
    {
        public string Execute(string toolName, string argsJson)
        {
            if (toolName.Equals("query_norm_rules", StringComparison.OrdinalIgnoreCase))
                return NormCatalogStore.ExecuteQueryTool(argsJson);

            // Кнопки ленты — локальный файл, снятый при запуске Revit. Через Revit ходить незачем.
            if (toolName.Equals("query_revit_ui", StringComparison.OrdinalIgnoreCase))
                return RevitUiCatalog.ExecuteQueryTool(argsJson);

            if (toolName.Equals("declare_plan", StringComparison.OrdinalIgnoreCase))
                return AgentPlan.ExecuteAsTool(argsJson);

            if (toolName.Equals("ask_user", StringComparison.OrdinalIgnoreCase))
            {
                return WrapLocalToolResult(new JObject
                {
                    ["success"] = false,
                    ["error"] = "ask_user обрабатывается хостом агента, не через Revit RPC.",
                }.ToString(Newtonsoft.Json.Formatting.None));
            }

            if (toolName.Equals("run_norm_audit", StringComparison.OrdinalIgnoreCase))
                return WrapLocalToolResult(NormAuditOrchestrator.Run(argsJson));

            if (toolName.Equals("annotate_norm_findings", StringComparison.OrdinalIgnoreCase))
                return WrapLocalToolResult(AnnotateNormFindingsHelper.Run(argsJson));

            return SocketService.Instance.ExecuteJsonRpcLocal(toolName, argsJson);
        }

        private static string WrapLocalToolResult(string body)
        {
            if (string.IsNullOrWhiteSpace(body))
                return body;
            try
            {
                var jo = JObject.Parse(body);
                if (jo["result"] != null || jo["error"] != null)
                    return body;
                return new JObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = "local",
                    ["result"] = jo
                }.ToString(Newtonsoft.Json.Formatting.None);
            }
            catch
            {
                return body;
            }
        }
    }
}
