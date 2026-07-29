using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;

namespace revit_mcp_plugin.Core.Assistant
{
    /// <summary>
    /// Builds OpenAI multimodal user content for the in-Revit assistant.
    /// </summary>
    internal static class ChatContentBuilder
    {
        /// <summary>
        /// Text (+ extracted office text) + image_url.
        /// Documents are inlined as text when possible — Chat Completions <c>file</c> parts
        /// are unreliable on many OpenAI-compatible proxies.
        /// PDF without local text still goes as a <c>file</c> part.
        /// </summary>
        public static JToken BuildUserContent(string text, IList<ChatAttachment> attachments)
        {
            if (attachments == null || attachments.Count == 0)
                return text ?? "";

            var usable = attachments.Where(a => a?.Data != null && a.Data.Length > 0).ToList();
            var docCount = usable.Count(a => !a.IsImage);
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
    }
}
