using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace revit_mcp_plugin.Core.Assistant
{
    /// <summary>
    /// Deterministic «Проверить нормы» for the in-Revit chip — no LLM round-trip required.
    /// </summary>
    public static class NormAuditPresetRunner
    {
        public static (string Reply, IList<string> Done) RunHighlight(bool annotate = true)
        {
            var raw = NormAuditOrchestrator.Run(new JObject
            {
                ["mode"] = "highlight",
                ["annotate"] = annotate
            }.ToString(Newtonsoft.Json.Formatting.None));

            JObject audit;
            try
            {
                audit = JObject.Parse(raw);
            }
            catch (Exception ex)
            {
                return ($"Не удалось выполнить проверку норм: {ex.Message}", new[] { "ошибка аудита" });
            }

            var success = audit["success"]?.Value<bool?>() ?? audit["Success"]?.Value<bool?>() ?? false;
            if (!success)
            {
                var msg = audit["message"]?.ToString() ?? audit["Message"]?.ToString() ?? "неизвестная ошибка";
                return ($"Проверка норм не выполнена: {msg}", new[] { "ошибка аудита" });
            }

            var summary = audit["summary"]?.ToString() ?? "";
            var level = audit["levelName"]?.ToString() ?? "";
            var findings = audit["findings"] as JArray ?? new JArray();
            var violationCount = 0;
            foreach (JToken f in findings)
            {
                if ((f["status"]?.ToString() ?? "").Equals("violation", StringComparison.OrdinalIgnoreCase))
                    violationCount++;
            }

            var highlight = audit["highlight"] as JObject;
            var roomCount = highlight?["roomCount"]?.Value<int?>() ?? 0;
            var doorCount = highlight?["doorCount"]?.Value<int?>() ?? 0;
            var highlightOk = highlight?["success"]?.Value<bool?>() ?? highlight?["Success"]?.Value<bool?>() ?? true;

            var done = new List<string> { $"проверка норм: {violationCount} наруш." };
            if (roomCount > 0)
                done.Add($"заливка: {roomCount}");
            if (doorCount > 0)
                done.Add($"двери: {doorCount}");
            if (annotate && violationCount > 0)
                done.Add("выноски");

            if (!highlightOk && highlight != null)
            {
                var err = highlight["message"]?.ToString() ?? "";
                if (!string.IsNullOrWhiteSpace(err))
                    done.Add("ошибка подсветки: " + err);
            }

            var levelBit = string.IsNullOrWhiteSpace(level) ? "" : $" ({level})";
            string reply;
            if (violationCount == 0)
            {
                reply =
                    $"По активному этажу{levelBit} нарушений не найдено (коридоры, глубина жилых, лоджии/балконы, противопожарные двери). " +
                    summary;
            }
            else
            {
                reply =
                    $"Проверка этажа{levelBit}: {violationCount} нарушений. " +
                    $"Подсвечено помещений: {roomCount}, дверей: {doorCount}. " +
                    (annotate ? "Выноски с цитатами норм поставлены. " : "") +
                    summary;
            }

            if (!NormCatalogStore.IsAvailable)
            {
                reply += " Каталог PDF не загружен — использованы встроенные нормы СП РК 3.02-101-2012.";
            }

            return (reply.Trim(), done);
        }
    }
}
