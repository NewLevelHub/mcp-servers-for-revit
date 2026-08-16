using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using revit_mcp_plugin.Configuration;

namespace revit_mcp_plugin.Core.Assistant
{
    /// <summary>
    /// In-Revit chat backed by local assistant-bridge → Cursor SDK agent → Revit MCP (REV-155).
    /// </summary>
    public sealed class CursorBridgeAgentHost : IAssistantHost
    {
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };

        private ServiceSettings _settings;
        private readonly string _sessionId = Guid.NewGuid().ToString("N").Substring(0, 12);
        private int _turnCount;

        /// <summary>A raw tool_step "running" not yet resolved to ok/error — tracked
        /// independently of the UI's folded <see cref="ToolStepEvent"/> rows so the turn
        /// log (REV feedback fix) sees every real call, not the folded display copy.</summary>
        private struct PendingToolCall
        {
            public long StartMs;
            public string Name;
            public string Args;
        }

        /// <summary>Set by ClearHistory; makes the next turn drop the Cursor session.</summary>
        private bool _resetPending;

        public CursorBridgeAgentHost(ServiceSettings settings)
        {
            _settings = settings ?? new ServiceSettings();
        }

        public event Action<string> StatusChanged;
        public event Func<PendingToolConfirmation, Task<bool>> ConfirmationRequested;
        public event Func<PendingAskUser, Task<AskUserAnswer>> AskUserRequested;
        public event Action HistoryTrimmed;
        public event Action<HistoryBudget> HistoryBudgetChanged;
        public event Action<AgentPlanSnapshot> PlanChanged;
        public event Action<string> ModelEscalated;
        public event Action<ToolStepEvent> ToolStepChanged;
        public event Action<string> ReplyDelta;

        public void ClearHistory()
        {
            _turnCount = 0;
            // The agent's memory lives in the bridge; drop it on the next send.
            _resetPending = true;
            RaiseBudgetChanged();
        }

        public HistoryBudget GetHistoryBudget()
        {
            return new HistoryBudget
            {
                UserTurns = _turnCount,
                MaxPreviousUserTurns = _settings.AssistantMaxPreviousUserTurns,
                EstimatedChars = 0,
                MaxHistoryChars = LocalAgentHost.MaxHistoryChars,
            };
        }

        public Task<AgentTurnResult> RunAsync(
            string userMessage,
            string viewContext,
            CancellationToken cancellationToken)
        {
            return RunAsync(userMessage, viewContext, null, cancellationToken);
        }

