using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using revit_mcp_plugin.Configuration;
using revit_mcp_plugin.Core;

namespace revit_mcp_plugin.Core.Assistant
{
    public sealed class PendingToolConfirmation
    {
        public string ToolName { get; set; }
        public string Summary { get; set; }
        public string ArgumentsJson { get; set; }
    }

    public sealed class AgentTurnResult
    {
        public string Reply { get; set; }
        public bool Cancelled { get; set; }
        public bool Failed { get; set; }
        public IList<string> DoneSummary { get; set; } = new List<string>();
    }

    /// <summary>
    /// In-process agent: LLM + existing Revit JSON-RPC commands (same as MCP socket).
    /// </summary>
    public sealed class LocalAgentHost
    {
        public const string SystemPrompt =
            "Ты AI-ассистент архитектора внутри Autodesk Revit. " +
            "Отвечай кратко по-русски (1–3 предложения + при необходимости список «сделано»). " +
            "Не упоминай JSON, названия tool, MCP, Cursor, stack trace. " +
            "Работай только через доступные tools в открытой модели (команды Revit). " +
            "Для нормоконтроля вызывай check_evacuation_width / check_room_depth / check_min_dimensions / check_fire_doors, " +
            "затем create_filled_regions и create_text_notes. " +
            "Типы семейств бери из проекта. Нормы не выдумывай. " +
            "Единицы: мм, м², м³. Перед созданием при необходимости вызови get_current_view_info.";

        private readonly List<JObject> _history = new List<JObject>();
        private readonly object _historyLock = new object();

        public event Action<string> StatusChanged;
        public event Func<PendingToolConfirmation, Task<bool>> ConfirmationRequested;

        public void ClearHistory()
        {
            lock (_historyLock)
            {
                _history.Clear();
            }
        }

        public async Task<AgentTurnResult> RunAsync(
            string userMessage,
            string viewContext,
            CancellationToken cancellationToken)
        {
            var settings = PluginSettingsStore.LoadSettings();
            if (string.IsNullOrWhiteSpace(settings.AssistantApiKey))
            {
                return new AgentTurnResult
                {
                    Failed = true,
                    Reply = "Нужен API-ключ организации. Откройте Settings → Ассистент и вставьте ключ (это делает IT)."
                };
            }

            if (!SocketService.Instance.IsRunning)
            {
                return new AgentTurnResult
                {
                    Failed = true,
                    Reply = "Ассистент не запущен. Нажмите «Включить» на панели или Revit MCP Switch."
                };
            }

            var client = new OpenAiCompatibleClient(
                settings.AssistantApiKey,
                settings.AssistantApiBaseUrl,
                settings.AssistantModel);

            var tools = ToolCatalog.GetOpenAiTools();
            var done = new List<string>();

            lock (_historyLock)
            {
                if (_history.Count == 0)
                {
                    _history.Add(new JObject
                    {
                        ["role"] = "system",
                        ["content"] = SystemPrompt
                    });
                }

                var content = userMessage ?? "";
                if (!string.IsNullOrWhiteSpace(viewContext))
                    content = "[Контекст вида]\n" + viewContext + "\n\n[Запрос]\n" + content;

                _history.Add(new JObject
                {
                    ["role"] = "user",
                    ["content"] = content
                });
            }

            const int maxRounds = 12;
            for (var round = 0; round < maxRounds; round++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                RaiseStatus("Думает…");

                JArray messages;
                lock (_historyLock)
                {
                    messages = new JArray(_history.ToArray());
                }

                JObject completion;
                try
                {
                    completion = await client.ChatCompletionsAsync(messages, tools, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    return new AgentTurnResult
                    {
                        Failed = true,
                        Reply = ex.Message,
                        DoneSummary = done
                    };
                }

                var choice = completion["choices"]?[0]?["message"] as JObject;
                if (choice == null)
                {
                    return new AgentTurnResult
                    {
                        Failed = true,
                        Reply = "Пустой ответ от ИИ. Попробуйте ещё раз.",
                        DoneSummary = done
                    };
                }

                lock (_historyLock)
                {
                    _history.Add((JObject)choice.DeepClone());
                }

                var toolCalls = choice["tool_calls"] as JArray;
                if (toolCalls == null || toolCalls.Count == 0)
                {
                    var text = choice["content"]?.ToString()?.Trim();
                    if (string.IsNullOrWhiteSpace(text))
                        text = done.Count > 0 ? "Готово." : "Готово. Дополнительных действий не требуется.";

                    RaiseStatus("Готов");
                    return new AgentTurnResult
                    {
                        Reply = text,
                        DoneSummary = done
                    };
                }

                foreach (JToken callTok in toolCalls)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var call = callTok as JObject;
                    if (call == null) continue;

                    var callId = call["id"]?.ToString() ?? Guid.NewGuid().ToString("N");
                    var fn = call["function"] as JObject;
                    var name = fn?["name"]?.ToString() ?? "";
                    var argsJson = fn?["arguments"]?.ToString() ?? "{}";

                    if (settings.AssistantRequireConfirmations && ToolCatalog.RequiresConfirmation(name))
                    {
                        var pending = new PendingToolConfirmation
                        {
                            ToolName = name,
                            ArgumentsJson = argsJson,
                            Summary = BuildConfirmSummary(name, argsJson)
                        };

                        RaiseStatus("Нужно подтверждение");
                        var handler = ConfirmationRequested;
                        var approved = handler != null && await handler(pending).ConfigureAwait(false);
                        if (!approved)
                        {
                            var cancelPayload = new JObject
                            {
                                ["cancelled"] = true,
                                ["message"] = "Архитектор отменил действие."
                            };
                            AppendToolResult(callId, cancelPayload.ToString());
                            done.Add("отменено: " + name);
                            continue;
                        }
                    }

                    RaiseStatus("Выполняет…");
                    string rawResult;
                    try
                    {
                        rawResult = await Task.Run(
                            () => SocketService.Instance.ExecuteJsonRpcLocal(name, argsJson),
                            cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        rawResult = new JObject
                        {
                            ["jsonrpc"] = "2.0",
                            ["error"] = new JObject
                            {
                                ["message"] = ex.Message
                            }
                        }.ToString();
                    }

                    var (ok, summary, forModel) = ParseToolResponse(name, rawResult);
                    AppendToolResult(callId, forModel);
                    if (ok)
                        done.Add(summary);
                    else
                        done.Add("ошибка: " + summary);
                }
            }

            RaiseStatus("Готов");
            return new AgentTurnResult
            {
                Failed = true,
                Reply = "Слишком много шагов в одном запросе. Уточните задачу или разбейте на части.",
                DoneSummary = done
            };
        }

        private void AppendToolResult(string callId, string content)
        {
            lock (_historyLock)
            {
                _history.Add(new JObject
                {
                    ["role"] = "tool",
                    ["tool_call_id"] = callId,
                    ["content"] = content ?? ""
                });
            }
        }

        private void RaiseStatus(string status)
        {
            try { StatusChanged?.Invoke(status); }
            catch { /* UI may be disposed */ }
        }

        private static string BuildConfirmSummary(string toolName, string argsJson)
        {
            try
            {
                var args = JObject.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson);
                if (toolName.Equals("delete_element", StringComparison.OrdinalIgnoreCase))
                {
                    var ids = args["elementIds"] as JArray;
                    var n = ids?.Count ?? 0;
                    return $"Удалить элементы ({n} шт.)?";
                }
                if (toolName.StartsWith("create_", StringComparison.OrdinalIgnoreCase))
                    return $"Выполнить создание: {FriendlyToolName(toolName)}?";
                if (toolName.Equals("operate_element", StringComparison.OrdinalIgnoreCase))
                {
                    var action = args["data"]?["action"]?.ToString() ?? args["action"]?.ToString() ?? "изменить";
                    return $"Выполнить действие «{action}» над элементами?";
                }
                return $"Разрешить действие «{FriendlyToolName(toolName)}»?";
            }
            catch
            {
                return $"Разрешить действие «{FriendlyToolName(toolName)}»?";
            }
        }

