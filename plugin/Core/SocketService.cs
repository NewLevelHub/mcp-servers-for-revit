using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RevitMCPSDK.API.Models.JsonRPC;
using RevitMCPSDK.API.Interfaces;
using revit_mcp_plugin.Configuration;
using revit_mcp_plugin.Utils;

namespace revit_mcp_plugin.Core
{
    public class SocketService
    {
        private static SocketService _instance;
        private TcpListener _listener;
        private Thread _listenerThread;
        private bool _isRunning;
        private int _port = 8080;
        private int _activeClients;
        private bool _hadClient;
        private string _lastStartError;
        /// <summary>Guards the Start/Stop transition and <see cref="_clients"/>.</summary>
        private readonly object _sync = new object();
        /// <summary>Accepted connections, so Stop can close them instead of leaking the port.</summary>
        private readonly List<TcpClient> _clients = new List<TcpClient>();
        private UIApplication _uiApp;
        private ICommandRegistry _commandRegistry;
        private Logger _logger;
        private CommandExecutor _commandExecutor;

        public const string PingMethod = "ping";
        public static SocketService Instance
        {
            get
            {
                if(_instance == null)
                    _instance = new SocketService();
                return _instance;
            }
        }

        private SocketService()
        {
            _commandRegistry = new RevitCommandRegistry();
            _logger = new Logger();
        }

        public bool IsRunning => _isRunning;

        /// <summary>Message from the last failed <see cref="Start"/>, or null after a good start.</summary>
        public string LastStartError => _lastStartError;

        public int Port
        {
            get => _port;
            set => _port = value;
        }

        public Logger Logger => _logger;

        /// <summary>
        /// Execute a JSON-RPC command in-process (used by the in-Revit AI assistant).
        /// Requires <see cref="Initialize"/> and a running command registry.
        /// </summary>
        public string ExecuteJsonRpcLocal(string method, string paramsJson)
        {
            if (_commandExecutor == null)
            {
                return JsonConvert.SerializeObject(new
                {
                    jsonrpc = "2.0",
                    id = "local",
                    error = new { code = -32000, message = "Сервер команд не инициализирован. Включите MCP Switch." }
                });
            }

            var id = Guid.NewGuid().ToString("N");
            JToken parameters;
            try
            {
                parameters = string.IsNullOrWhiteSpace(paramsJson)
                    ? new JObject()
                    : JToken.Parse(paramsJson);
            }
            catch (Exception ex)
            {
                return JsonConvert.SerializeObject(new
                {
                    jsonrpc = "2.0",
                    id,
                    error = new { code = -32602, message = "Некорректные параметры: " + ex.Message }
                });
            }

            var requestObj = new JObject
            {
                ["jsonrpc"] = "2.0",
                ["method"] = method,
                ["params"] = parameters,
                ["id"] = id
            };

            return ProcessJsonRPCRequest(requestObj.ToString(Formatting.None));
        }

        // Initialization.
        public void Initialize(UIApplication uiApp)
        {
            _uiApp = uiApp;

            ExternalEventManager.Instance.Initialize(uiApp, _logger);

            var versionAdapter = new RevitMCPSDK.API.Utils.RevitVersionAdapter(_uiApp.Application);
            string currentVersion = versionAdapter.GetRevitVersion();
            _logger.Info("当前 Revit 版本: {0}\nCurrent Revit version: {0}", currentVersion);

            _commandExecutor = new CommandExecutor(_commandRegistry, _logger);

            ConfigurationManager configManager = new ConfigurationManager(_logger);
            configManager.LoadConfiguration();

            _port = 8080;

            CommandManager commandManager = new CommandManager(
                _commandRegistry, _logger, configManager, _uiApp);
            commandManager.LoadCommands();

            _logger.Info($"Socket service initialized on port {_port}");
            _logger.Info("Command metrics log: {0}", _logger.MetricsLogFilePath);
        }

