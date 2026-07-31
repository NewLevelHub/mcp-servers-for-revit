using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
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
        /// <summary>Core-only prompt; production uses <see cref="BuildSystemPrompt"/> per turn.</summary>
        public const string SystemPrompt = AssistantSystemPrompt.Core;

        public static string BuildSystemPrompt(IReadOnlyList<string> profiles, string userText = null) =>
            AssistantSystemPrompt.Build(profiles, userText);

        private readonly List<JObject> _history = new List<JObject>();
        private readonly object _historyLock = new object();
        private readonly IAssistantToolExecutor _toolExecutor;
        private string _sessionId = Guid.NewGuid().ToString("N").Substring(0, 12);

        public LocalAgentHost()
            : this(null)
        {
        }

        /// <param name="toolExecutor">
        /// Optional. Golden dry-run / tests inject a stub; production uses
        /// <see cref="DefaultAssistantToolExecutor"/>.
        /// </param>
        public LocalAgentHost(IAssistantToolExecutor toolExecutor)
        {
            _toolExecutor = toolExecutor ?? new DefaultAssistantToolExecutor();
        }

        /// <summary>
        /// Keep this many previous user turns (+ the current one is never trimmed).
        /// One scenario creates many assistant/tool messages — counting raw messages was too aggressive.
        /// </summary>
        public const int MaxPreviousUserTurns = 4;

        /// <summary>Rough character budget for all non-system content sent to the model.</summary>
        public const int MaxHistoryChars = 120000;

        /// <summary>Cap each tool result stored in history (full payloads blow the budget in one round).</summary>
        public const int MaxToolResultChars = 4000;

        /// <summary>Obsolete name kept for any external references; prefer MaxPreviousUserTurns.</summary>
        public const int MaxRecentMessages = 80;

        public event Action<string> StatusChanged;
        public event Func<PendingToolConfirmation, Task<bool>> ConfirmationRequested;
        /// <summary>Raised when older messages were dropped to keep context within limits.</summary>
        public event Action HistoryTrimmed;
        /// <summary>REV-120: plan checklist declared or a step status changed.</summary>
        public event Action<AgentPlanSnapshot> PlanChanged;

        public int HistoryMessageCount
        {
            get
            {
                lock (_historyLock)
                {
                    return _history.Count;
                }
            }
        }

        public void ClearHistory()
        {
            lock (_historyLock)
            {
                _history.Clear();
            }
            _sessionId = Guid.NewGuid().ToString("N").Substring(0, 12);
        }

        private static void ParseViewContext(string ctx, TurnLogEntry log)
        {
            if (string.IsNullOrWhiteSpace(ctx)) return;
            try
            {
                // Format: "Документ: X · Вид: Y (ViewType) · Уровень: Z"
                foreach (var part in ctx.Split(new[] { " · " }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var colon = part.IndexOf(':');
                    if (colon < 0) continue;
                    var key = part.Substring(0, colon).Trim();
                    var val = part.Substring(colon + 1).Trim();
                    if (key.StartsWith("Документ")) log.DocTitle = val;
                    else if (key.StartsWith("Вид")) log.ViewName = val;
                    else if (key.StartsWith("Уровень")) log.LevelName = val;
                }
            }
            catch { /* best-effort */ }
        }

        /// <summary>
        /// Drop oldest complete user turns so the prompt stays within budgets.
        /// Never splits assistant tool_calls from their tool results; never trims the current turn.
        /// </summary>
        public bool TrimHistoryIfNeeded()
        {
            lock (_historyLock)
            {
                return TrimHistoryUnlocked();
            }
        }

        private bool TrimHistoryUnlocked()
        {
            if (_history.Count == 0)
                return false;

            EnsureSystemFirstUnlocked();
            var droppedUserTurns = false;

            while (NeedsTrimUnlocked())
            {
                // Prefer stripping base64 from older turns before dropping whole turns.
                // Compaction alone does NOT mean the chat is "full" for the user.
                if (CompactMultimodalUnlocked(keepLastUserIntact: true))
                    continue;

                // Drop the oldest complete turn (user … next user), never the latest user turn.
                var userIndexes = new List<int>();
                for (var i = 0; i < _history.Count; i++)
                {
                    if (IsRole(_history[i], "user"))
                        userIndexes.Add(i);
                }

                // Need at least one older turn besides the current (last) user message.
                if (userIndexes.Count <= 1)
                {
                    // Only current turn left — shrink oversized tool payloads instead of breaking structure.
                    if (!CompactOldestToolPayloadUnlocked())
                        break;
                    continue;
                }

                var dropFrom = userIndexes[0];
                var dropToExclusive = userIndexes[1];
                var removeCount = dropToExclusive - dropFrom;
                if (removeCount <= 0)
                    break;

                _history.RemoveRange(dropFrom, removeCount);
                droppedUserTurns = true;
            }

            SanitizeToolPairsUnlocked();
            EnsureSystemFirstUnlocked();
            return droppedUserTurns;
        }

        private bool NeedsTrimUnlocked()
        {
            var userTurns = 0;
            var chars = 0;
            foreach (var m in _history)
            {
                if (IsRole(m, "user"))
                    userTurns++;
                chars += EstimateChars(m);
            }

            // userTurns includes the current request; allow that + MaxPreviousUserTurns older ones.
            return userTurns > MaxPreviousUserTurns + 1 || chars > MaxHistoryChars;
        }

        private void EnsureSystemFirstUnlocked()
        {
            var system = _history.Find(m => IsRole(m, "system"));
            if (system == null)
                return;

            _history.RemoveAll(m => IsRole(m, "system"));
            _history.Insert(0, system);
        }

        /// <summary>
        /// OpenAI requires every tool message to follow an assistant message with matching tool_calls.
        /// Drop orphans left by older buggy trims or partial failures.
        /// </summary>
        private void SanitizeToolPairsUnlocked()
        {
            var i = 0;
            while (i < _history.Count)
            {
                var msg = _history[i];
                if (!IsRole(msg, "tool"))
                {
                    i++;
                    continue;
                }

                var prev = i - 1;
                while (prev >= 0 && IsRole(_history[prev], "tool"))
                    prev--;

                var ok = false;
                if (prev >= 0 && IsRole(_history[prev], "assistant"))
                {
                    var calls = _history[prev]["tool_calls"] as JArray;
                    var callId = msg["tool_call_id"]?.ToString();
                    if (calls != null && !string.IsNullOrEmpty(callId))
                    {
                        foreach (var c in calls)
                        {
                            if (string.Equals(c?["id"]?.ToString(), callId, StringComparison.Ordinal))
                            {
                                ok = true;
                                break;
                            }
                        }
                    }
                }

                if (!ok)
                {
                    _history.RemoveAt(i);
                    continue;
                }

                i++;
            }

            // Incomplete crash/cancel recovery: assistant tool_calls missing any tool result.
            // OpenAI rejects the next turn if even one call id has no matching tool message.
            for (var idx = _history.Count - 1; idx >= 0; idx--)
            {
                var assistantMsg = _history[idx];
                if (!IsRole(assistantMsg, "assistant"))
                    continue;
                var calls = assistantMsg["tool_calls"] as JArray;
                if (calls == null || calls.Count == 0)
                    continue;

                var needed = new HashSet<string>(StringComparer.Ordinal);
                foreach (var c in calls)
                {
                    var id = c?["id"]?.ToString();
                    if (!string.IsNullOrEmpty(id))
                        needed.Add(id);
                }

                if (needed.Count == 0)
                {
                    _history.RemoveAt(idx);
                    continue;
                }

                var end = idx + 1;
                while (end < _history.Count && IsRole(_history[end], "tool"))
                {
                    var callId = _history[end]["tool_call_id"]?.ToString();
                    if (!string.IsNullOrEmpty(callId))
                        needed.Remove(callId);
                    end++;
                }

                if (needed.Count > 0)
                {
                    // Drop the incomplete assistant + its partial tool results.
                    _history.RemoveRange(idx, end - idx);
                }
            }
        }

        private bool CompactOldestToolPayloadUnlocked()
        {
            for (var i = 0; i < _history.Count; i++)
            {
                if (!IsRole(_history[i], "tool"))
                    continue;
                var content = _history[i]["content"]?.ToString() ?? "";
                if (content.Length <= 400)
                    continue;
                _history[i]["content"] = "{\"ok\":true,\"truncated\":true}";
                return true;
            }
            return false;
        }

        private static bool IsRole(JObject message, string role)
        {
            return string.Equals(message?["role"]?.ToString(), role, StringComparison.OrdinalIgnoreCase);
        }

        private void EnsureSystemPromptUnlocked(string content)
        {
            if (_history.Count == 0)
            {
                _history.Add(new JObject
                {
                    ["role"] = "system",
                    ["content"] = content ?? AssistantSystemPrompt.Core,
                });
                return;
            }

            if (IsRole(_history[0], "system"))
                _history[0]["content"] = content ?? AssistantSystemPrompt.Core;
            else
            {
                _history.Insert(0, new JObject
                {
                    ["role"] = "system",
                    ["content"] = content ?? AssistantSystemPrompt.Core,
                });
            }
        }

        private static int EstimateChars(JObject message)
        {
            if (message == null) return 0;
            var n = 0;
            var content = message["content"];
            if (content is JArray parts)
            {
                // Do NOT count raw base64 — one photo/PDF would fake "context full"
                // and drop earlier chat turns. Attachments cost a fixed budget here.
                foreach (var part in parts)
                {
                    var type = part?["type"]?.ToString() ?? "";
                    if (string.Equals(type, "text", StringComparison.OrdinalIgnoreCase))
                        n += part["text"]?.ToString()?.Length ?? 0;
                    else if (string.Equals(type, "image_url", StringComparison.OrdinalIgnoreCase))
                        n += 3000;
                    else if (string.Equals(type, "file", StringComparison.OrdinalIgnoreCase))
                        n += 8000;
                    else
                        n += 500;
                }
            }
            else if (content != null)
            {
                n += content.ToString().Length;
            }

            var toolCalls = message["tool_calls"]?.ToString();
            if (toolCalls != null) n += Math.Min(toolCalls.Length, 4000);
            return n;
        }

        /// <summary>
        /// Cap tool/history payloads without mid-JSON cuts (REV-119). Prefer shaping first.
        /// </summary>
        private static string TruncateForHistory(string content)
        {
            if (string.IsNullOrEmpty(content))
                return content ?? "";
            if (content.Length <= MaxToolResultChars)
                return content;

            try
            {
                var token = JToken.Parse(content);
                if (token is JObject jo)
                    return ToolResultShaper.EnsureUnderBudget(jo, MaxToolResultChars);
                if (token is JArray arr)
                {
                    var wrapped = new JObject
                    {
                        ["ok"] = true,
                        ["summary"] = $"элементов: {arr.Count}",
                        ["count"] = arr.Count,
                        ["items"] = new JArray(arr.Take(ToolResultShaper.DefaultItemLimit))
                    };
                    return ToolResultShaper.EnsureUnderBudget(wrapped, MaxToolResultChars);
                }
            }
            catch
            {
                // Non-JSON — keep a safe prefix (should be rare after Shape).
            }

            return content.Substring(0, MaxToolResultChars) + "…";
        }

        /// <summary>
        /// Build OpenAI multimodal user content: text (+ extracted office text) + image_url.
        /// Documents are inlined as text when possible — Chat Completions <c>file</c> parts
        /// are unreliable on many OpenAI-compatible proxies.
        /// PDF without local text still goes as a <c>file</c> part (needs OpenAI-compatible file support).
        /// </summary>
        internal static JToken BuildUserContent(string text, IList<ChatAttachment> attachments)
        {
            if (attachments == null || attachments.Count == 0)
                return text ?? "";

            var usable = attachments.Where(a => a?.Data != null && a.Data.Length > 0).ToList();
            var docCount = usable.Count(a => !a.IsImage);
            // Share extract budget across docs so file #2/#3 are not drowned by a long first Word.
            var perDocBudget = docCount <= 0
                ? DocumentTextExtractor.MaxExtractedChars
                : Math.Max(6000, DocumentTextExtractor.MaxExtractedCharsTotal / docCount);

            var textBuilder = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(text))
                textBuilder.AppendLine(text.TrimEnd());

            textBuilder.AppendLine();
            textBuilder.AppendLine("[Вложения: " + usable.Count + " шт. " +
                                   "Ты ВИДИШЬ/ЧИТАЕШЬ все. Если файлов несколько — ответь по КАЖДОМУ отдельным пунктом " +
                                   "(1), (2), (3)… Не останавливайся на первом.]");

            var imageParts = new JArray();
            var pdfFileParts = new JArray();
            var failedDocs = new List<string>();
            var index = 0;

            foreach (var a in usable)
            {
                index++;
                var label = a.FileName ?? "файл";
                textBuilder.AppendLine();
                textBuilder.Append("=== ФАЙЛ ").Append(index).Append('/').Append(usable.Count)
                    .Append(": ").Append(a.KindLabel).Append(" · ").Append(label).AppendLine(" ===");

                if (a.IsImage)
                {
                    textBuilder.AppendLine("(изображение прикреплено ниже в запросе)");
                    imageParts.Add(new JObject
                    {
                        ["type"] = "image_url",
                        ["image_url"] = new JObject
                        {
                            ["url"] = a.ToDataUrl(),
                            ["detail"] = "low"
                        }
                    });
                    continue;
                }

                if (DocumentTextExtractor.TryExtract(a, perDocBudget, out var extracted, out var extractError))
                {
                    textBuilder.AppendLine("--- начало текста файла " + index + " ---");
                    textBuilder.AppendLine(extracted);
                    textBuilder.AppendLine("--- конец текста файла " + index + " (" + label + ") ---");
                    continue;
                }

                if (a.IsPdf)
                {
                    textBuilder.AppendLine("(PDF прикреплён файлом — разбери его содержимое)");
                    pdfFileParts.Add(new JObject
                    {
                        ["type"] = "file",
                        ["file"] = new JObject
                        {
                            ["filename"] = label.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)
                                ? label
                                : label + ".pdf",
                            ["file_data"] = a.ToDataUrl()
                        }
                    });
                    continue;
                }

                failedDocs.Add(label + (string.IsNullOrEmpty(extractError) ? "" : " (" + extractError + ")"));
                textBuilder.AppendLine(string.IsNullOrEmpty(extractError)
                    ? "не удалось прочитать этот файл"
                    : "не удалось прочитать: " + extractError);
            }

            if (failedDocs.Count > 0)
            {
                textBuilder.AppendLine();
                textBuilder.AppendLine("Не удалось разобрать: " + string.Join("; ", failedDocs));
            }

            if (usable.Count > 1)
            {
                textBuilder.AppendLine();
                textBuilder.AppendLine("Напоминание: в ответе кратко пройдись по всем " + usable.Count +
                                       " файлам по порядку, не только по первому.");
            }

            var parts = new JArray
            {
                new JObject
                {
                    ["type"] = "text",
                    ["text"] = textBuilder.ToString().Trim()
                }
            };

            foreach (var img in imageParts)
                parts.Add(img);
            foreach (var pdf in pdfFileParts)
                parts.Add(pdf);

            if (parts.Count == 1)
                return parts[0]["text"]?.ToString() ?? text ?? "";

            return parts;
        }

        /// <summary>
        /// Drop base64 payloads from user turns so history stays within token/char budgets.
        /// When <paramref name="keepLastUserIntact"/> is true, the latest user message is kept
        /// (needed while the current API round still needs the images/PDF).
        /// </summary>
        private bool CompactMultimodalUnlocked(bool keepLastUserIntact)
        {
            var lastUser = -1;
            if (keepLastUserIntact)
            {
                for (var i = _history.Count - 1; i >= 0; i--)
                {
                    if (IsRole(_history[i], "user"))
                    {
                        lastUser = i;
                        break;
                    }
                }
            }

            for (var i = 0; i < _history.Count; i++)
            {
                if (i == lastUser) continue;
                if (!IsRole(_history[i], "user")) continue;
                if (!(_history[i]["content"] is JArray parts)) continue;

                var labels = new List<string>();
                string textPart = null;
                foreach (var part in parts)
                {
                    var type = part?["type"]?.ToString();
                    if (string.Equals(type, "text", StringComparison.OrdinalIgnoreCase))
                        textPart = part["text"]?.ToString() ?? "";
                    else if (string.Equals(type, "image_url", StringComparison.OrdinalIgnoreCase))
                        labels.Add("изображение");
                    else if (string.Equals(type, "file", StringComparison.OrdinalIgnoreCase))
                        labels.Add(part["file"]?["filename"]?.ToString() ?? "файл");
                }

                if (labels.Count == 0)
                    continue;

                var stub = textPart ?? "";
                if (!string.IsNullOrWhiteSpace(stub))
                    stub += "\n\n";
                stub += "[Вложения предыдущего сообщения, данные убраны из памяти: " +
                        string.Join(", ", labels) + "]";
                _history[i]["content"] = stub;
                return true;
            }

            return false;
        }

        public async Task<AgentTurnResult> RunAsync(
            string userMessage,
            string viewContext,
            CancellationToken cancellationToken)
        {
            return await RunAsync(userMessage, viewContext, null, cancellationToken).ConfigureAwait(false);
        }

        public async Task<AgentTurnResult> RunAsync(
            string userMessage,
            string viewContext,
            IList<ChatAttachment> attachments,
            CancellationToken cancellationToken,
            string turnId = null)
        {
            return await RunAsync(userMessage, viewContext, attachments, cancellationToken, turnId, null)
                .ConfigureAwait(false);
        }

        public async Task<AgentTurnResult> RunAsync(
            string userMessage,
            string viewContext,
            IList<ChatAttachment> attachments,
            CancellationToken cancellationToken,
            string turnId,
            IReadOnlyList<string> toolProfiles)
        {
            var settings = PluginSettingsStore.LoadSettings();
            if (string.IsNullOrWhiteSpace(settings.AssistantApiKey))
            {
                return new AgentTurnResult
                {
                    Failed = true,
                    Reply = "Нужен API-ключ организации. Откройте Настройки → Ассистент и вставьте ключ (это делает IT)."
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
                settings.AssistantModel,
                settings.AssistantTemperature,
                settings.AssistantMaxTokens);

            var activeProfiles = ToolCatalog.NormalizeProfiles(toolProfiles);
            if (activeProfiles.Count == 0)
            {
                activeProfiles = await IntentRouter.ResolveAsync(userMessage, client, cancellationToken)
                    .ConfigureAwait(false);
                activeProfiles = ToolCatalog.NormalizeProfiles(activeProfiles);
            }

            var tools = ToolCatalog.GetOpenAiTools(activeProfiles);
            var done = new List<string>();
            var systemPrompt = BuildSystemPrompt(activeProfiles, userMessage);

            lock (_historyLock)
            {
                EnsureSystemPromptUnlocked(systemPrompt);

                var text = userMessage ?? "";
                if (!string.IsNullOrWhiteSpace(viewContext))
                    text = "[Контекст вида]\n" + viewContext + "\n\n[Запрос]\n" + text;

                // Strip base64 from ALL prior user turns before a new (possibly heavy) message.
                while (CompactMultimodalUnlocked(keepLastUserIntact: false)) { /* keep going */ }

                _history.Add(new JObject
                {
                    ["role"] = "user",
                    ["content"] = BuildUserContent(text, attachments)
                });

                if (TrimHistoryUnlocked())
                {
                    try { HistoryTrimmed?.Invoke(); }
                    catch { /* UI may be disposed */ }
                }
            }

            var maxRounds = RoundBudget.Resolve(activeProfiles, userMessage);
            var toolResultCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var loopGuard = new ToolCallLoopGuard();
            AgentPlan activePlan = null;
            var budgetWarningSent = false;
            var turnLog = new TurnLogEntry
            {
                TurnId = turnId ?? Guid.NewGuid().ToString("N").Substring(0, 12),
                SessionId = _sessionId,
                Ts = DateTime.UtcNow,
                Model = settings.AssistantModel ?? "gpt-4o-mini",
                UserText = userMessage,
                ToolProfiles = activeProfiles.ToList(),
            };
            ParseViewContext(viewContext, turnLog);
            if (attachments != null)
            {
                foreach (var a in attachments)
                {
                    if (a == null) continue;
                    turnLog.Attachments.Add(new AttachmentMeta
                    {
                        Kind = a.IsImage ? "image" : "file",
                        Name = a.FileName,
                        Bytes = a.Data?.Length ?? 0,
                    });
                }
            }
            var turnSw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                for (var round = 0; round < maxRounds; round++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    RaiseStatus("Думает…");

                    // Refresh tools each round so escalation expands the catalog.
                    tools = ToolCatalog.GetOpenAiTools(activeProfiles);

                    // REV-120: warn the model when the round budget is almost gone.
                    if (!budgetWarningSent && round >= Math.Max(0, maxRounds - 2))
                    {
                        budgetWarningSent = true;
                        var left = maxRounds - round;
                        AppendBudgetWarning(left);
                    }

                    JArray messages;
                    lock (_historyLock)
                    {
                        if (TrimHistoryUnlocked())
                        {
                            try { HistoryTrimmed?.Invoke(); }
                            catch { /* ignore */ }
                        }
                        messages = new JArray(_history.ToArray());
                    }

                    JObject completion;
                    try
                    {
                        completion = await client.ChatCompletionsAsync(messages, tools, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return CancelledResult(done, activePlan);
                    }
                    catch (Exception ex)
                    {
                        if (IsCancel(ex, cancellationToken))
                            return CancelledResult(done, activePlan);

                        return new AgentTurnResult
                        {
                            Failed = true,
                            Reply = ex.Message,
                            DoneSummary = done
                        };
                    }

                    // Capture token usage from API response (REV-121: usage available to caller/logs).
                    if (OpenAiCompatibleClient.TryReadUsage(completion, out var promptTok, out var completionTok))
                    {
                        turnLog.PromptTokens += promptTok;
                        turnLog.CompletionTokens += completionTok;
                    }

                    var choice = completion["choices"]?[0]?["message"] as JObject;
                    if (choice == null)
                    {
                        turnLog.Rounds = round + 1;
                        turnLog.Outcome = "failed";
                        turnLog.TotalMs = turnSw.ElapsedMilliseconds;
                        turnLog.Reply = "Пустой ответ от ИИ.";
                        turnLog.ToolProfiles = activeProfiles.ToList();
                        AssistantTurnLogger.Write(turnLog);
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
                        var replyText = choice["content"]?.ToString()?.Trim();
                        if (string.IsNullOrWhiteSpace(replyText))
                            replyText = done.Count > 0 ? "Готово." : "Готово. Дополнительных действий не требуется.";

                        turnLog.Rounds = round + 1;
                        turnLog.Outcome = "ok";
                        turnLog.Reply = replyText;
                        turnLog.DoneSummary = done;
                        turnLog.TotalMs = turnSw.ElapsedMilliseconds;
                        turnLog.ToolProfiles = activeProfiles.ToList();
                        AssistantTurnLogger.Write(turnLog);

                        RaiseStatus("Готов");
                        return new AgentTurnResult
                        {
                            Reply = replyText,
                            DoneSummary = done
                        };
                    }

                    foreach (JToken callTok in toolCalls)
                    {
                        var call = callTok as JObject;
                        if (call == null) continue;

                        var callId = call["id"]?.ToString() ?? Guid.NewGuid().ToString("N");
                        var fn = call["function"] as JObject;
                        var rawName = fn?["name"]?.ToString() ?? "";
                        var name = ToolCatalog.ResolveToolAlias(rawName);
                        var argsJson = fn?["arguments"]?.ToString() ?? "{}";

                        if (cancellationToken.IsCancellationRequested)
                        {
                            AppendToolResult(callId, CancelledToolPayload());
                            continue;
                        }

                        // REV-112: tool outside active profiles → escalate, do not execute yet.
                        if (!ToolCatalog.IsToolAllowed(name, activeProfiles))
                        {
                            var missing = ToolCatalog.GetMissingProfiles(name, activeProfiles);
                            if (missing.Count > 0)
                            {
                                activeProfiles = ToolCatalog.MergeProfiles(activeProfiles, missing);
                                foreach (var p in missing)
                                    turnLog.ProfileEscalations.Add(p);
                                AppendToolResult(callId, BuildProfileEscalationPayload(name, missing, activeProfiles));
                                done.Add("профиль +" + string.Join("+", missing) + " для " + name);
                                continue;
                            }

                            // Profile already active but tool truncated by cap — reorder profiles.
                            var reordered = ToolCatalog.PrioritizeProfilesForTool(name, activeProfiles);
                            if (!ReferenceEquals(reordered, activeProfiles)
                                && ToolCatalog.IsToolAllowed(name, reordered))
                            {
                                activeProfiles = reordered;
                                turnLog.ProfileEscalations.Add("reorder:" + name);
                                AppendToolResult(callId, BuildProfileEscalationPayload(
                                    name,
                                    ToolCatalog.ResolveProfilesForTool(name),
                                    activeProfiles));
                                done.Add("профиль↑ для " + name);
                                continue;
                            }

                            // Unknown tool — soft error (REV-116: clearer message for server-only names).
                            AppendToolResult(callId, new JObject
                            {
                                ["error"] = "unknown_tool",
                                ["tool"] = rawName,
                                ["message"] = ToolCatalog.DescribeUnavailableTool(rawName),
                            }.ToString());
                            done.Add("ошибка: неизвестный tool " + rawName);
                            continue;
                        }

                        if (settings.AssistantRequireConfirmations && ToolCatalog.RequiresConfirmation(name, argsJson))
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
                                AppendToolResult(callId, new JObject
                                {
                                    ["cancelled"] = true,
                                    ["message"] = "Архитектор отменил действие."
                                }.ToString());
                                done.Add("отменено: " + name);
                                continue;
                            }
                        }

                        RaiseStatus("Выполняет…");
                        string rawResult;
                        var toolName = name;
                        var enrichedArgs = NormCheckDefaults.EnrichArgs(toolName, argsJson, userMessage);
                        enrichedArgs = CreateElementArgsNormalizer.Normalize(toolName, enrichedArgs);
                        var toolSw = System.Diagnostics.Stopwatch.StartNew();

                        // REV-121: never silently inject typeId — teach the model with candidates.
                        var typeIdCheck = MissingTypeIdGuard.Check(toolResultCache, toolName, enrichedArgs);
                        if (typeIdCheck.Missing)
                        {
                            var failJson = typeIdCheck.Payload.ToString(Newtonsoft.Json.Formatting.None);
                            AppendToolResult(callId, failJson);
                            done.Add("ошибка: " + typeIdCheck.Error);
                            toolSw.Stop();
                            turnLog.ToolCalls.Add(new ToolCallLog
                            {
                                Round = round,
                                Name = toolName,
                                Args = argsJson,
                                NormalizedArgs = enrichedArgs,
                                Ok = false,
                                DurationMs = toolSw.ElapsedMilliseconds,
                                Error = typeIdCheck.Error,
                                ResultBytes = failJson.Length,
                                MissingTypeId = true,
                            });
                            continue;
                        }

                        // REV-120: declare_plan is a local meta-tool — no Revit call.
                        if (toolName.Equals("declare_plan", StringComparison.OrdinalIgnoreCase))
                        {
                            if (!AgentPlan.TryParse(enrichedArgs, out activePlan, out var planErr))
                            {
                                var fail = ToolResultShaper.FailurePayload(new ToolCatalog.FailureHint(
                                    planErr ?? "Некорректный план.",
                                    "Передай goal и steps[{n,what,tool}]."));
                                AppendToolResult(callId, fail.ToString());
                                done.Add("ошибка: план");
                                toolSw.Stop();
                                turnLog.ToolCalls.Add(new ToolCallLog
                                {
                                    Round = round,
                                    Name = toolName,
                                    Args = argsJson,
                                    NormalizedArgs = enrichedArgs,
                                    Ok = false,
                                    DurationMs = toolSw.ElapsedMilliseconds,
                                    Error = planErr,
                                    ResultBytes = fail.ToString().Length,
                                });
                                continue;
                            }

                            var planPayload = activePlan.ToSuccessPayload();
                            AppendToolResult(callId, planPayload.ToString());
                            RaisePlanChanged(activePlan);
                            done.Add(planPayload["summary"]?.ToString() ?? "план");
                            toolSw.Stop();
                            turnLog.ToolCalls.Add(new ToolCallLog
                            {
                                Round = round,
                                Name = toolName,
                                Args = argsJson,
                                NormalizedArgs = enrichedArgs,
                                Ok = true,
                                DurationMs = toolSw.ElapsedMilliseconds,
                                ResultBytes = planPayload.ToString().Length,
                            });
                            continue;
                        }

                        // REV-120: block 3rd identical tool+args (loop guard).
                        if (!loopGuard.TryAllow(toolName, enrichedArgs, out _))
                        {
                            var blocked = ToolCallLoopGuard.BlockPayload(toolName);
                            AppendToolResult(callId, blocked.ToString());
                            done.Add("ошибка: повтор " + FriendlyToolName(toolName));
                            toolSw.Stop();
                            turnLog.ToolCalls.Add(new ToolCallLog
                            {
                                Round = round,
                                Name = toolName,
                                Args = argsJson,
                                NormalizedArgs = enrichedArgs,
                                Ok = false,
                                DurationMs = toolSw.ElapsedMilliseconds,
                                Error = "loop_blocked",
                                ResultBytes = blocked.ToString().Length,
                            });
                            continue;
                        }

                        try
                        {
                            if (HasNormalizeError(enrichedArgs, out var normalizeMsg))
                            {
                                rawResult = new JObject
                                {
                                    ["jsonrpc"] = "2.0",
                                    ["error"] = new JObject { ["message"] = normalizeMsg }
                                }.ToString();
                            }
                            else if (TryGetCachedToolResult(toolResultCache, toolName, enrichedArgs, out var cached))
                            {
                                rawResult = cached;
                            }
                            else
                            {
                                rawResult = await Task.Run(
                                    () => _toolExecutor.Execute(toolName, enrichedArgs),
                                    cancellationToken).ConfigureAwait(false);
                                RememberCachedToolResult(toolResultCache, toolName, enrichedArgs, rawResult);
                            }
                            if (toolName.Equals("check_fire_doors", StringComparison.OrdinalIgnoreCase))
                                rawResult = FireDoorRulesApplier.EnrichRawResult(rawResult);
                        }
                        catch (OperationCanceledException)
                        {
                            AppendToolResult(callId, CancelledToolPayload());
                            // Fill remaining sibling calls so history stays valid for the next turn.
                            FillRemainingToolResults(toolCalls, callTok);
                            return CancelledResult(done, activePlan);
                        }
                        catch (Exception ex)
                        {
                            if (IsCancel(ex, cancellationToken))
                            {
                                AppendToolResult(callId, CancelledToolPayload());
                                FillRemainingToolResults(toolCalls, callTok);
                                return CancelledResult(done, activePlan);
                            }

                            rawResult = new JObject
                            {
                                ["jsonrpc"] = "2.0",
                                ["error"] = new JObject
                                {
                                    ["message"] = ex.Message
                                }
                            }.ToString();
                        }

                        rawResult = NormCheckDefaults.AttachSourceToResult(name, enrichedArgs, rawResult);
                        var bridged = NormCheckHighlightBridge.AfterCheckTool(toolName, rawResult);
                        rawResult = bridged.RawResult;
                        var highlightExtra = bridged.DoneExtra;

                        var (ok, summary, forModel) = ParseToolResponse(name, rawResult);
                        AppendToolResult(callId, forModel);
                        toolSw.Stop();

                        if (activePlan != null && activePlan.TryMarkTool(toolName, ok))
                            RaisePlanChanged(activePlan);

                        turnLog.ToolCalls.Add(new ToolCallLog
                        {
                            Round = round,
                            Name = toolName,
                            Args = argsJson,
                            NormalizedArgs = enrichedArgs,
                            Ok = ok,
                            DurationMs = toolSw.ElapsedMilliseconds,
                            Error = ok ? null : summary,
                            ResultBytes = rawResult?.Length ?? 0,
                        });

                        if (ok)
                        {
                            done.Add(string.IsNullOrWhiteSpace(highlightExtra)
                                ? summary
                                : summary + " · " + highlightExtra);
                        }
                        else
                            done.Add("ошибка: " + summary);
                    }

                    if (cancellationToken.IsCancellationRequested)
                        return CancelledResult(done, activePlan);
                }

                var maxRoundsReply = AgentPlan.BuildPartialReply(
                    "Слишком много шагов в одном запросе. Уточните задачу или разбейте на части.",
                    done,
                    activePlan);

                turnLog.Rounds = maxRounds;
                turnLog.Outcome = "maxRounds";
                turnLog.DoneSummary = done;
                turnLog.TotalMs = turnSw.ElapsedMilliseconds;
                turnLog.Reply = maxRoundsReply;
                turnLog.ToolProfiles = activeProfiles.ToList();
                AssistantTurnLogger.Write(turnLog);

                RaiseStatus("Готов");
                return new AgentTurnResult
                {
                    Failed = true,
                    Reply = maxRoundsReply,
                    DoneSummary = done
                };
            }
            catch (OperationCanceledException)
            {
                turnLog.Outcome = "cancelled";
                turnLog.DoneSummary = done;
                turnLog.TotalMs = turnSw.ElapsedMilliseconds;
                turnLog.ToolProfiles = activeProfiles.ToList();
                AssistantTurnLogger.Write(turnLog);
                return CancelledResult(done, activePlan);
            }
            finally
            {
                FinishTurnCleanup();
            }
        }

        private static AgentTurnResult CancelledResult(IList<string> done, AgentPlan plan = null)
        {
            return new AgentTurnResult
            {
                Cancelled = true,
                Reply = AgentPlan.BuildPartialReply("Остановлено.", done, plan),
                DoneSummary = done ?? new List<string>()
            };
        }

        private static bool IsCancel(Exception ex, CancellationToken token)
        {
            // HttpClient timeout also throws TaskCanceledException — only treat as cancel
            // when the user/token actually requested cancellation.
            if (!token.IsCancellationRequested)
                return false;
            return ex is OperationCanceledException
                   || ex?.InnerException is OperationCanceledException
                   || ex is TaskCanceledException;
        }

        private static string CancelledToolPayload()
        {
            return new JObject
            {
                ["cancelled"] = true,
                ["message"] = "Остановлено пользователем."
            }.ToString();
        }

        private static string BuildProfileEscalationPayload(
            string toolName,
            IReadOnlyList<string> missingProfiles,
            IReadOnlyList<string> activeProfiles)
        {
            return new JObject
            {
                ["error"] = "tool_not_in_profile",
                ["tool"] = toolName,
                ["availableInProfiles"] = new JArray(missingProfiles.ToArray()),
                ["activeProfiles"] = new JArray(activeProfiles.ToArray()),
                ["hint"] =
                    "Tool was outside the active profile set. Profiles " +
                    string.Join(", ", missingProfiles) +
                    " are now enabled — call the same tool again.",
            }.ToString();
        }

        /// <summary>
        /// After cancel mid-batch, stub any tool_calls that still lack a result.
        /// </summary>
        private void FillRemainingToolResults(JArray toolCalls, JToken afterCall)
        {
            var seen = false;
            foreach (JToken callTok in toolCalls)
            {
                if (!seen)
                {
                    if (ReferenceEquals(callTok, afterCall))
                        seen = true;
                    continue;
                }

                var call = callTok as JObject;
                if (call == null) continue;
                var callId = call["id"]?.ToString() ?? Guid.NewGuid().ToString("N");
                AppendToolResult(callId, CancelledToolPayload());
            }
        }

        /// <summary>
        /// After a turn finishes, drop base64 from the just-answered user message so the next
        /// photo/PDF does not look like the chat is already "full". Also repair incomplete tool pairs.
        /// </summary>
        private void FinishTurnCleanup()
        {
            lock (_historyLock)
            {
                while (CompactMultimodalUnlocked(keepLastUserIntact: false)) { /* strip all attachments now that the turn is done */ }
                SanitizeToolPairsUnlocked();
            }
        }

        private void AppendToolResult(string callId, string content)
        {
            lock (_historyLock)
            {
                _history.Add(new JObject
                {
                    ["role"] = "tool",
                    ["tool_call_id"] = callId,
                    ["content"] = TruncateForHistory(content ?? "")
                });
            }
        }

        private void RaiseStatus(string status)
        {
            try { StatusChanged?.Invoke(status); }
            catch { /* UI may be disposed */ }
        }

        private void RaisePlanChanged(AgentPlan plan)
        {
            if (plan == null) return;
            try { PlanChanged?.Invoke(plan.Snapshot()); }
            catch { /* UI may be disposed */ }
        }

        /// <summary>
        /// Inject a system nudge so the model wraps up instead of hard-stopping (REV-120).
        /// </summary>
        private void AppendBudgetWarning(int roundsLeft)
        {
            var left = Math.Max(1, roundsLeft);
            var text = left <= 1
                ? "Остался 1 шаг бюджета. Завершай: краткий отчёт что сделано и что не успели."
                : $"Осталось {left} шага бюджета. Завершай работу и отчитайся: что сделано, что нет.";
            lock (_historyLock)
            {
                _history.Add(new JObject
                {
                    ["role"] = "system",
                    ["content"] = text
                });
            }
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

        private static string FriendlyToolName(string name) => ToolCatalog.FriendlyName(name);

        /// <summary>
        /// Collapse consecutive identical done lines: «размеры помещений» ×5 → «размеры помещений ×5».
        /// </summary>
        public static IList<string> CollapseDoneSummary(IList<string> items)
        {
            var result = new List<string>();
            if (items == null || items.Count == 0)
                return result;

            string current = null;
            var count = 0;
            foreach (var raw in items)
            {
                var item = raw ?? "";
                if (current != null && string.Equals(item, current, StringComparison.Ordinal))
                {
                    count++;
                    continue;
                }

                if (current != null)
                    result.Add(count > 1 ? $"{current} ×{count}" : current);
                current = item;
                count = 1;
            }

            if (current != null)
                result.Add(count > 1 ? $"{current} ×{count}" : current);

            return result;
        }

        private static (bool ok, string summary, string forModel) ParseToolResponse(string toolName, string raw)
        {
            try
            {
                var jo = JObject.Parse(raw);
                if (jo["error"] != null)
                {
                    var msg = jo["error"]?["message"]?.ToString() ?? "ошибка";
                    var hint = ToolCatalog.DescribeFailure(toolName, msg);
                    var fail = ToolResultShaper.FailurePayload(hint);
                    return (false, hint.Combined, ToolResultShaper.EnsureUnderBudget(fail, MaxToolResultChars));
                }

                var result = ExtractResultPayload(jo);
                if (result is JObject resultObj)
                {
                    var successToken = resultObj["Success"] ?? resultObj["success"] ?? resultObj["ok"];
                    if (successToken != null && successToken.Type == JTokenType.Boolean && !successToken.Value<bool>())
                    {
                        var msg = resultObj["Message"]?.ToString()
                            ?? resultObj["message"]?.ToString()
                            ?? "неуспех";
                        var hint = ToolCatalog.DescribeFailure(toolName, msg);
                        var fail = ToolResultShaper.FailurePayload(hint);
                        return (false, hint.Combined, ToolResultShaper.EnsureUnderBudget(fail, MaxToolResultChars));
                    }
                }

                if (result == null)
                {
                    var hint = ToolCatalog.DescribeFailure(toolName, "пустой ответ");
                    var fail = ToolResultShaper.FailurePayload(hint);
                    return (false, hint.Combined, ToolResultShaper.EnsureUnderBudget(fail, MaxToolResultChars));
                }

                var shaped = ToolResultShaper.Shape(toolName, result);
                var summary = shaped["summary"]?.ToString();
                if (string.IsNullOrWhiteSpace(summary))
                    summary = CompactResult(toolName, result);
                var forModel = ToolResultShaper.EnsureUnderBudget(shaped, MaxToolResultChars);
                return (true, summary, forModel);
            }
            catch
            {
                var hint = ToolCatalog.DescribeFailure(toolName, raw);
                var fail = ToolResultShaper.FailurePayload(hint);
                return (false, hint.Combined, ToolResultShaper.EnsureUnderBudget(fail, MaxToolResultChars));
            }
        }

        /// <summary>
        /// In-Revit tools (run_norm_audit, annotate_*) return bare JSON without jsonrpc result wrapper.
        /// </summary>
        private static JToken ExtractResultPayload(JObject jo)
        {
            if (jo == null)
                return null;
            if (jo["result"] != null)
                return jo["result"];
            if (jo["Success"] != null || jo["success"] != null || jo["findings"] != null || jo["summary"] != null)
                return jo;
            return null;
        }

        private static string CompactResult(string toolName, JToken result)
        {
            return ToolResultShaper.CompactSummary(toolName, result);
        }

        private static bool TryGetCachedToolResult(
            Dictionary<string, string> cache,
            string toolName,
            string argsJson,
            out string rawResult)
        {
            rawResult = null;
            if (!IsCacheableReadTool(toolName))
                return false;
            var key = CacheKey(toolName, argsJson);
            return cache.TryGetValue(key, out rawResult);
        }

        private static void RememberCachedToolResult(
            Dictionary<string, string> cache,
            string toolName,
            string argsJson,
            string rawResult)
        {
            if (!IsCacheableReadTool(toolName) || string.IsNullOrEmpty(rawResult))
                return;
            cache[CacheKey(toolName, argsJson)] = rawResult;
        }

        private static bool IsCacheableReadTool(string toolName)
        {
            if (string.IsNullOrWhiteSpace(toolName))
                return false;
            switch (toolName.Trim().ToLowerInvariant())
            {
                case "get_current_view_info":
                case "get_available_family_types":
                case "get_document_styles":
                case "query_norm_rules":
                    return true;
                default:
                    return false;
            }
        }

        private static string CacheKey(string toolName, string argsJson)
        {
            return toolName.Trim().ToLowerInvariant() + "|" + (argsJson ?? "{}").Trim();
        }

        private static bool HasNormalizeError(string argsJson, out string message)
        {
            message = null;
            try
            {
                var jo = JObject.Parse(argsJson ?? "{}");
                message = jo["_normalizeError"]?.ToString();
                return !string.IsNullOrWhiteSpace(message);
            }
            catch
            {
                return false;
            }
        }

    }
}
