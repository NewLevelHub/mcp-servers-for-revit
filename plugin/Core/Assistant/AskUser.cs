using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace revit_mcp_plugin.Core.Assistant
{
    /// <summary>Pending ask_user card for the chat UI (REV-125).</summary>
    public sealed class PendingAskUser
    {
        public string Question { get; set; }
        public IReadOnlyList<string> Options { get; set; } = Array.Empty<string>();
        public bool AllowFreeText { get; set; } = true;
    }

    /// <summary>Architect's answer to ask_user.</summary>
    public sealed class AskUserAnswer
    {
        public bool Cancelled { get; set; }
        public string SelectedOption { get; set; }
        public string FreeText { get; set; }

        public string DisplayText
        {
            get
            {
                if (Cancelled)
                    return "";
                if (!string.IsNullOrWhiteSpace(SelectedOption))
                    return SelectedOption.Trim();
                return (FreeText ?? "").Trim();
            }
        }
    }

    /// <summary>Parse / validate ask_user tool arguments.</summary>
    public static class AskUserParser
    {
        public const int MaxOptions = 6;
        public const int MinOptions = 2;

        public static bool TryParse(string argsJson, out PendingAskUser pending, out string error)
        {
            pending = null;
            error = null;
            try
            {
                var args = JObject.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson);
                var question = args["question"]?.ToString()?.Trim();
                if (string.IsNullOrWhiteSpace(question))
                {
                    error = "Нужен question.";
                    return false;
                }

                var options = new List<string>();
                if (args["options"] is JArray arr)
                {
                    foreach (var t in arr)
                    {
                        var s = t?.ToString()?.Trim();
                        if (!string.IsNullOrWhiteSpace(s) && options.Count < MaxOptions)
                            options.Add(s);
                    }
                }

                if (options.Count < MinOptions)
                {
                    error = $"Нужно от {MinOptions} до {MaxOptions} options.";
                    return false;
                }

                var allowFree = true;
                if (args["allowFreeText"] != null && args["allowFreeText"].Type != JTokenType.Null)
                    allowFree = args["allowFreeText"].Value<bool>();

                pending = new PendingAskUser
                {
                    Question = question,
                    Options = options,
                    AllowFreeText = allowFree,
                };
                return true;
            }
            catch (Exception ex)
            {
                error = "Некорректные аргументы ask_user: " + ex.Message;
                return false;
            }
        }

        public static JObject ToSuccessPayload(AskUserAnswer answer)
        {
            var display = answer?.DisplayText ?? "";
            return new JObject
            {
                ["success"] = true,
                ["cancelled"] = false,
                ["selected"] = answer?.SelectedOption,
                ["freeText"] = answer?.FreeText,
                ["answer"] = display,
                ["summary"] = string.IsNullOrWhiteSpace(display) ? "ответ получен" : ("ответ: " + display),
            };
        }

        public static JObject ToCancelledPayload()
        {
            return new JObject
            {
                ["success"] = false,
                ["cancelled"] = true,
                ["message"] = "Архитектор отменил уточнение.",
            };
        }
    }
}