        /// <summary>
        /// Opens the listener. Auto-start (Idling), the ribbon command and the assistant pane all
        /// call this, so the whole transition is serialized: two overlapping Starts used to let the
        /// loser's catch clear _isRunning and null _listener for the winner's live socket, leaving
        /// port 8080 bound with no accept loop and no reference to close it.
        /// </summary>
        public void Start()
        {
            lock (_sync)
            {
                if (_isRunning) return;

                try
                {
                    _activeClients = 0;
                    _hadClient = false;
                    _listener = new TcpListener(IPAddress.Any, _port);
                    _listener.Start();

                    // Only now: ListenForClients loops on _isRunning, so raising the flag before
                    // the bind succeeds leaves it on for a listener that never came up.
                    _isRunning = true;
                    _lastStartError = null;

                    _listenerThread = new Thread(ListenForClients)
                    {
                        IsBackground = true
                    };
                    _listenerThread.Start();
                    _logger.Info("Socket service listening on port {0}", _port);
                }
                catch (Exception ex)
                {
                    // Release the socket before giving up, otherwise the port stays taken and every
                    // later Start() dies with "address already in use".
                    _isRunning = false;
                    try { _listener?.Stop(); } catch { }
                    _listener = null;
                    _lastStartError = ex.Message;
                    _logger.Error("Socket service failed to start on port {0}: {1}", _port, ex.Message);
                    RibbonStatusManager.UpdateStatus(McpLinkStatus.Offline, ex.Message);
                    return;
                }
            }

            // Outside the lock and the try: a ribbon hiccup must neither deadlock nor tear down a
            // listener that is already up.
            try
            {
                RefreshRibbonStatus();
            }
            catch (Exception ex)
            {
                _logger.Error("Ribbon status refresh failed after start: {0}", ex.Message);
            }
        }

        /// <summary>
        /// Closes the listener and every accepted connection. Idempotent on purpose.
        /// </summary>
        public void Stop()
        {
            Thread listenerThread;

            lock (_sync)
            {
                // No early return on !_isRunning. The flag and the socket can disagree (a failed
                // Start, a client thread still parked in Read), and skipping the close on that
                // mismatch is exactly what left the port bound with nobody accepting.
                _isRunning = false;
                _activeClients = 0;
                _hadClient = false;

                // Accepted sockets keep local port 8080 occupied on their own, and their threads
                // sit blocked in stream.Read - clearing _isRunning never wakes them. Closing the
                // socket does: Read throws, the finally releases the client.
                foreach (var client in _clients.ToArray())
                {
                    try { client.Close(); } catch { }
                }
                _clients.Clear();

                try
                {
                    _listener?.Stop();
                }
                catch (Exception ex)
                {
                    _logger.Error("Socket service failed to release port {0}: {1}", _port, ex.Message);
                }

                _listener = null;
                listenerThread = _listenerThread;
                _listenerThread = null;
            }

            if (listenerThread != null && listenerThread.IsAlive)
            {
                listenerThread.Join(1000);
            }

            _logger.Info("Socket service stopped, port {0} released", _port);
            RibbonStatusManager.UpdateStatus(McpLinkStatus.Offline);
        }

        private void RefreshRibbonStatus(string lastError = null)
        {
            if (!_isRunning)
            {
                RibbonStatusManager.UpdateStatus(McpLinkStatus.Offline, lastError);
                return;
            }

            if (_activeClients > 0)
            {
                RibbonStatusManager.UpdateStatus(McpLinkStatus.Connected);
                return;
            }

            // After Cursor drops the socket, show Reconnecting while the listener stays up.
            // Fresh Open Server (no client yet) stays Connected so the ribbon matches legacy UX.
            RibbonStatusManager.UpdateStatus(
                _hadClient ? McpLinkStatus.Reconnecting : McpLinkStatus.Connected,
                lastError);
        }

        private void ListenForClients()
        {
            try
            {
                while (_isRunning)
                {
                    TcpClient client = _listener.AcceptTcpClient();

                    Thread clientThread = new Thread(HandleClientCommunication)
                    {
                        IsBackground = true
                    };
                    clientThread.Start(client);
                }
            }
            catch (SocketException)
            {
                
            }
            catch(Exception)
            {
                // log
            }
        }

