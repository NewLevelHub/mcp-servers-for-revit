using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using Autodesk.Windows;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace revit_mcp_plugin.Core.Tutor
{
    /// <summary>
    /// Снимает дерево ленты живого Revit: вкладка → панель → кнопка (REV-150).
    ///
    /// Смысл: каталог кнопок берётся из Revit, который стоит у пользователя — его версия,
    /// его язык интерфейса, его аддоны. Модель не должна вспоминать пути к кнопкам:
    /// выдуманный путь для новичка хуже молчания.
    /// </summary>
    public static class RibbonScanner
    {
        /// <summary>Куда кладём выгрузку — рядом с остальными локальными данными плагина.</summary>
        public static string CatalogDirectory =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".mcp-servers-for-revit",
                "ui-catalog");

        /// <summary>
        /// Обходит ленту целиком. Возвращает null, если AdWindows не отдал ленту
        /// (например, вызов вне UI-потока или Revit ещё не построил интерфейс).
        /// </summary>
        public static JObject Scan(string revitVersion, string language)
        {
            var ribbon = ComponentManager.Ribbon;
            if (ribbon == null)
                return null;

            var tabs = new JArray();
            var stats = new ScanStats();

            foreach (RibbonTab tab in ribbon.Tabs)
            {
                if (tab == null)
                    continue;

                var panels = new JArray();
                foreach (RibbonPanel panel in tab.Panels)
                {
                    var source = panel?.Source;
                    if (source == null)
                        continue;

                    var items = new JArray();
                    foreach (RibbonItem item in source.Items)
                        AppendItem(items, item, stats, depth: 0);

                    if (items.Count == 0)
                        continue;

                    panels.Add(new JObject
                    {
                        ["id"] = source.Id,
                        ["title"] = Clean(source.Title),
                        ["items"] = items,
                    });
                    stats.Panels++;
                }

                if (panels.Count == 0)
                    continue;

                tabs.Add(new JObject
                {
                    ["id"] = tab.Id,
                    ["title"] = Clean(tab.Title),
                    ["isActive"] = tab.IsActive,
                    ["isVisible"] = tab.IsVisible,
                    ["panels"] = panels,
                });
                stats.Tabs++;
            }

            return new JObject
            {
                ["schema"] = "revit-ribbon/1",
                ["scannedAt"] = DateTimeOffset.Now.ToString("O"),
                ["revitVersion"] = revitVersion,
                ["language"] = language,
                ["counts"] = new JObject
                {
                    ["tabs"] = stats.Tabs,
                    ["panels"] = stats.Panels,
                    ["items"] = stats.Items,
                    ["withId"] = stats.WithId,
                    ["withCommand"] = stats.WithCommand,
                },
                ["tabs"] = tabs,
            };
        }

        /// <summary>Пишет выгрузку в файл и возвращает путь.</summary>
        public static string Save(JObject catalog, string revitVersion, string language)
        {
            Directory.CreateDirectory(CatalogDirectory);
            var safeLang = string.IsNullOrWhiteSpace(language) ? "unknown" : language.ToLowerInvariant();
            var path = Path.Combine(CatalogDirectory, $"ribbon-{revitVersion}-{safeLang}.json");
            File.WriteAllText(path, catalog.ToString(Formatting.Indented));
            return path;
        }

        private static void AppendItem(
            JArray target,
            RibbonItem item,
            ScanStats stats,
            int depth)
        {
            if (item == null || depth > 4)
                return;

            var node = new JObject
            {
                ["id"] = item.Id,
                ["text"] = Clean(item.Text),
                ["automationName"] = Clean(item.AutomationName),
                ["type"] = item.GetType().Name,
                ["keyTip"] = Clean(item.KeyTip),
                ["isEnabled"] = item.IsEnabled,
                ["isVisible"] = item.IsVisible,
            };

            var tooltip = DescribeTooltip(item.ToolTip);
            if (tooltip != null)
            {
                node["tooltip"] = tooltip;
                if (tooltip["command"] != null)
                    stats.WithCommand++;
            }

            // Экранные координаты здесь НЕ снимаются намеренно. У кнопок скрытых вкладок
            // визуалов не существует, поэтому при обходе всей ленты их получить нельзя —
            // счётчик показывал бы 9 из 1750 и выглядел как поломка. Координаты берутся
            // по одной кнопке в RibbonSpotlight.PointAt, после того как её вкладка открыта.

            stats.Items++;
            if (!string.IsNullOrWhiteSpace(item.Id))
                stats.WithId++;

            var children = ChildrenOf(item);
            if (children != null)
            {
                var nested = new JArray();
                foreach (RibbonItem child in children)
                    AppendItem(nested, child, stats, depth + 1);
                if (nested.Count > 0)
                    node["items"] = nested;
            }

            target.Add(node);
        }

        /// <summary>Выпадающие списки и вложенные ряды — тоже кнопки, их нельзя терять.</summary>
        private static IEnumerable ChildrenOf(RibbonItem item)
        {
            if (item is RibbonListButton list)
                return list.Items;
            if (item is RibbonRowPanel row)
                return row.Items;
            return null;
        }

        private static JObject DescribeTooltip(object tooltip)
        {
            if (tooltip == null)
                return null;

            if (tooltip is RibbonToolTip rich)
            {
                var node = new JObject
                {
                    ["title"] = Clean(rich.Title),
                    ["content"] = Clean(rich.Content as string),
                    ["expandedContent"] = Clean(rich.ExpandedContent as string),
                };
                // Command — внутренний идентификатор команды Revit, живёт дольше подписи.
                if (!string.IsNullOrWhiteSpace(rich.Command))
                    node["command"] = rich.Command;
                return node;
            }

            var text = Clean(tooltip as string);
            return text == null ? null : new JObject { ["content"] = text };
        }

        /// <summary>Подписи кнопок Revit содержат переносы строк — они мешают поиску.</summary>
        private static string Clean(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;
            return value.Replace("\r\n", " ").Replace("\n", " ").Replace("\r", " ").Trim();
        }

        private sealed class ScanStats
        {
            public int Tabs;
            public int Panels;
            public int Items;
            public int WithId;
            public int WithCommand;
        }
    }
}