        public async Task<AgentTurnResult> RunAsync(
            string userMessage,
            string viewContext,
            IList<ChatAttachment> attachments,
            CancellationToken cancellationToken,
            string turnId = null,
            IReadOnlyList<string> toolProfiles = null)
        {
            var steps = new List<ToolStepEvent>();
            var stepsByCallId = new Dictionary<string, ToolStepEvent>(StringComparer.Ordinal);
            var pendingCalls = new Dictionary<string, PendingToolCall>(StringComparer.Ordinal);
            var model = DescribeConfiguredModel();

            // REV feedback fix: the turn log is the only way a 👎 in the panel ever
            // reaches assistant-feedback_*.md with real content (question/view/model/
            // tool chain) instead of an empty stub — see CursorBridgeAgentHost never
            // having written one before this.
            var turnLog = new TurnLogEntry
            {
                TurnId = string.IsNullOrWhiteSpace(turnId) ? Guid.NewGuid().ToString("N").Substring(0, 12) : turnId,
                SessionId = _sessionId,
                Ts = DateTime.UtcNow,
                UserText = userMessage,
                Model = model,
                ToolProfiles = toolProfiles != null ? toolProfiles.ToList() : new List<string>(),
            };
            LocalAgentHost.ParseViewContext(viewContext, turnLog);
            var turnSw = Stopwatch.StartNew();

            // Stop must reach the agent itself, not just abort our HTTP read.
            using (cancellationToken.Register(() => FireAndForgetCancel()))
            {
                try
                {
                    // Pick up a key or model edited in settings without restarting Revit.
                    ReloadSettings();

                    // First turn pays for the Node bridge start; say so instead of freezing on "Готов".
                    RaiseStatus("Запускаю движок…");
                    AssistantBridgeLauncher.EnsureRunning(_settings);
                    RaiseStatus("Cursor…");

                    var preamble = BuildPrompt(userMessage, viewContext, toolProfiles);
                    var images = BuildImages(attachments);
                    var reset = _resetPending;

                    var body = new JObject
                    {
                        ["sessionId"] = _sessionId,
                        ["message"] = preamble,
                        ["reset"] = reset,
                    };

                    if (images.Count > 0)
                        body["images"] = images;

                    using (var request = NewRequest(HttpMethod.Post, AssistantBridgeLauncher.ChatUrl(_settings)))
                    {
                        request.Content = new StringContent(body.ToString(Formatting.None), Encoding.UTF8, "application/json");

                        using (var response = await Http.SendAsync(
                                   request,
                                   HttpCompletionOption.ResponseHeadersRead,
                                   cancellationToken).ConfigureAwait(false))
                        {
                            response.EnsureSuccessStatusCode();

                            // Only clear the flag once the bridge accepted the reset.
                            if (reset)
                                _resetPending = false;

                            using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                            using (var reader = new StreamReader(stream, Encoding.UTF8))
                            {
                                var reply = await ReadSseAsync(
                                    reader, steps, stepsByCallId, pendingCalls, turnLog, turnSw,
                                    m => model = m, cancellationToken)
                                    .ConfigureAwait(false);

                                _turnCount++;
                                RaiseBudgetChanged();

                                turnLog.Outcome = "ok";
                                turnLog.Reply = reply;

                                return new AgentTurnResult
                                {
                                    Reply = reply,
                                    Model = model,
                                };
                            }
                        }
                    }
                }
                // Any exception, not just OperationCanceledException: tearing down the
                // SSE read mid-flight surfaces as IOException, which used to fall through
                // to the generic branch below and show the architect a raw .NET string
                // instead of "Остановлено.".
                catch (Exception) when (cancellationToken.IsCancellationRequested)
                {
                    MarkRunningStepsCancelled(steps, pendingCalls, turnLog, turnSw);
                    turnLog.Outcome = "cancelled";
                    turnLog.Reply = "Остановлено.";
                    return new AgentTurnResult { Cancelled = true, Reply = "Остановлено.", Model = model };
                }
                catch (Exception ex)
                {
                    MarkRunningStepsCancelled(steps, pendingCalls, turnLog, turnSw);
                    turnLog.Outcome = "failed";
                    var shown = DescribeFailure(ex, turnLog.ToolCalls.Count);
                    turnLog.Reply = shown;
                    // The technical chain stays in the log, where it is useful, instead of
                    // in the chat, where it is not.
                    turnLog.FailureDetail = FailureDetail(ex);
                    return new AgentTurnResult
                    {
                        Failed = true,
                        Reply = shown,
                        Model = model,
                    };
                }
                finally
                {
                    // Always exactly one entry per turn, whichever of the three paths above ran.
                    turnLog.TotalMs = turnSw.ElapsedMilliseconds;
                    turnLog.Model = model;
                    turnLog.Rounds = turnLog.ToolCalls.Count;
                    AssistantTurnLogger.Write(turnLog);
                }
            }
        }