        private void HandleClientCommunication(object clientObj)
        {
            TcpClient tcpClient = (TcpClient)clientObj;
            lock (_sync)
            {
                _clients.Add(tcpClient);
            }

            NetworkStream stream = tcpClient.GetStream();
            var readBuffer = new List<byte>();
            var chunk = new byte[8192];

            Interlocked.Increment(ref _activeClients);
            _hadClient = true;
            RefreshRibbonStatus();
            _logger.Info("MCP client connected (active={0})", _activeClients);

            try
            {
                while (_isRunning && tcpClient.Connected)
                {
                    int bytesRead = 0;

                    try
                    {
                        bytesRead = stream.Read(chunk, 0, chunk.Length);
                    }
                    catch (IOException)
                    {
                        break;
                    }

                    if (bytesRead == 0)
                    {
                        break;
                    }

                    for (int i = 0; i < bytesRead; i++)
                    {
                        readBuffer.Add(chunk[i]);
                    }

                    while (TryExtractFramedMessage(readBuffer, out string message))
                    {
                        string response = ProcessJsonRPCRequest(message);
                        WriteFramedMessage(stream, response);
                    }
                }
            }
            catch(Exception)
            {
                // log
            }
            finally
            {
                lock (_sync)
                {
                    _clients.Remove(tcpClient);
                }

                tcpClient.Close();
                var remaining = Interlocked.Decrement(ref _activeClients);
                if (remaining < 0)
                    Interlocked.Exchange(ref _activeClients, 0);
                RefreshRibbonStatus("Cursor disconnected");
                _logger.Info("MCP client disconnected (active={0})", Math.Max(0, remaining));
            }
        }

        private const int MaxFrameBytes = 50 * 1024 * 1024;

        private static bool TryExtractFramedMessage(List<byte> buffer, out string message)
        {
            message = null;
            if (buffer.Count < 4)
                return false;

            int length = (buffer[0] << 24) | (buffer[1] << 16) | (buffer[2] << 8) | buffer[3];
            if (length <= 0 || length > MaxFrameBytes)
                throw new InvalidDataException($"Invalid TCP frame length: {length}");

            int totalLength = 4 + length;
            if (buffer.Count < totalLength)
                return false;

            message = Encoding.UTF8.GetString(buffer.GetRange(4, length).ToArray());
            buffer.RemoveRange(0, totalLength);
            return true;
        }

        private static void WriteFramedMessage(NetworkStream stream, string json)
        {
            byte[] body = Encoding.UTF8.GetBytes(json);
            byte[] header = new byte[4];
            header[0] = (byte)((body.Length >> 24) & 0xFF);
            header[1] = (byte)((body.Length >> 16) & 0xFF);
            header[2] = (byte)((body.Length >> 8) & 0xFF);
            header[3] = (byte)(body.Length & 0xFF);
            stream.Write(header, 0, header.Length);
            stream.Write(body, 0, body.Length);
        }

