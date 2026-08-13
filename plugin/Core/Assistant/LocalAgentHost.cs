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
        /// <summary>Final model id used for the turn (REV-124).</summary>
        public string Model { get; set; }
        public int PromptTokens { get; set; }
        public int CompletionTokens { get; set; }
        /// <summary>True when the turn upgraded from fast to smart mid-flight.</summary>
        public bool EscalatedToSmart { get; set; }
    }

    /// <summary>
    /// In-process agent: LLM + existing Revit JSON-RPC commands (same as MCP socket).
    /// </summary>
    public sealed class LocalAgentHost : IAssistantHost
    {
        /// <summary>Core-only prompt; production uses <see cref="BuildSystemPrompt"/> per turn.</summary>
        public const string SystemPrompt = AssistantSystemPrompt.Core;

        public static string BuildSystemPrompt(IReadOnlyList<string> profiles, string userText = null) =>
            AssistantSystemPrompt.Build(profiles, userText);

        private readonly ConversationHistory _history = new ConversationHistory();
        private readonly SessionCreationJournal _journal = new SessionCreationJournal();
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
        /// Default previous user turns (+ the current one is never trimmed). Overridable via settings.
        /// </summary>
        public const int MaxPreviousUserTurns = ConversationHistory.DefaultMaxPreviousUserTurns;

        /// <summary>Rough character budget for all non-system content sent to the model.</summary>
        public const int MaxHistoryChars = ConversationHistory.DefaultMaxHistoryChars;

        /// <summary>Cap each tool result stored in history (full payloads blow the budget in one round).</summary>
        public const int MaxToolResultChars = 4000;

        /// <summary>Obsolete name kept for any external references; prefer MaxPreviousUserTurns.</summary>
        public const int MaxRecentMessages = 80;

        public event Action<string> StatusChanged;
        public event Func<PendingToolConfirmation, Task<bool>> ConfirmationRequested;
        /// <summary>REV-125: pause for ask_user option card.</summary>
        public event Func<PendingAskUser, Task<AskUserAnswer>> AskUserRequested;
        /// <summary>Raised when older messages were dropped to keep context within limits.</summary>
        public event Action HistoryTrimmed;
        /// <summary>REV-124: raised when the turn upgrades fast → smart (UI shows notice).</summary>
        public event Action<string> ModelEscalated;
        /// <summary>REV-126: history fill changed (turns / chars) — update context meter.</summary>
        public event Action<HistoryBudget> HistoryBudgetChanged;
        /// <summary>REV-120: plan checklist declared or a step status changed.</summary>
        public event Action<AgentPlanSnapshot> PlanChanged;
        /// <summary>REV-127: live tool journal row (running / ok / error / cancelled).</summary>
        public event Action<ToolStepEvent> ToolStepChanged;
        /// <summary>REV-127: cumulative assistant text while streaming the final reply.</summary>
        public event Action<string> ReplyDelta;

        public int HistoryMessageCount => _history.Count;

        public HistoryBudget GetHistoryBudget() => _history.GetBudget();

        public SessionCreationJournal CreationJournal => _journal;

        public void ClearHistory()
        {
            lock (_historyLock)
            {
                _history.Clear();
                _journal.Clear();
            }
            _sessionId = Guid.NewGuid().ToString("N").Substring(0, 12);
            RaiseBudgetChanged();
        }

        private static void ParseViewContext(string ctx, TurnLogEntry log)
        {
            if (string.IsNullOrWhiteSpace(ctx)) return;
            try
            {
                // Format: "Документ: X · Вид: Y (ViewType) · Уровень: Z" (+ optional Выделено / Единицы)
                foreach (var part in ctx.Split(new[] { " · ", "\n" }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var colon = part.IndexOf(':');
                    if (colon < 0) continue;
                    var key = part.Substring(0, colon).Trim();
                    var val = part.Substring(colon + 1).Trim();
                    if (key.StartsWith("Документ") || key.Contains("Документ"))
                        log.DocTitle = val;
                    else if (key.StartsWith("Вид") || key.Contains("Вид"))
                        log.ViewName = val;
                    else if (key.StartsWith("Уровень") || key.Contains("Уровень"))
                        log.LevelName = val;
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
                var dropped = _history.TrimIfNeeded();
                if (dropped)
                    RaiseBudgetChangedUnlocked();
                return dropped;
            }
        }

        private void ApplyHistoryLimitsFromSettings(ServiceSettings settings)
        {
            var turns = settings?.AssistantMaxPreviousUserTurns ?? MaxPreviousUserTurns;
            if (turns < 4) turns = 4;
            if (turns > 20) turns = 20;
            _history.MaxPreviousUserTurns = turns;
            _history.MaxHistoryChars = MaxHistoryChars;
        }

        private void RaiseBudgetChanged()
        {
            HistoryBudget budget;
            lock (_historyLock)
                budget = _history.GetBudget();
            try { HistoryBudgetChanged?.Invoke(budget); }
            catch { /* UI may be disposed */ }
        }

        private void RaiseBudgetChangedUnlocked()
        {
            var budget = _history.GetBudget();
            try { HistoryBudgetChanged?.Invoke(budget); }
            catch { /* UI may be disposed */ }
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

        /// <summary>Build user preamble with [КОНТЕКСТ] / [ЖУРНАЛ] / [Запрос] (REV-126).</summary>
        internal string BuildUserPreamble(string userMessage, string viewContext)
        {
            var sb = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(viewContext))
            {
                var ctx = viewContext.Trim();
                if (!ctx.StartsWith("[КОНТЕКСТ]", StringComparison.OrdinalIgnoreCase)
                    && !ctx.StartsWith("[Контекст", StringComparison.OrdinalIgnoreCase))
                    sb.AppendLine("[КОНТЕКСТ] " + ctx);
                else
                    sb.AppendLine(ctx);
                sb.AppendLine();
            }

            var journal = _journal.FormatForPrompt();
            if (!string.IsNullOrWhiteSpace(journal))
            {
                sb.AppendLine(journal);
                sb.AppendLine();
            }

            sb.AppendLine("[Запрос]");
            sb.Append(userMessage ?? "");
            return sb.ToString().TrimEnd();
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

            ApplyHistoryLimitsFromSettings(settings);

            // IntentRouter always uses the cheap/fast model (REV-124).
            var fastModel = ModelRouter.NormalizeFast(settings.AssistantModel);
            var smartModel = ModelRouter.NormalizeSmart(settings.AssistantModelSmart);
            var routerClient = CreateChatClient(settings, fastModel);

            var activeProfiles = ToolCatalog.NormalizeProfiles(toolProfiles);
            if (activeProfiles.Count == 0)
            {
                activeProfiles = await IntentRouter.ResolveAsync(userMessage, routerClient, cancellationToken)
                    .ConfigureAwait(false);
                activeProfiles = ToolCatalog.NormalizeProfiles(activeProfiles);
            }

            var modelId = ModelRouter.Resolve(fastModel, smartModel, activeProfiles, out var usingSmart);
            var client = usingSmart && modelId == smartModel
                ? CreateChatClient(settings, modelId)
                : (modelId == fastModel ? routerClient : CreateChatClient(settings, modelId));

            var tools = ToolCatalog.GetOpenAiTools(activeProfiles);
            var done = new List<string>();
            var systemPrompt = BuildSystemPrompt(activeProfiles, userMessage);

            lock (_historyLock)
            {
                _history.EnsureSystemPrompt(systemPrompt);

                var text = BuildUserPreamble(userMessage, viewContext);

                // Strip base64 from ALL prior user turns before a new (possibly heavy) message.
                _history.CompactAllMultimodal(keepLastUserIntact: false);

                _history.Add(new JObject
                {
                    ["role"] = "user",
                    ["content"] = BuildUserContent(text, attachments)
                });

                if (_history.TrimIfNeeded())
                {
                    try { HistoryTrimmed?.Invoke(); }
                    catch { /* UI may be disposed */ }
                }

                RaiseBudgetChangedUnlocked();
            }

            var maxRounds = RoundBudget.Resolve(activeProfiles, userMessage);
            var toolResultCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var loopGuard = new ToolCallLoopGuard();
            AgentPlan activePlan = null;
            var budgetWarningSent = false;
            var askUserUsed = false;
            var consecutiveToolFailures = 0;
            var stepJournal = new List<ToolStepEvent>();
            var turnLog = new TurnLogEntry
            {
                TurnId = turnId ?? Guid.NewGuid().ToString("N").Substring(0, 12),
                SessionId = _sessionId,
                Ts = DateTime.UtcNow,
                Model = modelId,
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
                        if (_history.TrimIfNeeded())
                        {
                            try { HistoryTrimmed?.Invoke(); }
                            catch { /* ignore */ }
                            RaiseBudgetChangedUnlocked();
                        }
                        messages = _history.CloneForApi();
                    }

                    JObject completion;
                    try
                    {
                        // REV-127: SSE stream; ReplyDelta only for text-only rounds (no tool_calls).
                        completion = await client.ChatCompletionsStreamingAsync(
                                messages,
                                tools,
                                text => RaiseReplyDelta(text),
                                cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        CancelRunningToolStep(stepJournal);
                        return CancelledResult(done, activePlan, turnLog, stepJournal);
                    }
                    catch (Exception ex)
                    {
                        if (IsCancel(ex, cancellationToken))
                        {
                            CancelRunningToolStep(stepJournal);
                            return CancelledResult(done, activePlan, turnLog, stepJournal);
                        }

                        turnLog.Rounds = round + 1;
                        turnLog.Outcome = "failed";
                        turnLog.TotalMs = turnSw.ElapsedMilliseconds;
                        turnLog.Reply = ex.Message;
                        turnLog.ToolProfiles = activeProfiles.ToList();
                        AssistantTurnLogger.Write(turnLog);
                        return FinishTurn(turnLog, ex.Message, done, failed: true);
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
                        return FinishTurn(turnLog, "Пустой ответ от ИИ. Попробуйте ещё раз.", done, failed: true);
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
                        return FinishTurn(turnLog, replyText, done);
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
                                // REV-124: adding a smart profile while still on fast → upgrade model.
                                if (ModelRouter.RequiresSmart(missing)
                                    && TryEscalateModel(settings, smartModel, "profile:" + string.Join("+", missing),
                                        ref client, ref usingSmart, turnLog))
                                {
                                    done.Add("модель→smart");
                                }
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
                                if (ModelRouter.RequiresSmart(activeProfiles)
                                    && TryEscalateModel(settings, smartModel, "profile_reorder:" + name,
                                        ref client, ref usingSmart, turnLog))
                                {
                                    done.Add("модель→smart");
                                }
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
                            consecutiveToolFailures++;
                            if (consecutiveToolFailures >= 3
                                && TryEscalateModel(settings, smartModel, "errors:3",
                                    ref client, ref usingSmart, turnLog))
                            {
                                consecutiveToolFailures = 0;
                                done.Add("модель→smart");
                            }
                            continue;
                        }

                        if (ToolCatalog.ShouldConfirm(
                                name,
                                argsJson,
                                settings.AssistantRequireConfirmations,
                                settings.AssistantConfirmDeleteThreshold > 0
                                    ? settings.AssistantConfirmDeleteThreshold
                                    : DeleteConfirmSummary.DefaultThreshold))
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
                        var activeStep = BeginToolStep(stepJournal, round, toolName, enrichedArgs);

                        // REV-121: never silently inject typeId — teach the model with candidates.
                        var typeIdCheck = MissingTypeIdGuard.Check(toolResultCache, toolName, enrichedArgs);
                        if (typeIdCheck.Missing)
                        {
                            var failJson = typeIdCheck.Payload.ToString(Newtonsoft.Json.Formatting.None);
                            AppendToolResult(callId, failJson);
                            done.Add("ошибка: " + typeIdCheck.Error);
                            toolSw.Stop();
                            FinishToolStep(stepJournal, activeStep, false, typeIdCheck.Error, failJson, toolSw.ElapsedMilliseconds);
                            turnLog.ToolCalls.Add(new ToolCallLog
                            {
                                Round = round,
                                Name = toolName,
                                Args = argsJson,
                                NormalizedArgs = enrichedArgs,
                                Ok = false,
                                DurationMs = toolSw.ElapsedMilliseconds,
                                Error = typeIdCheck.Error,
                                Summary = typeIdCheck.Error,
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
                                FinishToolStep(stepJournal, activeStep, false, planErr, fail.ToString(), toolSw.ElapsedMilliseconds);
                                turnLog.ToolCalls.Add(new ToolCallLog
                                {
                                    Round = round,
                                    Name = toolName,
                                    Args = argsJson,
                                    NormalizedArgs = enrichedArgs,
                                    Ok = false,
                                    DurationMs = toolSw.ElapsedMilliseconds,
                                    Error = planErr,
                                    Summary = planErr,
                                    ResultBytes = fail.ToString().Length,
                                });
                                continue;
                            }

                            var planPayload = activePlan.ToSuccessPayload();
                            AppendToolResult(callId, planPayload.ToString());
                            RaisePlanChanged(activePlan);
                            var planSummary = planPayload["summary"]?.ToString() ?? "план";
                            done.Add(planSummary);
                            toolSw.Stop();
                            FinishToolStep(stepJournal, activeStep, true, planSummary, planPayload.ToString(), toolSw.ElapsedMilliseconds);
                            turnLog.ToolCalls.Add(new ToolCallLog
                            {
                                Round = round,
                                Name = toolName,
                                Args = argsJson,
                                NormalizedArgs = enrichedArgs,
                                Ok = true,
                                DurationMs = toolSw.ElapsedMilliseconds,
                                Summary = planSummary,
                                ResultBytes = planPayload.ToString().Length,
                            });
                            continue;
                        }

                        // REV-125: ask_user — pause for UI card (max one per turn).
                        if (toolName.Equals("ask_user", StringComparison.OrdinalIgnoreCase))
                        {
                            if (askUserUsed)
                            {
                                var dup = ToolResultShaper.FailurePayload(new ToolCatalog.FailureHint(
                                    "ask_user уже вызывали в этом запросе.",
                                    "Используй полученный ответ; не спрашивай повторно."));
                                AppendToolResult(callId, dup.ToString());
                                done.Add("ошибка: повторный вопрос");
                                toolSw.Stop();
                                FinishToolStep(stepJournal, activeStep, false, "ask_user уже вызывали", dup.ToString(), toolSw.ElapsedMilliseconds);
                                turnLog.ToolCalls.Add(new ToolCallLog
                                {
                                    Round = round,
                                    Name = toolName,
                                    Args = argsJson,
                                    NormalizedArgs = enrichedArgs,
                                    Ok = false,
                                    DurationMs = toolSw.ElapsedMilliseconds,
                                    Error = "ask_user_already_used",
                                    Summary = "ask_user уже вызывали",
                                    ResultBytes = dup.ToString().Length,
                                });
                                continue;
                            }

                            if (!AskUserParser.TryParse(enrichedArgs, out var pendingAsk, out var askErr))
                            {
                                var failAsk = ToolResultShaper.FailurePayload(new ToolCatalog.FailureHint(
                                    askErr ?? "Некорректный ask_user.",
                                    "Передай question и options (2–6 строк)."));
                                AppendToolResult(callId, failAsk.ToString());
                                done.Add("ошибка: вопрос");
                                toolSw.Stop();
                                FinishToolStep(stepJournal, activeStep, false, askErr, failAsk.ToString(), toolSw.ElapsedMilliseconds);
                                turnLog.ToolCalls.Add(new ToolCallLog
                                {
                                    Round = round,
                                    Name = toolName,
                                    Args = argsJson,
                                    NormalizedArgs = enrichedArgs,
                                    Ok = false,
                                    DurationMs = toolSw.ElapsedMilliseconds,
                                    Error = askErr,
                                    Summary = askErr,
                                    ResultBytes = failAsk.ToString().Length,
                                });
                                continue;
                            }

                            askUserUsed = true;
                            RaiseStatus("Ждёт ответ");
                            var askHandler = AskUserRequested;
                            AskUserAnswer answer = null;
                            if (askHandler != null)
                                answer = await askHandler(pendingAsk).ConfigureAwait(false);

                            if (answer == null || answer.Cancelled || string.IsNullOrWhiteSpace(answer.DisplayText))
                            {
                                var cancelled = AskUserParser.ToCancelledPayload();
                                AppendToolResult(callId, cancelled.ToString());
                                done.Add("отменено: вопрос");
                                toolSw.Stop();
                                FinishToolStep(stepJournal, activeStep, false, "отменено", cancelled.ToString(), toolSw.ElapsedMilliseconds);
                                turnLog.ToolCalls.Add(new ToolCallLog
                                {
                                    Round = round,
                                    Name = toolName,
                                    Args = argsJson,
                                    NormalizedArgs = enrichedArgs,
                                    Ok = false,
                                    DurationMs = toolSw.ElapsedMilliseconds,
                                    Error = "ask_user_cancelled",
                                    Summary = "отменено",
                                    ResultBytes = cancelled.ToString().Length,
                                });
                                continue;
                            }

                            var askPayload = AskUserParser.ToSuccessPayload(answer);
                            var typologyHint = TypologyPrograms.BuildHintForAnswer(answer.DisplayText);
                            if (!string.IsNullOrWhiteSpace(typologyHint))
                            {
                                askPayload["typologyGuidance"] = typologyHint;
                                askPayload["summary"] = "ответ: " + answer.DisplayText + " · программа типологии приложена";
                            }

                            AppendToolResult(callId, askPayload.ToString());
                            if (!string.IsNullOrWhiteSpace(typologyHint))
                            {
                                AppendSystemMessage(
                                    "Архитектор выбрал: " + answer.DisplayText + ".\n" +
                                    typologyHint + "\n" +
                                    "Собери declare_plan и геометрию по этой программе. Не своди к двум комнатам.");
                            }
                            var askSummary = askPayload["summary"]?.ToString() ?? "вопрос";
                            done.Add(askSummary);
                            toolSw.Stop();
                            FinishToolStep(stepJournal, activeStep, true, askSummary, askPayload.ToString(), toolSw.ElapsedMilliseconds);
                            turnLog.ToolCalls.Add(new ToolCallLog
                            {
                                Round = round,
                                Name = toolName,
                                Args = argsJson,
                                NormalizedArgs = enrichedArgs,
                                Ok = true,
                                DurationMs = toolSw.ElapsedMilliseconds,
                                Summary = askSummary,
                                ResultBytes = askPayload.ToString().Length,
                            });
                            continue;
                        }

                        // REV-120: block 3rd identical tool+args (loop guard).
                        // REV-124: on stall while still on fast — escalate to smart once.
                        if (!loopGuard.TryAllow(toolName, enrichedArgs, out _))
                        {
                            if (TryEscalateModel(settings, smartModel, "loop:" + toolName,
                                ref client, ref usingSmart, turnLog))
                            {
                                var upgraded = new JObject
                                {
                                    ["ok"] = false,
                                    ["error"] = "loop_escalated",
                                    ["hint"] = ModelRouter.EscalationNotice +
                                               " Измени подход или аргументы — повтор с теми же аргументами запрещён.",
                                    ["tool"] = toolName ?? "",
                                };
                                AppendToolResult(callId, upgraded.ToString());
                                done.Add("модель→smart (цикл)");
                                toolSw.Stop();
                                FinishToolStep(stepJournal, activeStep, false, "цикл → smart", upgraded.ToString(), toolSw.ElapsedMilliseconds);
                                turnLog.ToolCalls.Add(new ToolCallLog
                                {
                                    Round = round,
                                    Name = toolName,
                                    Args = argsJson,
                                    NormalizedArgs = enrichedArgs,
                                    Ok = false,
                                    DurationMs = toolSw.ElapsedMilliseconds,
                                    Error = "loop_escalated",
                                    Summary = "цикл → smart",
                                    ResultBytes = upgraded.ToString().Length,
                                });
                                consecutiveToolFailures = 0;
                                continue;
                            }

                            var blocked = ToolCallLoopGuard.BlockPayload(toolName);
                            AppendToolResult(callId, blocked.ToString());
                            done.Add("ошибка: повтор " + FriendlyToolName(toolName));
                            toolSw.Stop();
                            FinishToolStep(stepJournal, activeStep, false, "повтор заблокирован", blocked.ToString(), toolSw.ElapsedMilliseconds);
                            turnLog.ToolCalls.Add(new ToolCallLog
                            {
                                Round = round,
                                Name = toolName,
                                Args = argsJson,
                                NormalizedArgs = enrichedArgs,
                                Ok = false,
                                DurationMs = toolSw.ElapsedMilliseconds,
                                Error = "loop_blocked",
                                Summary = "повтор заблокирован",
                                ResultBytes = blocked.ToString().Length,
                            });
                            consecutiveToolFailures++;
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
                            CancelRunningToolStep(stepJournal);
                            return CancelledResult(done, activePlan, turnLog, stepJournal);
                        }
                        catch (Exception ex)
                        {
                            if (IsCancel(ex, cancellationToken))
                            {
                                AppendToolResult(callId, CancelledToolPayload());
                                FillRemainingToolResults(toolCalls, callTok);
                                CancelRunningToolStep(stepJournal);
                                return CancelledResult(done, activePlan, turnLog, stepJournal);
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
                        if (ok)
                            _journal.TryRecord(toolName, rawResult);
                        toolSw.Stop();

                        if (activePlan != null && activePlan.TryMarkTool(toolName, ok))
                            RaisePlanChanged(activePlan);

                        FinishToolStep(stepJournal, activeStep, ok, summary, rawResult, toolSw.ElapsedMilliseconds);
                        turnLog.ToolCalls.Add(new ToolCallLog
                        {
                            Round = round,
                            Name = toolName,
                            Args = argsJson,
                            NormalizedArgs = enrichedArgs,
                            Ok = ok,
                            DurationMs = toolSw.ElapsedMilliseconds,
                            Error = ok ? null : summary,
                            Summary = summary,
                            ResultBytes = rawResult?.Length ?? 0,
                        });

                        if (ok)
                        {
                            consecutiveToolFailures = 0;
                            done.Add(string.IsNullOrWhiteSpace(highlightExtra)
                                ? summary
                                : summary + " · " + highlightExtra);
                        }
                        else
                        {
                            done.Add("ошибка: " + summary);
                            consecutiveToolFailures++;
                            if (consecutiveToolFailures >= 3
                                && TryEscalateModel(settings, smartModel, "errors:3",
                                    ref client, ref usingSmart, turnLog))
                            {
                                consecutiveToolFailures = 0;
                                done.Add("модель→smart");
                            }
                        }
                    }

                    if (cancellationToken.IsCancellationRequested)
                    {
                        CancelRunningToolStep(stepJournal);
                        return CancelledResult(done, activePlan, turnLog, stepJournal);
                    }
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
                return FinishTurn(turnLog, maxRoundsReply, done, failed: true);
            }
            catch (OperationCanceledException)
            {
                CancelRunningToolStep(stepJournal);
                turnLog.Outcome = "cancelled";
                turnLog.DoneSummary = done;
                turnLog.TotalMs = turnSw.ElapsedMilliseconds;
                turnLog.ToolProfiles = activeProfiles.ToList();
                AssistantTurnLogger.Write(turnLog);
                return CancelledResult(done, activePlan, turnLog, stepJournal);
            }
            finally
            {
                FinishTurnCleanup();
            }
        }

        private static OpenAiCompatibleClient CreateChatClient(ServiceSettings settings, string model)
        {
            return new OpenAiCompatibleClient(
                settings.AssistantApiKey,
                settings.AssistantApiBaseUrl,
                model,
                settings.AssistantTemperature,
                settings.AssistantMaxTokens);
        }

        /// <summary>REV-124: one-shot fast→smart upgrade; injects notice into history + UI event.</summary>
        private bool TryEscalateModel(
            ServiceSettings settings,
            string smartModel,
            string reason,
            ref OpenAiCompatibleClient client,
            ref bool usingSmart,
            TurnLogEntry turnLog)
        {
            if (!ModelRouter.CanEscalate(smartModel, usingSmart))
                return false;

            usingSmart = true;
            client = CreateChatClient(settings, smartModel);
            if (turnLog != null)
            {
                turnLog.Model = smartModel;
                turnLog.ModelEscalations.Add(reason ?? "escalate");
            }

            AppendSystemMessage(ModelRouter.EscalationNotice);
            RaiseStatus("Сильная модель…");
            try { ModelEscalated?.Invoke(ModelRouter.EscalationNotice); }
            catch { /* UI may be disposed */ }
            return true;
        }

        private static AgentTurnResult FinishTurn(
            TurnLogEntry turnLog,
            string reply,
            IList<string> done,
            bool failed = false,
            bool cancelled = false)
        {
            return new AgentTurnResult
            {
                Reply = reply,
                Failed = failed,
                Cancelled = cancelled,
                DoneSummary = done ?? new List<string>(),
                Model = turnLog != null ? turnLog.Model : null,
                PromptTokens = turnLog != null ? turnLog.PromptTokens : 0,
                CompletionTokens = turnLog != null ? turnLog.CompletionTokens : 0,
                EscalatedToSmart = turnLog != null
                    && turnLog.ModelEscalations != null
                    && turnLog.ModelEscalations.Count > 0
            };
        }

        private static AgentTurnResult CancelledResult(
            IList<string> done,
            AgentPlan plan = null,
            TurnLogEntry turnLog = null,
            List<ToolStepEvent> stepJournal = null)
        {
            return FinishTurn(
                turnLog,
                AgentPlan.BuildPartialReply("Остановлено.", done, plan),
                done ?? new List<string>(),
                cancelled: true);
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
                _history.CompactAllMultimodal(keepLastUserIntact: false);
                _history.SanitizeToolPairs();
                RaiseBudgetChangedUnlocked();
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

        private void RaiseReplyDelta(string cumulativeText)
        {
            try { ReplyDelta?.Invoke(cumulativeText ?? ""); }
            catch { /* UI may be disposed */ }
        }

        private void RaisePlanChanged(AgentPlan plan)
        {
            if (plan == null) return;
            try { PlanChanged?.Invoke(plan.Snapshot()); }
            catch { /* UI may be disposed */ }
        }

        private void RaiseToolStep(List<ToolStepEvent> journal, ToolStepEvent step)
        {
            if (journal == null || step == null) return;
            step.AllSteps = ToolStepEvent.Snapshot(journal);
            try { ToolStepChanged?.Invoke(step); }
            catch { /* UI may be disposed */ }
        }

        private ToolStepEvent BeginToolStep(
            List<ToolStepEvent> journal,
            int round,
            string name,
            string argsJson)
        {
            if (journal == null) return null;
            var step = new ToolStepEvent
            {
                Index = journal.Count + 1,
                Round = round,
                Name = name ?? "",
                Status = ToolStepEvent.StatusRunning,
                Summary = "…",
                ArgsJson = TruncateForJournal(argsJson, 2000),
            };
            journal.Add(step);
            RaiseToolStep(journal, step);
            return step;
        }

        private void FinishToolStep(
            List<ToolStepEvent> journal,
            ToolStepEvent step,
            bool ok,
            string summary,
            string resultJson,
            long durationMs)
        {
            if (step == null) return;
            step.Status = ok ? ToolStepEvent.StatusOk : ToolStepEvent.StatusError;
            step.Summary = string.IsNullOrWhiteSpace(summary) ? (ok ? "ok" : "ошибка") : summary;
            step.ResultJson = TruncateForJournal(resultJson, 2000);
            step.DurationMs = durationMs;
            RaiseToolStep(journal, step);
        }

        private void CancelRunningToolStep(List<ToolStepEvent> journal)
        {
            if (journal == null || journal.Count == 0) return;
            var last = journal[journal.Count - 1];
            if (last == null || !string.Equals(last.Status, ToolStepEvent.StatusRunning, StringComparison.Ordinal))
                return;
            last.Status = ToolStepEvent.StatusCancelled;
            last.Summary = "остановлено";
            RaiseToolStep(journal, last);
        }

        private static string TruncateForJournal(string s, int max)
        {
            if (string.IsNullOrEmpty(s) || s.Length <= max) return s;
            return s.Substring(0, max) + "…";
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
            AppendSystemMessage(text);
        }

        private void AppendSystemMessage(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return;
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
                var n = toolName ?? "";
                if (n.Equals("delete_element", StringComparison.OrdinalIgnoreCase)
                    || (n.Equals("operate_element", StringComparison.OrdinalIgnoreCase)
                        && ToolCatalog.RequiresConfirmation(n, argsJson)))
                {
                    return DeleteConfirmSummary.Format(n, argsJson);
                }

                if (n.StartsWith("create_", StringComparison.OrdinalIgnoreCase))
                    return $"Выполнить создание: {FriendlyToolName(toolName)}?";
                if (n.Equals("operate_element", StringComparison.OrdinalIgnoreCase))
                {
                    var args = JObject.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson);
                    var action = args["data"]?["action"]?.ToString() ?? args["action"]?.ToString() ?? "изменить";
                    return $"Выполнить действие «{action}» над элементами?";
                }
                if (n.Equals("send_code_to_revit", StringComparison.OrdinalIgnoreCase))
                    return "Выполнить произвольный C# код в модели?";
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
