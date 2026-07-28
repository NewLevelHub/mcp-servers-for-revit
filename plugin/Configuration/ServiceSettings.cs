using Newtonsoft.Json;

namespace revit_mcp_plugin.Configuration
{
    /// <summary>
    /// <para>服务设置类</para>
    /// <para>Service settings.</para>
    /// </summary>
    public class ServiceSettings
    {
        /// <summary>
        /// <para>日志级别</para>
        /// <para>Log level.</para>
        /// </summary>
        [JsonProperty("logLevel")]
        public string LogLevel { get; set; } = "Info";

        /// <summary>
        /// <para>socket服务端口</para>
        /// <para>Socket service port.</para>
        /// </summary>
        [JsonProperty("port")]
        public int Port { get; set; } = 8080;

        /// <summary>
        /// <para>Автозапуск MCP-сервера при открытии Revit</para>
        /// <para>Automatically start the MCP server when Revit opens.</para>
        /// </summary>
        [JsonProperty("autoStartOnLaunch")]
        public bool AutoStartOnLaunch { get; set; } = true;

        /// <summary>OpenAI-compatible API key (organization). Architect does not configure this.</summary>
        [JsonProperty("assistantApiKey")]
        public string AssistantApiKey { get; set; } = "";

        /// <summary>Base URL, e.g. https://api.openai.com/v1 or https://openrouter.ai/api/v1</summary>
        [JsonProperty("assistantApiBaseUrl")]
        public string AssistantApiBaseUrl { get; set; } = "https://api.openai.com/v1";

        /// <summary>Model id for the in-Revit assistant.</summary>
        [JsonProperty("assistantModel")]
        public string AssistantModel { get; set; } = "gpt-4o-mini";

        /// <summary>
        /// When true, only delete / send_code_to_revit ask for confirmation in the chat pane.
        /// Creates, dimensions, tags run without a prompt.
        /// </summary>
        [JsonProperty("assistantRequireConfirmations")]
        public bool AssistantRequireConfirmations { get; set; } = false;
    }
}