        private string ProcessJsonRPCRequest(string requestJson)
        {
            JsonRPCRequest request = null;
            string commandName = "unknown";
            var stopwatch = Stopwatch.StartNew();

            try
            {
                request = JsonConvert.DeserializeObject<JsonRPCRequest>(requestJson);

                if (request == null || !request.IsValid())
                {
                    string response = CreateErrorResponse(
                        null,
                        JsonRPCErrorCodes.InvalidRequest,
                        "Invalid JSON-RPC request"
                    );
                    LogRequestMetrics(commandName, stopwatch, response, false, "Invalid JSON-RPC request");
                    return response;
                }

                commandName = request.Method;

                // Heartbeat: answer on the socket thread without ExternalEvent / command registry.
                if (string.Equals(commandName, PingMethod, StringComparison.OrdinalIgnoreCase))
                {
                    string pingResponse = CreatePingResponse(request.Id);
                    // Do not write ping to command-metrics (noise every ~10s).
                    return pingResponse;
                }

                string responseJson = _commandExecutor.ExecuteCommand(request);
                bool success = IsSuccessResponse(responseJson);
                string errorDetails = success ? null : ExtractErrorDetails(responseJson);
                LogRequestMetrics(commandName, stopwatch, responseJson, success, errorDetails);

                if (string.Equals(commandName, CommandExecutor.BatchExecuteMethod, StringComparison.OrdinalIgnoreCase)
                    && success)
                {
                    LogBatchSubCommandMetrics(responseJson);
                }

                return responseJson;
            }
            catch (JsonException ex)
            {
                string response = CreateErrorResponse(
                    null,
                    JsonRPCErrorCodes.ParseError,
                    "Invalid JSON"
                );
                LogRequestMetrics(commandName, stopwatch, response, false, ex.ToString());
                return response;
            }
            catch (Exception ex)
            {
                string response = CreateErrorResponse(
                    request?.Id,
                    JsonRPCErrorCodes.InternalError,
                    $"Internal error: {ex.Message}",
                    new { stackTrace = ex.ToString(), revitMessage = ex.Message }
                );
                LogRequestMetrics(commandName, stopwatch, response, false, ex.ToString());
                return response;
            }
        }

        private void LogRequestMetrics(
            string command,
            Stopwatch stopwatch,
            string responseJson,
            bool success,
            string errorDetails)
        {
            stopwatch.Stop();
            _logger.LogCommandMetrics(
                command,
                stopwatch.ElapsedMilliseconds,
                success,
                Encoding.UTF8.GetByteCount(responseJson ?? string.Empty),
                errorDetails);
        }

        private void LogBatchSubCommandMetrics(string responseJson)
        {
            try
            {
                var response = JObject.Parse(responseJson);
                var results = response["result"]?["results"] as JArray;
                if (results == null)
                    return;

                foreach (var item in results)
                {
                    var command = item["command"]?.ToString() ?? "unknown";
                    var success = item["success"]?.Value<bool>() ?? false;
                    string errorDetails = null;

                    if (!success)
                    {
                        var error = item["error"];
                        errorDetails = error?["data"]?["stackTrace"]?.ToString()
                            ?? error?["message"]?.ToString();
                    }

                    var itemJson = item.ToString(Formatting.None);
                    _logger.LogCommandMetrics(
                        $"{CommandExecutor.BatchExecuteMethod}:{command}",
                        0,
                        success,
                        Encoding.UTF8.GetByteCount(itemJson),
                        errorDetails);
                }
            }
            catch
            {
                // Metrics logging should not affect command execution.
            }
        }

        private static bool IsSuccessResponse(string responseJson)
        {
            try
            {
                var token = JObject.Parse(responseJson);
                return token["error"] == null;
            }
            catch
            {
                return false;
            }
        }

        private static string ExtractErrorDetails(string responseJson)
        {
            try
            {
                var token = JObject.Parse(responseJson);
                var error = token["error"];
                if (error == null)
                    return null;

                var data = error["data"];
                if (data?["stackTrace"] != null)
                    return data["stackTrace"].ToString();

                return error["message"]?.ToString();
            }
            catch
            {
                return responseJson;
            }
        }

        private string CreateErrorResponse(string id, int code, string message, object data = null)
        {
            var response = new JsonRPCErrorResponse
            {
                Id = id,
                Error = new JsonRPCError
                {
                    Code = code,
                    Message = message,
                    Data = data != null ? JToken.FromObject(data) : null
                }
            };

            return response.ToJson();
        }

        private static string CreatePingResponse(string id)
        {
            var response = new JsonRPCSuccessResponse
            {
                Id = id,
                Result = new JObject
                {
                    ["ok"] = true,
                    ["ts"] = DateTime.UtcNow.ToString("o")
                }
            };
            return response.ToJson();
        }
    }
}
