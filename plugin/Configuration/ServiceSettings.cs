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

        /// <summary>openai = legacy Chat Completions; cursor = Cursor SDK bridge (REV-155).</summary>
        [JsonProperty("assistantBackend")]
        public string AssistantBackend { get; set; } = "cursor";

        /// <summary>Cursor API key (organization). Used when <see cref="AssistantBackend"/> = cursor.</summary>
        [JsonProperty("assistantCursorApiKey")]
        public string AssistantCursorApiKey { get; set; } = "";

        /// <summary>Cursor model id. "default" is the auto-router — see CursorModelCatalog.</summary>
        [JsonProperty("assistantCursorModel")]
        public string AssistantCursorModel { get; set; } = "default";

        /// <summary>Local HTTP port for assistant-bridge (default 8790).</summary>
        [JsonProperty("assistantBridgePort")]
        public int AssistantBridgePort { get; set; } = 8790;

        /// <summary>Optional path to node.exe (Node 22.13+).</summary>
        [JsonProperty("assistantNodePath")]
        public string AssistantNodePath { get; set; } = "";

        /// <summary>Optional path to assistant-bridge/dist/index.js.</summary>
        [JsonProperty("assistantBridgePath")]
        public string AssistantBridgePath { get; set; } = "";

        /// <summary>Optional cwd with .cursor/rules for Cursor agent.</summary>
        [JsonProperty("assistantRulesPath")]
        public string AssistantRulesPath { get; set; } = "";

        /// <summary>Optional path to mcp server/build/index.js.</summary>
        [JsonProperty("assistantMcpServerPath")]
        public string AssistantMcpServerPath { get; set; } = "";

        /// <summary>OpenAI-compatible API key (organization). Architect does not configure this.</summary>
        [JsonProperty("assistantApiKey")]
        public string AssistantApiKey { get; set; } = "";

        /// <summary>Base URL, e.g. https://api.openai.com/v1 or https://openrouter.ai/api/v1</summary>
        [JsonProperty("assistantApiBaseUrl")]
        public string AssistantApiBaseUrl { get; set; } = "https://api.openai.com/v1";

        /// <summary>
        /// Fast / default model (REV-124). Used for data/core reads and when smart is empty.
        /// </summary>
        [JsonProperty("assistantModel")]
        public string AssistantModel { get; set; } = "gpt-4o-mini";

        /// <summary>
        /// Stronger model for modeling / annotation / norms / schedules / sheets (REV-124).
        /// Empty = always use <see cref="AssistantModel"/> (backward compatible).
        /// </summary>
        [JsonProperty("assistantModelSmart")]
        public string AssistantModelSmart { get; set; } = "";

        /// <summary>
        /// Sampling temperature for chat.completions (REV-121). Default 0 for reliable tool-calling.
        /// </summary>
        [JsonProperty("assistantTemperature")]
        public double AssistantTemperature { get; set; } = 0;

        /// <summary>
        /// Optional max_tokens cap for completions (REV-121). Null or ≤0 = provider default.
        /// </summary>
        [JsonProperty("assistantMaxTokens")]
        public int? AssistantMaxTokens { get; set; } = 4096;

        /// <summary>
        /// When true, destructive actions may ask for confirmation in the chat pane (REV-125).
        /// Deletes confirm only when element count ≥ <see cref="AssistantConfirmDeleteThreshold"/>;
        /// send_code_to_revit always confirms. Creates, dimensions, tags run without a prompt.
        /// </summary>
        [JsonProperty("assistantRequireConfirmations")]
        public bool AssistantRequireConfirmations { get; set; } = true;

        /// <summary>
        /// Minimum number of elements for delete / operate Delete to show a confirmation card (REV-125).
        /// </summary>
        [JsonProperty("assistantConfirmDeleteThreshold")]
        public int AssistantConfirmDeleteThreshold { get; set; } = 20;

        /// <summary>
        /// How many previous user turns to keep in chat memory besides the current one (REV-126).
        /// Clamped to 4…20; default 12.
        /// </summary>
        [JsonProperty("assistantMaxPreviousUserTurns")]
        public int AssistantMaxPreviousUserTurns { get; set; } = 12;

        /// <summary>
        /// Where complaint packages are mirrored so they reach the maintainer without the
        /// architect doing anything: a UNC share (\\server\share\revit-ai-feedback) or a
        /// cloud-synced folder (Яндекс.Диск / OneDrive). Empty = keep them local only.
        /// </summary>
        [JsonProperty("assistantFeedbackDropDir")]
        public string AssistantFeedbackDropDir { get; set; } = "";

        /// <summary>
        /// Name shown on the package so a complaint can be traced back to a person.
        /// Empty falls back to the Windows account name.
        /// </summary>
        [JsonProperty("assistantFeedbackAuthor")]
        public string AssistantFeedbackAuthor { get; set; } = "";
    }
}
