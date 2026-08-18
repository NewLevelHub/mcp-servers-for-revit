using System;
using System.Collections.Concurrent;
using revit_mcp_plugin.Utils;

namespace revit_mcp_plugin.Core
{
    /// <summary>
    /// Why a command declared in command.json never reached the registry, so a
    /// failed call can say what to do about it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Before this, calling a command that was merely unticked in Settings answered
    /// «未找到方法: 'tag_elements'» — Chinese, and silent about the one-click fix. It
    /// showed up in a real session log on 2026-08-17: tag_elements failed 1 of 3
    /// calls that way, and nothing in the message suggested looking at Settings.
    /// </para>
    /// <para>
    /// Populated by <see cref="CommandManager"/> as it walks the config, read by
    /// <see cref="CommandExecutor"/> when a lookup misses. A shared store rather
    /// than a constructor argument because loading and dispatch are wired up
    /// separately (Application → CommandManager, SocketService → CommandExecutor)
    /// and neither owns the other.
    /// </para>
    /// </remarks>
    public static class CommandAvailability
    {
        private static readonly ConcurrentDictionary<string, string> _reasons =
            new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Forget every recorded reason. Call before a fresh load pass.</summary>
        public static void Reset()
        {
            _reasons.Clear();
        }

        /// <summary>Unticked in Revit → Настройки.</summary>
        public static void RecordDisabled(string commandName)
        {
            Record(commandName,
                string.Format(
                    "Команда «{0}» выключена в настройках. " +
                    "Revit → лента mcp-servers-for-revit → Настройки → отметьте «{0}» → Сохранить.",
                    commandName));
        }

        /// <summary>Present in the config, but not built for this Revit version.</summary>
        public static void RecordUnsupportedVersion(string commandName, string revitVersion)
        {
            Record(commandName,
                string.Format(
                    "Команда «{0}» не поддерживается в Revit {1}. " +
                    "Выполните это действие вручную или откройте проект в поддерживаемой версии.",
                    commandName, revitVersion));
        }

        /// <summary>The assembly was there but would not load.</summary>
        public static void RecordLoadFailed(string commandName, string error)
        {
            Record(commandName,
                string.Format(
                    "Команда «{0}» не загрузилась при запуске Revit: {1}. " +
                    "Проверьте, что папка Commands скопирована целиком, и перезапустите Revit.",
                    commandName, error));
        }

        private static void Record(string commandName, string reason)
        {
            if (string.IsNullOrWhiteSpace(commandName)) return;
            _reasons[commandName] = reason;
        }

        /// <summary>
        /// Message for a command that is not in the registry. Falls back to the
        /// version-skew explanation, which is what an unrecorded name almost always
        /// means: the MCP server offers a tool this plugin build does not carry.
        /// </summary>
        public static string DescribeMissing(string commandName)
        {
            string reason;
            if (_reasons.TryGetValue(commandName ?? string.Empty, out reason))
            {
                return reason;
            }

            return string.Format(
                "Команда «{0}» отсутствует в этой сборке плагина ({1}). " +
                "Скорее всего MCP-сервер новее плагина — обновите плагин в Revit " +
                "(лента mcp-servers-for-revit → Обновление) и перезапустите Revit.",
                commandName,
                BuildVersion.Current);
        }
    }
}