        /// <summary>
        /// A sentence the architect can act on, instead of the exception text.
        /// "The read operation failed, see inner exception." is literally an
        /// instruction to look somewhere they cannot see, and it arrives in the chat
        /// styled exactly like the assistant's answer.
        /// </summary>
        /// <param name="toolCallCount">
        /// Whether anything already ran matters: a turn that died before the first
        /// call changed nothing, but one that died after three calls may have left
        /// the model half-edited, and saying "ничего не произошло" would be a lie.
        /// </param>
        private static string DescribeFailure(Exception ex, int toolCallCount)
        {
            if (IsStaleEngineFailure(ex))
            {
                var head = "Движок ассистента потерял связь с Cursor. Откройте Настройки → Ассистент "
                         + "→ «Перезапустить движок» и повторите запрос — Revit закрывать не нужно.";
                return toolCallCount == 0
                    ? head + " В проекте ничего не изменилось."
                    : head + $" До обрыва успело выполниться действий: {toolCallCount} — посмотрите чертёж.";
            }

            if (IsTransportFailure(ex))
            {
                var tail = " Если повторяется — Настройки → Ассистент → «Перезапустить движок».";
                return toolCallCount == 0
                    ? "Связь с моделью оборвалась — ход не начался, в проекте ничего не изменилось. "
                      + "Проверьте интернет и повторите запрос." + tail
                    : $"Связь с моделью оборвалась после {toolCallCount} выполненных действий — "
                      + "часть работы могла остаться в проекте. Проверьте интернет, посмотрите чертёж "
                      + "и повторите запрос." + tail;
            }

            return "Ассистент не смог выполнить запрос: " + InnermostMessage(ex);
        }

