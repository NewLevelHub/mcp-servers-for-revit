using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace revit_mcp_plugin.Core.Assistant
{
    public sealed class OpenAiCompatibleClient : IChatCompletionsClient
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

        public async Task<JObject> ChatCompletionsAsync(
            JArray messages,
            JArray tools,
            CancellationToken cancellationToken)
        {
            var body = new JObject
            {
                ["model"] = _model,
                ["messages"] = messages,
                // Tool-calling: low temperature; overridable via ServiceSettings.AssistantTemperature (REV-121).
                ["temperature"] = _temperature
            };
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

            var request = new HttpRequestMessage(HttpMethod.Post, _baseUrl + "/chat/completions")
            {
                Content = new StringContent(body.ToString(Formatting.None), Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

            const int maxAttempts = 4;
            try
            {
                for (var attempt = 1; attempt <= maxAttempts; attempt++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    using (var response = await Http.SendAsync(request, cancellationToken).ConfigureAwait(false))
                    {
                        var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        if (response.IsSuccessStatusCode)
                        {
                            // Full API body is returned so callers can read usage.prompt_tokens /
                            // usage.completion_tokens for dialog logs (REV-121).
                            return JObject.Parse(text);
                        }

                        var status = (int)response.StatusCode;
                        if (status == 429 && attempt < maxAttempts)
                        {
                            var delayMs = ResolveRetryDelayMs(response, attempt);
                            await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
                            // HttpContent can only be read once — rebuild request for retry
                            request.Dispose();
                            request = new HttpRequestMessage(HttpMethod.Post, _baseUrl + "/chat/completions")
                            {
                                Content = new StringContent(body.ToString(Formatting.None), Encoding.UTF8, "application/json")
                            };
                            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
                            continue;
                        }

                        throw new InvalidOperationException(HumanizeHttpError(status, text));
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
