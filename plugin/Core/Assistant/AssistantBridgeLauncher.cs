using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using revit_mcp_plugin.Configuration;
using revit_mcp_plugin.Utils;

namespace revit_mcp_plugin.Core.Assistant
{
    /// <summary>
    /// Starts the Node assistant-bridge process (Cursor SDK + Revit MCP).
    /// </summary>
    public static class AssistantBridgeLauncher
    {
        private static Process _process;
        private static readonly object Gate = new object();
        private static readonly object LogGate = new object();

        /// <summary>
        /// Cursor's auto-router id, verified against Cursor.models.list().
        /// "auto-smart" is not a real model and the API rejects it.
        /// </summary>
        public const string DefaultModelId = "default";

        /// <summary>Per-launch secret; the bridge rejects requests without it.</summary>
        public static string Token { get; private set; } = "";

        /// <summary>Settings the running bridge was started with; a change forces a restart.</summary>
        private static string _fingerprint;

        private static string Fingerprint(ServiceSettings settings)
        {
            return string.Join(
                "",
                settings.AssistantCursorApiKey ?? "",
                CursorModelCatalog.Normalize(settings.AssistantCursorModel),
                settings.AssistantBridgePort.ToString(),
                settings.AssistantNodePath ?? "",
                settings.AssistantBridgePath ?? "",
                settings.AssistantRulesPath ?? "",
                settings.AssistantMcpServerPath ?? "");
        }

        public static bool EnsureRunning(ServiceSettings settings)
        {
            settings = settings ?? new ServiceSettings();

            lock (Gate)
            {
                var fingerprint = Fingerprint(settings);

                if (_process != null && !_process.HasExited)
                {
                    // A key/model/port edit must reach the running bridge.
                    if (string.Equals(_fingerprint, fingerprint, StringComparison.Ordinal))
                        return true;

                    StopLocked();
                }

                if (string.IsNullOrWhiteSpace(settings.AssistantCursorApiKey))
                    throw new InvalidOperationException(
                        "Не задан Cursor API key. Откройте Настройки → Ассистент и вставьте ключ.");

                var bridgeJs = ResolveBridgeEntry(settings);
                if (!File.Exists(bridgeJs))
                    throw new InvalidOperationException(
                        "Не найден assistant-bridge. Соберите: cd assistant-bridge && npm install && npm run build");

                var node = ResolveNodeExe(settings);
                if (string.IsNullOrWhiteSpace(node) || !File.Exists(node))
                    throw new InvalidOperationException(
                        "Node.js 22.13+ не найден. Укажите путь в Настройки → Ассистент или установите Node.js.");

                var rulesCwd = ResolveRulesCwd(settings);
                var mcpJs = ResolveMcpServerJs(settings);
                var mcpCwd = Path.GetDirectoryName(mcpJs) ?? rulesCwd;

                var psi = new ProcessStartInfo
                {
                    FileName = node,
                    Arguments = "\"" + bridgeJs + "\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    WorkingDirectory = Path.GetDirectoryName(bridgeJs) ?? rulesCwd,
                };

                Token = Guid.NewGuid().ToString("N");

                psi.EnvironmentVariables["CURSOR_API_KEY"] = settings.AssistantCursorApiKey ?? "";
                psi.EnvironmentVariables["ASSISTANT_BRIDGE_PORT"] = settings.AssistantBridgePort.ToString();
                psi.EnvironmentVariables["ASSISTANT_BRIDGE_TOKEN"] = Token;
                psi.EnvironmentVariables["ASSISTANT_CURSOR_MODEL"] =
                    CursorModelCatalog.Normalize(settings.AssistantCursorModel);
                psi.EnvironmentVariables["CURSOR_RULES_CWD"] = rulesCwd;
                psi.EnvironmentVariables["REVIT_MCP_NODE"] = node;
                psi.EnvironmentVariables["REVIT_MCP_SERVER_JS"] = mcpJs;
                psi.EnvironmentVariables["REVIT_MCP_SERVER_CWD"] = mcpCwd;

                _process = Process.Start(psi);
                if (_process == null)
                    throw new InvalidOperationException("Не удалось запустить assistant-bridge.");

                // Both pipes are redirected, so both must be drained: an unread
                // pipe fills its buffer and blocks the bridge permanently.
                _process.OutputDataReceived += (_, e) => AppendLog("out", e.Data);
                _process.ErrorDataReceived += (_, e) => AppendLog("err", e.Data);
                _process.BeginOutputReadLine();
                _process.BeginErrorReadLine();

                WaitForHealth(settings, timeoutMs: 45000);
                _fingerprint = fingerprint;
                return true;
            }
        }

        private static void WaitForHealth(ServiceSettings settings, int timeoutMs)
        {
            var url = HealthUrl(settings);
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            Exception last = null;

            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    using (var client = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(3) })
                    using (var response = client.GetAsync(url).GetAwaiter().GetResult())
                    {
                        if (response.IsSuccessStatusCode)
                            return;
                    }
                }
                catch (Exception ex)
                {
                    last = ex;
                }