        /// <summary>
        /// Cursor answers "Authentication error. If you are logged in, try logging out and
        /// back in." on a bridge process whose session went stale, and it keeps answering it
        /// on every turn even though the key itself is still valid (verified against
        /// api.cursor.com while the panel was failing). EnsureRunning reuses a live process
        /// whose settings did not change, so without this the architect is told to log in
        /// somewhere they never logged in, and only closing Revit clears it.
        /// Matched on the message: it arrives as a plain bridge error, not an HTTP status.
        /// </summary>
        private static bool IsStaleEngineFailure(Exception ex)
        {
            for (var e = ex; e != null; e = e.InnerException)
            {
                var message = e.Message;
                if (string.IsNullOrEmpty(message)) continue;
                if (message.IndexOf("Authentication error", StringComparison.OrdinalIgnoreCase) >= 0
                    || message.IndexOf("logging out and back in", StringComparison.OrdinalIgnoreCase) >= 0
                    || message.IndexOf("Unauthorized", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }
            return false;
        }

        private static bool IsTransportFailure(Exception ex)
        {
            for (var e = ex; e != null; e = e.InnerException)
            {
                if (e is IOException
                    || e is HttpRequestException
                    || e is TaskCanceledException
                    || e is System.Net.Sockets.SocketException)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>Innermost message — the outer one is usually "see inner exception".</summary>
        private static string InnermostMessage(Exception ex)
        {
            var e = ex;
            while (e.InnerException != null)
                e = e.InnerException;
            return e.Message;
        }

        /// <summary>Full exception chain for the turn log.</summary>
        private static string FailureDetail(Exception ex)
        {
            var parts = new List<string>();
            for (var e = ex; e != null; e = e.InnerException)
                parts.Add(e.GetType().Name + ": " + e.Message);
            return string.Join(" <- ", parts);
        }

        private void ReloadSettings()
        {
            try
            {
                var fresh = PluginSettingsStore.LoadSettings();
                if (fresh != null)
                    _settings = fresh;
            }
            catch { /* keep the settings we already have */ }
        }

        private HttpRequestMessage NewRequest(HttpMethod method, string url)
        {
            var request = new HttpRequestMessage(method, url);
            var token = AssistantBridgeLauncher.Token;
            if (!string.IsNullOrEmpty(token))
                request.Headers.TryAddWithoutValidation("x-bridge-token", token);
            return request;
        }

        /// <summary>Tells the bridge to cancel the run; failures are not worth surfacing.</summary>
        private void FireAndForgetCancel()
        {
            Task.Run(async () =>
            {
                try
                {
                    using (var request = NewRequest(HttpMethod.Post, AssistantBridgeLauncher.CancelUrl(_settings)))
                    {
                        var payload = new JObject { ["sessionId"] = _sessionId };
                        request.Content = new StringContent(payload.ToString(Formatting.None), Encoding.UTF8, "application/json");
                        using (await Http.SendAsync(request).ConfigureAwait(false)) { }
                    }
                }
                catch { /* bridge already gone or run finished */ }
            });
        }

        private async Task<string> ReadSseAsync(
            StreamReader reader,
            IList<ToolStepEvent> steps,
            IDictionary<string, ToolStepEvent> stepsByCallId,
            IDictionary<string, PendingToolCall> pendingCalls,
            TurnLogEntry turnLog,
            Stopwatch turnSw,
            Action<string> setModel,
            CancellationToken cancellationToken)
        {
            string reply = "";
            string eventName = null;

            while (!reader.EndOfStream)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var line = await reader.ReadLineAsync().ConfigureAwait(false);
                if (line == null)
                    break;

                if (line.StartsWith("event:", StringComparison.OrdinalIgnoreCase))
                {
                    eventName = line.Substring(6).Trim();
                    continue;
                }

                if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                    continue;

                var json = line.Substring(5).Trim();
                if (string.IsNullOrWhiteSpace(json))
                    continue;

                var data = JObject.Parse(json);
                switch (eventName)
                {
                    case "status":
                        RaiseStatus(data["text"]?.ToString());
                        break;

                    case "text_delta":
                        reply = data["text"]?.ToString() ?? reply;
                        RaiseReplyDelta(reply);
                        break;

                    case "tool_step":
                        ApplyToolStep(data, steps, stepsByCallId, pendingCalls, turnLog, turnSw);
                        break;

                    case "confirm":
                        await HandleConfirmAsync(data).ConfigureAwait(false);
                        break;

                    case "done":
                        reply = data["reply"]?.ToString() ?? reply;
                        var doneModel = data["model"]?.ToString();
                        if (!string.IsNullOrWhiteSpace(doneModel))
                            setModel(doneModel);
                        // doneSummary is deliberately ignored: the steps journal above the
                        // answer already lists what ran, in Russian. Repeating raw tool ids
                        // in a "Сделано:" line is noise for an architect.
                        return reply;

                    case "error":
                        throw new InvalidOperationException(data["message"]?.ToString() ?? "Ошибка Cursor bridge");
                }
            }

            return string.IsNullOrWhiteSpace(reply) ? "Пустой ответ от Cursor bridge." : reply;
        }

        /// <summary>
        /// Maintains the turn journal. The UI only renders steps that carry a full
        /// <see cref="ToolStepEvent.AllSteps"/> snapshot, so every raise carries one.
        /// </summary>
        private void ApplyToolStep(
            JObject data,
            IList<ToolStepEvent> steps,
            IDictionary<string, ToolStepEvent> stepsByCallId,
            IDictionary<string, PendingToolCall> pendingCalls,
            TurnLogEntry turnLog,
            Stopwatch turnSw)
        {
            var name = data["name"]?.ToString() ?? "tool";
            var status = data["status"]?.ToString() ?? ToolStepEvent.StatusRunning;
            var callId = data["callId"]?.ToString();
            if (string.IsNullOrEmpty(callId))
                callId = name + "#" + steps.Count;

            RecordRawToolCall(data, name, status, callId, pendingCalls, turnLog, turnSw);

            var label = ToolStepLabels.Humanize(name);

            ToolStepEvent step;
            if (!stepsByCallId.TryGetValue(callId, out step))
            {
                // The agent often repeats one tool across layers or passes, and the
                // repeats are rarely back to back — another tool interleaves. Fold a
                // repeat into the row that tool already owns, wherever it sits, as
                // "×N" instead of 16 near-identical lines (or two rows for one tool).
                var owner = FindFoldTarget(steps, label);
                if (owner != null)
                {
                    step = owner;
                    step.Round += 1;
                }
                else
                {
                    step = new ToolStepEvent
                    {
                        Index = steps.Count + 1,
                        Round = 1,
                        BaseLabel = label,
                    };
                    steps.Add(step);
                }

                stepsByCallId[callId] = step;
            }

            if (status == ToolStepEvent.StatusRunning)
            {
                step.Pending += 1;
            }
            else
            {
                if (step.Pending > 0) step.Pending -= 1;
                if (status == ToolStepEvent.StatusError) step.HadError = true;
            }

            // The journal header shows Name; args/result live in the expander.
            step.Name = ToolStepLabels.WithRepeat(label, step.Round);
            // A folded row is done only when the last call in it has answered, and a
            // failure among them outranks the successes that followed.
            step.Status = step.Pending > 0
                ? ToolStepEvent.StatusRunning
                : (step.HadError ? ToolStepEvent.StatusError : status);
            step.ArgsJson = data["args"]?.ToString() ?? step.ArgsJson;
            step.ResultJson = data["result"]?.ToString() ?? step.ResultJson;
            step.Summary = ToolStepLabels.StatusNote(step.Status);

            RaiseToolStep(step, steps);
        }

        /// <summary>
        /// One entry per real tool call for the turn log, independent of the UI fold
        /// above (which merges repeats of the same tool into one journal row). This is
        /// what lets a 👎 report actually name which call in the chain failed.
        /// </summary>
        private static void RecordRawToolCall(
            JObject data,
            string name,
            string status,
            string callId,
            IDictionary<string, PendingToolCall> pendingCalls,
            TurnLogEntry turnLog,
            Stopwatch turnSw)
        {
            if (status == ToolStepEvent.StatusRunning)
            {
                pendingCalls[callId] = new PendingToolCall
                {
                    StartMs = turnSw.ElapsedMilliseconds,
                    Name = name,
                    Args = data["args"]?.ToString(),
                };
                return;
            }

            PendingToolCall pending;
            pendingCalls.TryGetValue(callId, out pending);
            pendingCalls.Remove(callId);
            var resultStr = data["result"]?.ToString();

            turnLog.ToolCalls.Add(new ToolCallLog
            {
                Round = turnLog.ToolCalls.Count + 1,
                Name = string.IsNullOrEmpty(pending.Name) ? name : pending.Name,
                Args = pending.Args ?? data["args"]?.ToString(),
                Ok = status == ToolStepEvent.StatusOk,
                DurationMs = turnSw.ElapsedMilliseconds - pending.StartMs,
                Error = status == ToolStepEvent.StatusError ? (SummarizeResult(resultStr) ?? "ошибка") : null,
                ResultBytes = resultStr?.Length ?? 0,
                Summary = SummarizeResult(resultStr),
            });
        }

        /// <summary>
        /// A readable one-liner from the tool result. The raw value is an MCP envelope
        /// wrapping the payload in nested content/text objects, and writing it straight
        /// into the log made the "Цепочка" line of a complaint report unreadable JSON.
        /// </summary>
        private static string SummarizeResult(string resultJson)
        {
            if (string.IsNullOrWhiteSpace(resultJson))
                return null;

            var text = resultJson;
            try
            {
                // Unwrap {"status":..,"value":{"content":[{"text":{"text":"<payload>"}}]}}
                var token = JToken.Parse(resultJson);
                var inner = token.SelectToken("value.content[0].text.text")
                            ?? token.SelectToken("value.content[0].text")
                            ?? token.SelectToken("content[0].text.text")
                            ?? token.SelectToken("content[0].text");
                if (inner != null)
                    text = inner.Type == JTokenType.String ? inner.ToString() : inner.ToString(Formatting.None);

                // The payload is often itself JSON with a summary/message worth showing.
                if (text.TrimStart().StartsWith("{"))
                {
                    var payload = JToken.Parse(text);
                    var note = payload.SelectToken("summary")
                               ?? payload.SelectToken("message")
                               ?? payload.SelectToken("Message");
                    if (note != null && note.Type == JTokenType.String)
                        text = note.ToString();
                }
            }
            catch
            {
                // The bridge truncates results to 2000 chars, so the envelope usually
                // arrives as invalid JSON and parsing above never gets a chance. Pull
                // the innermost payload out textually instead of showing the wrapper.
                text = UnwrapTruncatedEnvelope(resultJson);
            }

            text = System.Text.RegularExpressions.Regex.Replace(text ?? "", @"\s+", " ").Trim();
            if (text.Length > 160)
                text = text.Substring(0, 160) + "…";
            return text.Length == 0 ? null : text;
        }

        /// <summary>
        /// Last-resort unwrap for a result the bridge cut mid-JSON: take the deepest
        /// "text": "…" value, then unescape it enough to read.
        /// </summary>
        private static string UnwrapTruncatedEnvelope(string raw)
        {
            if (string.IsNullOrEmpty(raw))
                return raw;

            var matches = System.Text.RegularExpressions.Regex.Matches(
                raw, "\"(?:text|summary|message|Message)\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)");
            if (matches.Count == 0)
                return raw;

            // The deepest match is the innermost payload, which is the human-readable part.
            var payload = matches[matches.Count - 1].Groups[1].Value;
            return payload
                .Replace("\\n", " ")
                .Replace("\\r", " ")
                .Replace("\\\"", "\"")
                .Replace("\\\\", "\\");
        }

        /// <summary>
        /// Row a repeat of <paramref name="label"/> should fold into: the row that tool
        /// already owns, or null when it has none yet. Deliberately folds into rows that
        /// are still running — the agent fires a tool as a parallel batch, and refusing
        /// in-flight rows split one tool across several lines. <see cref="ToolStepEvent.Pending"/>
        /// keeps the row marked running until the whole batch answers.
        /// </summary>
        private static ToolStepEvent FindFoldTarget(IList<ToolStepEvent> steps, string label)
        {
            for (var i = steps.Count - 1; i >= 0; i--)
            {
                var candidate = steps[i];
                if (candidate != null && string.Equals(candidate.BaseLabel, label, StringComparison.Ordinal))
                    return candidate;
            }

            return null;
        }

        private async Task HandleConfirmAsync(JObject data)
        {
            var requestId = data["requestId"]?.ToString() ?? "";
            var action = data["action"]?.ToString() ?? "Действие в модели";
            var details = data["details"]?.ToString() ?? "";
            var tool = data["tool"]?.ToString();

            var summary = string.IsNullOrWhiteSpace(details) ? action : action + "\n" + details;

            // Feed the delete confirmation bar real ids so it can show categories.
            var args = new JObject();
            if (data["elementIds"] is JArray ids && ids.Count > 0)
                args["elementIds"] = ids;

            var approved = false;
            var handler = ConfirmationRequested;
            if (handler != null)
            {
                try
                {
                    approved = await handler(new PendingToolConfirmation
                    {
                        // The tool id drives the "Удалить"/"Выполнить" button choice.
                        ToolName = string.IsNullOrWhiteSpace(tool) ? action : tool.Trim(),
                        Summary = summary,
                        ArgumentsJson = args.ToString(Formatting.None),
                    }).ConfigureAwait(false);
                }
                catch
                {
                    approved = false;
                }
            }

            try
            {
                using (var request = NewRequest(HttpMethod.Post, AssistantBridgeLauncher.ConfirmUrl(_settings)))
                {
                    var payload = new JObject
                    {
                        ["sessionId"] = _sessionId,
                        ["requestId"] = requestId,
                        ["approved"] = approved,
                    };
                    request.Content = new StringContent(payload.ToString(Formatting.None), Encoding.UTF8, "application/json");
                    using (await Http.SendAsync(request).ConfigureAwait(false)) { }
                }
            }
            catch { /* the bridge times out on its own and treats it as a refusal */ }
        }

        private void MarkRunningStepsCancelled(
            IList<ToolStepEvent> steps,
            IDictionary<string, PendingToolCall> pendingCalls,
            TurnLogEntry turnLog,
            Stopwatch turnSw)
        {
            // Whatever was still in flight when the turn died is exactly what a "почему
            // завис" investigation needs — record it before the pending set is dropped.
            if (pendingCalls != null && pendingCalls.Count > 0)
            {
                foreach (var kv in pendingCalls)
                {
                    turnLog.ToolCalls.Add(new ToolCallLog
                    {
                        Round = turnLog.ToolCalls.Count + 1,
                        Name = kv.Value.Name,
                        Args = kv.Value.Args,
                        Ok = false,
                        DurationMs = turnSw.ElapsedMilliseconds - kv.Value.StartMs,
                        Error = "прервано",
                    });
                }
                pendingCalls.Clear();
            }

            if (steps == null || steps.Count == 0)
                return;

            var changed = false;
            foreach (var step in steps)
            {
                if (step.Status == ToolStepEvent.StatusRunning)
                {
                    step.Status = ToolStepEvent.StatusCancelled;
                    step.Summary = ToolStepLabels.StatusNote(ToolStepEvent.StatusCancelled);
                    changed = true;
                }
            }

            if (changed)
                RaiseToolStep(steps[steps.Count - 1], steps);
        }

        private string DescribeConfiguredModel()
        {
            var id = string.IsNullOrWhiteSpace(_settings.AssistantCursorModel)
                ? AssistantBridgeLauncher.DefaultModelId
                : _settings.AssistantCursorModel.Trim();
            return CursorModelCatalog.LabelFor(id);
        }

        private static string BuildPrompt(
            string userMessage,
            string viewContext,
            IReadOnlyList<string> toolProfiles)
        {
            var sb = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(viewContext))
            {
                sb.AppendLine("[КОНТЕКСТ] " + viewContext.Trim());
                sb.AppendLine();
            }

            if (toolProfiles != null && toolProfiles.Count > 0)
            {
                sb.AppendLine("[Профиль сценария: " + string.Join(", ", toolProfiles) + "]");
                sb.AppendLine();
            }

            sb.AppendLine("[Запрос]");
            sb.Append(userMessage ?? "");
            return sb.ToString().TrimEnd();
        }

        private static JArray BuildImages(IList<ChatAttachment> attachments)
        {
            var arr = new JArray();
            if (attachments == null)
                return arr;

            foreach (var a in attachments.Where(x => x?.IsImage == true && x.Data != null && x.Data.Length > 0))
            {
                arr.Add(new JObject
                {
                    ["mimeType"] = string.IsNullOrWhiteSpace(a.MimeType) ? "image/png" : a.MimeType,
                    ["dataBase64"] = Convert.ToBase64String(a.Data),
                });
            }

            return arr;
        }

        private void RaiseStatus(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return;
            try { StatusChanged?.Invoke(text); }
            catch { /* UI disposed */ }
        }

        private void RaiseReplyDelta(string text)
        {
            try { ReplyDelta?.Invoke(text ?? ""); }
            catch { /* UI disposed */ }
        }

        private void RaiseToolStep(ToolStepEvent step, IList<ToolStepEvent> steps)
        {
            try
            {
                var payload = step.CloneWithoutAll();
                payload.AllSteps = ToolStepEvent.Snapshot(steps);
                ToolStepChanged?.Invoke(payload);
            }
            catch { /* UI disposed */ }
        }

        private void RaiseBudgetChanged()
        {
            try { HistoryBudgetChanged?.Invoke(GetHistoryBudget()); }
            catch { /* UI disposed */ }
        }
    }
}