        private static string FriendlyToolName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "операция";
            return name.Replace('_', ' ');
        }

        private static (bool ok, string summary, string forModel) ParseToolResponse(string toolName, string raw)
        {
            try
            {
                var jo = JObject.Parse(raw);
                if (jo["error"] != null)
                {
                    var msg = jo["error"]?["message"]?.ToString() ?? "ошибка";
                    var human = ToolCatalog.HumanizeFailure(toolName, msg);
                    return (false, human, new JObject { ["ok"] = false, ["error"] = human }.ToString());
                }

                var result = jo["result"];
                var compact = result == null ? "ok" : CompactResult(toolName, result);
                var forModel = result?.ToString(Newtonsoft.Json.Formatting.None) ?? "{\"ok\":true}";
                if (forModel.Length > 12000)
                    forModel = forModel.Substring(0, 12000) + "…";
                return (true, compact, forModel);
            }
            catch
            {
                var human = ToolCatalog.HumanizeFailure(toolName, raw);
                return (false, human, new JObject { ["ok"] = false, ["error"] = human }.ToString());
            }
        }

        private static string CompactResult(string toolName, JToken result)
        {
            if (result is JArray arr)
                return $"{FriendlyToolName(toolName)}: {arr.Count}";
            if (result is JObject obj)
            {
                if (obj["count"] != null) return $"{FriendlyToolName(toolName)}: {obj["count"]}";
                if (obj["created"] is JArray created) return $"{FriendlyToolName(toolName)}: {created.Count}";
                if (obj["Success"] != null || obj["success"] != null)
                {
                    var ok = obj["Success"]?.Value<bool>() ?? obj["success"]?.Value<bool>() ?? true;
                    return ok ? FriendlyToolName(toolName) : FriendlyToolName(toolName) + " (неуспех)";
                }
            }
            return FriendlyToolName(toolName);
        }
    }
}