                Thread.Sleep(500);
            }

            throw new InvalidOperationException(
                "assistant-bridge не ответил на /health. Проверьте Node 22.13+ и Cursor API key. Лог: "
                + LogPath
                + (last != null ? " " + last.Message : ""));
        }

        public static void Stop()
        {
            lock (Gate)
            {
                StopLocked();
            }
        }

        private static void StopLocked()
        {
            try
            {
                if (_process != null && !_process.HasExited)
                    _process.Kill();
            }
            catch { /* ignore */ }
            finally
            {
                _process = null;
                Token = "";
                _fingerprint = null;
            }
        }

        /// <summary>Bridge stdout/stderr; the only place to look when a turn fails.</summary>
        public static string LogPath
        {
            get
            {
                return Path.Combine(
                    PathManager.GetLogsDirectoryPath(),
                    "assistant-bridge_" + DateTime.Now.ToString("yyyyMMdd") + ".log");
            }
        }

        private static void AppendLog(string channel, string line)
        {
            if (line == null)
                return;

            try
            {
                lock (LogGate)
                {
                    File.AppendAllText(
                        LogPath,
                        DateTime.Now.ToString("HH:mm:ss") + " [" + channel + "] " + line + Environment.NewLine);
                }
            }
            catch { /* logging must never break a turn */ }
        }

        public static string HealthUrl(ServiceSettings settings)
        {
            return BaseUrl(settings) + "/health";
        }

        public static string ChatUrl(ServiceSettings settings)
        {
            return BaseUrl(settings) + "/v1/chat";
        }

        public static string CancelUrl(ServiceSettings settings)
        {
            return BaseUrl(settings) + "/v1/cancel";
        }

        public static string ConfirmUrl(ServiceSettings settings)
        {
            return BaseUrl(settings) + "/v1/confirm";
        }

        private static string BaseUrl(ServiceSettings settings)
        {
            var port = settings?.AssistantBridgePort ?? 8790;
            return "http://127.0.0.1:" + port;
        }

        private static string ResolveNodeExe(ServiceSettings settings)
        {
            if (!string.IsNullOrWhiteSpace(settings.AssistantNodePath)
                && File.Exists(settings.AssistantNodePath))
                return settings.AssistantNodePath.Trim();

            var candidates = new[]
            {
                @"C:\Program Files\nodejs\node.exe",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs", "node.exe"),
            };

            foreach (var c in candidates)
            {
                if (File.Exists(c))
                    return c;
            }

            return "node";
        }

        private static string ResolveBridgeEntry(ServiceSettings settings)
        {
            if (!string.IsNullOrWhiteSpace(settings.AssistantBridgePath)
                && File.Exists(settings.AssistantBridgePath))
                return settings.AssistantBridgePath.Trim();

            var pluginDir = PathManager.GetAppDataDirectoryPath();
            var bundled = Path.Combine(pluginDir, "assistant-bridge", "dist", "index.js");
            if (File.Exists(bundled))
                return bundled;

            // Dev layout: repo root next to plugin publish folder is unreliable; walk up from plugin.
            var dev = Path.GetFullPath(Path.Combine(pluginDir, "..", "..", "..", "..", "assistant-bridge", "dist", "index.js"));
            if (File.Exists(dev))
                return dev;

            dev = Path.GetFullPath(Path.Combine(pluginDir, "..", "..", "..", "assistant-bridge", "dist", "index.js"));
            return dev;
        }

        private static string ResolveMcpServerJs(ServiceSettings settings)
        {
            if (!string.IsNullOrWhiteSpace(settings.AssistantMcpServerPath)
                && File.Exists(settings.AssistantMcpServerPath))
                return settings.AssistantMcpServerPath.Trim();

            var pluginDir = PathManager.GetAppDataDirectoryPath();
            var bundled = Path.Combine(pluginDir, "mcp-server", "build", "index.js");
            if (File.Exists(bundled))
                return bundled;

            bundled = Path.Combine(pluginDir, "mcp-server", "index.js");
            if (File.Exists(bundled))
                return bundled;

            var dev = Path.GetFullPath(Path.Combine(pluginDir, "..", "..", "..", "..", "server", "build", "index.js"));
            if (File.Exists(dev))
                return dev;

            dev = Path.GetFullPath(Path.Combine(pluginDir, "..", "..", "..", "server", "build", "index.js"));
            return dev;
        }

        private static string ResolveRulesCwd(ServiceSettings settings)
        {
            if (!string.IsNullOrWhiteSpace(settings.AssistantRulesPath)
                && Directory.Exists(settings.AssistantRulesPath))
                return settings.AssistantRulesPath.Trim();

            var pluginDir = PathManager.GetAppDataDirectoryPath();
            var bundled = Path.Combine(pluginDir, "assistant-bundle");
            if (Directory.Exists(bundled))
                return bundled;

            var dev = Path.GetFullPath(Path.Combine(pluginDir, "..", "..", "..", ".."));
            if (Directory.Exists(Path.Combine(dev, ".cursor", "rules")))
                return dev;

            dev = Path.GetFullPath(Path.Combine(pluginDir, "..", "..", "..", "..", ".."));
            if (Directory.Exists(Path.Combine(dev, ".cursor", "rules")))
                return dev;

            return pluginDir;
        }
    }
}
