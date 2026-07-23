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
    public sealed class OpenAiCompatibleClient
    {
        private static readonly HttpClient Http = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(3)
        };

        private readonly string _apiKey;
        private readonly string _baseUrl;
        private readonly string _model;

        public OpenAiCompatibleClient(string apiKey, string baseUrl, string model)
        {
            _apiKey = apiKey ?? "";
            _baseUrl = (baseUrl ?? "https://api.openai.com/v1").TrimEnd('/');
            _model = string.IsNullOrWhiteSpace(model) ? "gpt-4o-mini" : model;
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
                ["temperature"] = 0.2
            };
            if (tools != null && tools.Count > 0)
            {
                body["tools"] = tools;
                body["tool_choice"] = "auto";
            }

            var request = new HttpRequestMessage(HttpMethod.Post, _baseUrl + "/chat/completions")
            {
                Content = new StringContent(body.ToString(Formatting.None), Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

            using (var response = await Http.SendAsync(request, cancellationToken).ConfigureAwait(false))
            {
                var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    throw new InvalidOperationException(HumanizeHttpError((int)response.StatusCode, text));
                }

                return JObject.Parse(text);
            }
        }

        private static string HumanizeHttpError(int status, string body)
        {
            if (status == 401 || status == 403)
                return "Нет доступа к ИИ: проверьте API-ключ в Settings → Ассистент.";
            if (status == 429)
                return "Слишком много запросов к ИИ. Подождите немного и повторите.";
            if (status >= 500)
                return "Сервис ИИ временно недоступен. Попробуйте позже.";

            try
            {
                var jo = JObject.Parse(body);
                var msg = jo["error"]?["message"]?.ToString();
                if (!string.IsNullOrWhiteSpace(msg))
                    return "Ошибка ИИ: " + msg;
            }
            catch
            {
                // ignore
            }

            return "Ошибка обращения к ИИ (HTTP " + status + ").";
        }
    }
}
