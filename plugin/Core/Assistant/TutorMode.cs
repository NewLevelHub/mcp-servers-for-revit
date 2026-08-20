using System;
using System.Collections.Generic;

namespace revit_mcp_plugin.Core.Assistant
{
    /// <summary>
    /// Тумблер «Режим наставника» (REV-154).
    ///
    /// Логика вынесена из панели, чтобы её можно было проверить тестами:
    /// в UI остаётся только галочка и текст, а решение «какие профили уходят агенту»
    /// принимается здесь. Пока режим включён, он перебивает и роутинг, и чипы —
    /// иначе достаточно нажать «Оси и размеры», чтобы обойти обучение и начать чертить.
    /// </summary>
    public static class TutorMode
    {
        public const string EnabledNotice =
            "Режим наставника включён. Веду по шагам и объясняю, но ничего не делаю за вас: " +
            "создавать, удалять и править элементы в этом режиме нельзя. Выключить — тем же тумблером.";

        public const string DisabledNotice =
            "Режим наставника выключен. Работаю как обычно — выполняю задачи сам.";

        /// <summary>
        /// Профили для запуска. Включённый режим наставника всегда даёт ровно learning,
        /// чем бы ни был вызван запуск — чипом, роутером или свободным вводом.
        /// </summary>
        public static IReadOnlyList<string> ResolveProfiles(bool enabled, IReadOnlyList<string> requested)
        {
            if (enabled)
                return new[] { ToolCatalog.Profiles.Learning };

            // Выключенный режим не должен оставлять learning в списке: обычная работа
            // с урезанным до чтения каталогом выглядит как «ассистент сломался».
            if (requested == null)
                return null;

            var cleaned = new List<string>();
            foreach (var p in requested)
            {
                if (string.IsNullOrWhiteSpace(p))
                    continue;
                if (p.Trim().Equals(ToolCatalog.Profiles.Learning, StringComparison.OrdinalIgnoreCase))
                    continue;
                cleaned.Add(p);
            }

            return cleaned.Count == 0 ? null : cleaned;
        }

        public static string NoticeFor(bool enabled) => enabled ? EnabledNotice : DisabledNotice;
    }
}
