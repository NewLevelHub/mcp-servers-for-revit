using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace revit_mcp_plugin.Core.Assistant
{
    public sealed class OpenAiCompatibleClient : IStreamingChatCompletionsClient
    {
        private static readonly HttpClient Http = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(5)
        };

        private readonly string _apiKey;
        private readonly string _baseUrl;
        private readonly string _model;
        private readonly double _temperature;
        private readonly int? _maxTokens;

        public OpenAiCompatibleClient(string apiKey, string baseUrl, string model)
            : this(apiKey, baseUrl, model, temperature: 0, maxTokens: null)
        {
        }

        public OpenAiCompatibleClient(
            string apiKey,
            string baseUrl,
            string model,
            double temperature,
            int? maxTokens)
        {
            _apiKey = apiKey ?? "";
            _baseUrl = (baseUrl ?? "https://api.openai.com/v1").TrimEnd('/');
            _model = string.IsNullOrWhiteSpace(model) ? "gpt-4o-mini" : model;
            _temperature = ClampTemperature(temperature);
            _maxTokens = maxTokens.HasValue && maxTokens.Value > 0 ? maxTokens : null;
        }

        /// <summary>Model id sent on each chat.completions request (REV-124).</summary>
        public string Model => _model;

        public Task<JObject> ChatCompletionsAsync(
            JArray messages,
            JArray tools,
            CancellationToken cancellationToken)
        {
            return SendChatAsync(messages, tools, stream: false, onContentDelta: null, cancellationToken);
        }

        public Task<JObject> ChatCompletionsStreamingAsync(
            JArray messages,
            JArray tools,
            Action<string> onContentDelta,
            CancellationToken cancellationToken)
        {
            return SendChatAsync(messages, tools, stream: true, onContentDelta, cancellationToken);
        }

        private async Task<JObject> SendChatAsync(
            JArray messages,
            JArray tools,
            bool stream,
            Action<string> onContentDelta,
            CancellationToken cancellationToken)
        {
            var body = BuildBody(messages, tools, stream);

            const int maxAttempts = 4;
            try
            {
                for (var attempt = 1; attempt <= maxAttempts; attempt++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    using (var request = BuildRequest(body))
                    using (var response = await Http.SendAsync(
                               request,
                               stream
                                   ? HttpCompletionOption.ResponseHeadersRead
                                   : HttpCompletionOption.ResponseContentRead,
                               cancellationToken).ConfigureAwait(false))
                    {
                        if (response.IsSuccessStatusCode)
                        {
                            if (stream)
                                return await ReadSseAsync(response, onContentDelta, cancellationToken)
                                    .ConfigureAwait(false);

                            var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                            return JObject.Parse(text);
                        }

                        var errBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        var status = (int)response.StatusCode;
                        if (status == 429 && attempt < maxAttempts)
                        {
                            var delayMs = ResolveRetryDelayMs(response, attempt);
                            await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
                            continue;
                        }

                        throw new InvalidOperationException(HumanizeHttpError(status, errBody));
                    }
                }

                throw new InvalidOperationException(HumanizeHttpError(429, ""));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (TaskCanceledException)
            {
                // HttpClient.Timeout (not user cancel)
                throw new InvalidOperationException(
                    "ИИ не ответил вовремя (таймаут). Упростите запрос или уберите тяжёлые вложения.");
            }
        }

        private JObject BuildBody(JArray messages, JArray tools, bool stream)
        {
            var body = new JObject
            {
                ["model"] = _model,
                ["messages"] = messages,
                // Tool-calling: low temperature; overridable via ServiceSettings.AssistantTemperature (REV-121).
                ["temperature"] = _temperature
            };
            if (stream)
                body["stream"] = true;

            if (tools != null && tools.Count > 0)
            {
                body["tools"] = tools;
                body["tool_choice"] = "auto";
                // One tool call per round so fail-fast on walls/doors works as the prompt promises (REV-121).
                body["parallel_tool_calls"] = false;
            }

            if (_maxTokens.HasValue)
            {
                // OpenAI chat.completions accepts max_tokens; some newer models prefer max_completion_tokens.
                body["max_tokens"] = _maxTokens.Value;
            }

            return body;
        }

        private HttpRequestMessage BuildRequest(JObject body)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, _baseUrl + "/chat/completions")
            {
                Content = new StringContent(body.ToString(Formatting.None), Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
            return request;
        }

        private static async Task<JObject> ReadSseAsync(
            HttpResponseMessage response,
            Action<string> onContentDelta,
            CancellationToken cancellationToken)
        {
            var assembler = new SseChatAssembler();
            using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
            using (var reader = new StreamReader(stream, Encoding.UTF8))
            {
                while (!reader.EndOfStream)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var line = await reader.ReadLineAsync().ConfigureAwait(false);
                    if (line == null)
                        break;
                    if (line.Length == 0)
                        continue;
                    if (!line.StartsWith("data:", StringComparison.Ordinal))
                        continue;

                    var payload = line.Substring(5).TrimStart();
                    if (payload == "[DONE]")
                        break;

                    assembler.ApplyChunkJson(payload);
                    // Only surface text deltas when we are not assembling tool_calls
                    // (final answer round). Avoid flashing partial "thinking" into the chat.
                    if (!assembler.HasToolCalls && onContentDelta != null)
                    {
                        try { onContentDelta(assembler.ContentSoFar); }
                        catch { /* UI may be disposed */ }
                    }
                }
            }

            return assembler.ToCompletion();
        }

        /// <summary>Extract token usage from a chat.completions response body.</summary>
        public static bool TryReadUsage(JObject completion, out int promptTokens, out int completionTokens)
        {
            promptTokens = 0;
            completionTokens = 0;
            if (completion == null)
                return false;
            var usage = completion["usage"];
            if (usage == null)
                return false;
            promptTokens = usage["prompt_tokens"]?.Value<int>() ?? 0;
            completionTokens = usage["completion_tokens"]?.Value<int>() ?? 0;
            return promptTokens > 0 || completionTokens > 0 || usage["total_tokens"] != null;
        }

        private static double ClampTemperature(double temperature)
        {
            if (double.IsNaN(temperature) || double.IsInfinity(temperature))
                return 0;
            if (temperature < 0) return 0;
            if (temperature > 2) return 2;
            return temperature;
        }

        private static int ResolveRetryDelayMs(HttpResponseMessage response, int attempt)
        {
            try
            {
                var retry = response.Headers.RetryAfter;
                if (retry != null && retry.Delta.HasValue && retry.Delta.Value.TotalMilliseconds > 0)
                    return (int)Math.Min(retry.Delta.Value.TotalMilliseconds, 60000);
            }
            catch
            {
                // ignore
            }

            // 2s, 4s, 8s
            return (int)(2000 * Math.Pow(2, attempt - 1));
        }

        private static string HumanizeHttpError(int status, string body)
        {
            if (status == 401 || status == 403)
                return "Нет доступа к ИИ: проверьте API-ключ в Настройки → Ассистент.";
            if (status == 429)
                return "Слишком много запросов к ИИ (лимит API). Подождите 1–2 минуты, нажмите «+ Новый» " +
                       "и повторите короче: сначала только стены, потом помещения.";
            if (status >= 500)
                return "Сервис ИИ временно недоступен. Попробуйте позже.";

            try
            {
                var jo = JObject.Parse(body);
                var msg = jo["error"]?["message"]?.ToString() ?? "";
                if (!string.IsNullOrWhiteSpace(msg))
                {
                    if (msg.IndexOf("image", StringComparison.OrdinalIgnoreCase) >= 0
                        || msg.IndexOf("vision", StringComparison.OrdinalIgnoreCase) >= 0
                        || msg.IndexOf("multimodal", StringComparison.OrdinalIgnoreCase) >= 0
                        || msg.IndexOf("file", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return "Модель не приняла вложение. В Настройки → Ассистент поставьте gpt-4o-mini " +
                               "или gpt-4o и Base URL https://api.openai.com/v1. Детали: " + msg;
                    }
                    return "Ошибка ИИ: " + msg;
                }
            }
            catch
            {
                // ignore
            }

            return "Ошибка обращения к ИИ (HTTP " + status + ").";
        }
    }
}
