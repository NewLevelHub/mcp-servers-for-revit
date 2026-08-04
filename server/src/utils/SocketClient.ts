import * as net from "net";

export const CONNECT_TIMEOUT_MS = 5000;
export const COMMAND_TIMEOUT_MS = 120000;
export const HEARTBEAT_INTERVAL_MS = 10_000;
export const HEARTBEAT_TIMEOUT_MS = 5_000;
export const RECONNECT_BASE_MS = 500;
export const RECONNECT_MAX_MS = 15_000;

// Commands whose plugin-side wait is 120–180 s. The socket timeout must outlast the
// plugin's own timeout so its TimeoutException reaches the chat instead of a blind
// socket cut-off (observed on large models, e.g. 572-room «Короткий блок»).
export const HEAVY_COMMAND_TIMEOUT_MS = 210000;
const HEAVY_COMMANDS = new Set([
  "analyze_model_statistics",
  "export_room_finish_data",
  "export_apartment_data",
  "export_egress_graph",
  "create_door_schedule",
  "create_window_schedule",
  "create_floor_schedule",
  "create_curtain_wall_schedule",
  "get_material_quantities",
  "validate_schedule",
  "get_schedule_definition",
  "create_schedule",
  "auto_layout_sheet",
  "render_tep_table",
  "create_floor_explication",
  "check_min_dimensions",
  "check_fire_doors",
  // A full batch (20 sub-commands, e.g. fill_title_block writes) can exceed 120 s.
  "batch_execute",
]);

export type ConnectionStatus = "connected" | "reconnecting" | "offline";

export type RevitErrorKind =
  | "server_offline"
  | "revit_unresponsive"
  | "command_timeout"
  | "closed";

export class RevitConnectionError extends Error {
  readonly kind: RevitErrorKind;

  constructor(kind: RevitErrorKind, message: string) {
    super(message);
    this.name = "RevitConnectionError";
    this.kind = kind;
  }
}

export interface ReconnectEvent {
  event: "reconnect";
  timestamp: string;
  attempt: number;
  success: boolean;
  delayMs: number;
  error?: string;
}

export function computeReconnectDelayMs(attempt: number): number {
  const exp = Math.max(0, attempt);
  return Math.min(RECONNECT_MAX_MS, RECONNECT_BASE_MS * Math.pow(2, exp));
}

export function formatRevitError(
  kind: RevitErrorKind,
  detail?: string
): string {
  switch (kind) {
    case "server_offline":
      return (
        "Сервер MCP в Revit выключен. На вкладке mcp-servers-for-revit нажмите " +
        "Open Server и повторите запрос." +
        (detail ? ` (${detail})` : "")
      );
    case "revit_unresponsive":
      return (
        "Связь с Revit потеряна (нет ответа на heartbeat или обрыв сокета). " +
        "Идёт переподключение…" +
        (detail ? ` (${detail})` : "")
      );
    case "command_timeout":
      return detail ?? "Команда превысила лимит ожидания";
    case "closed":
      return "Соединение с Revit закрыто";
  }
}

export class RevitClientConnection {
  host: string;
  port: number;
  socket: net.Socket;
  isConnected: boolean = false;
  private intentionallyClosed: boolean = false;
  private connectPromise: Promise<void> | null = null;
  private disconnectCallbacks: Array<() => void> = [];
  private statusCallbacks: Array<(status: ConnectionStatus) => void> = [];
  private reconnectCallbacks: Array<(event: ReconnectEvent) => void> = [];
  private status: ConnectionStatus = "offline";
  private reconnectAttempt = 0;
  private heartbeatTimer: ReturnType<typeof setInterval> | null = null;
  private reconnectTimer: ReturnType<typeof setTimeout> | null = null;
  private heartbeatInFlight = false;
  responseCallbacks: Map<string, (response: string) => void> = new Map();
  private byteBuffer: Buffer = Buffer.alloc(0);

  /** Max framed JSON payload (50 MB) — guards against corrupt length headers. */
  private static readonly MAX_FRAME_BYTES = 50 * 1024 * 1024;

  constructor(host: string, port: number) {
    this.host = host;
    this.port = port;
    this.socket = this.createSocket();
  }

  getConnectionStatus(): ConnectionStatus {
    return this.status;
  }

  private createSocket(): net.Socket {
    const socket = new net.Socket();
    socket.setKeepAlive(true, HEARTBEAT_INTERVAL_MS);
    this.setupSocketListeners(socket);
    return socket;
  }

  private setupSocketListeners(socket: net.Socket): void {
    socket.on("connect", () => {
      this.isConnected = true;
    });

    socket.on("data", (data) => {
      this.byteBuffer = Buffer.concat([this.byteBuffer, data]);
      this.processByteBuffer();
    });

    socket.on("close", () => {
      this.handleDisconnect("socket close");
    });

    socket.on("error", (error) => {
      console.error("RevitClientConnection error:", error);
      this.handleDisconnect(error.message);
    });
  }

  public onDisconnect(callback: () => void): void {
    this.disconnectCallbacks.push(callback);
  }

  public onStatusChange(callback: (status: ConnectionStatus) => void): void {
    this.statusCallbacks.push(callback);
  }

  public onReconnect(callback: (event: ReconnectEvent) => void): void {
    this.reconnectCallbacks.push(callback);
  }

  private setStatus(status: ConnectionStatus): void {
    if (this.status === status) return;
    this.status = status;
    for (const callback of this.statusCallbacks) {
      try {
        callback(status);
      } catch (error) {
        console.error("Status callback failed:", error);
      }
    }
  }

  private notifyDisconnect(): void {
    for (const callback of this.disconnectCallbacks) {
      callback();
    }
  }

  private notifyReconnect(event: ReconnectEvent): void {
    for (const callback of this.reconnectCallbacks) {
      try {
        callback(event);
      } catch (error) {
        console.error("Reconnect callback failed:", error);
      }
    }
  }

  private failPendingRequests(kind: RevitErrorKind, detail?: string): void {
    const message = formatRevitError(kind, detail);
    for (const [requestId, callback] of this.responseCallbacks) {
      callback(
        JSON.stringify({
          jsonrpc: "2.0",
          id: requestId,
          error: {
            code: -32000,
            message,
            data: { kind },
          },
        })
      );
    }
    this.responseCallbacks.clear();
  }

  private handleDisconnect(reason: string): void {
    const wasConnected = this.isConnected || this.status === "connected";
    this.isConnected = false;
    this.connectPromise = null;
    this.byteBuffer = Buffer.alloc(0);
    this.stopHeartbeat();

    if (this.intentionallyClosed) {
      this.failPendingRequests("closed");
      this.setStatus("offline");
      this.notifyDisconnect();
      return;
    }

    if (wasConnected || this.responseCallbacks.size > 0) {
      this.failPendingRequests("revit_unresponsive", reason);
      this.notifyDisconnect();
    }

    this.scheduleReconnect();
  }

  private stopHeartbeat(): void {
    if (this.heartbeatTimer !== null) {
      clearInterval(this.heartbeatTimer);
      this.heartbeatTimer = null;
    }
    this.heartbeatInFlight = false;
  }

  private startHeartbeat(): void {
    this.stopHeartbeat();
    this.heartbeatTimer = setInterval(() => {
      void this.heartbeatTick();
    }, HEARTBEAT_INTERVAL_MS);
    // Do not keep the Node process alive solely for heartbeat (tests / idle).
    this.heartbeatTimer.unref?.();
  }

  private async heartbeatTick(): Promise<void> {
    if (
      this.intentionallyClosed ||
      !this.isConnected ||
      this.socket.destroyed ||
      this.heartbeatInFlight
    ) {
      return;
    }

    // Skip while a real command is in flight — the channel is clearly alive,
    // and the plugin handles one framed request at a time on this socket.
    if (this.responseCallbacks.size > 0) {
      return;
    }

    this.heartbeatInFlight = true;
    try {
      await this.sendFramedRequest("ping", {}, HEARTBEAT_TIMEOUT_MS);
    } catch (error) {
      const detail =
        error instanceof Error ? error.message : String(error);
      // Legacy plugin without ping still proves the TCP channel is alive.
      if (/Method not found|не найден|未找到方法/i.test(detail)) {
        return;
      }
      console.error(`Heartbeat failed: ${detail}`);
      // Destroying the socket fires "close" → handleDisconnect → backoff reconnect.
      // Do not destroy on intentional shutdown.
      if (!this.intentionallyClosed && !this.socket.destroyed) {
        this.socket.destroy();
      }
    } finally {
      this.heartbeatInFlight = false;
    }
  }

  private clearReconnectTimer(): void {
    if (this.reconnectTimer !== null) {
      clearTimeout(this.reconnectTimer);
      this.reconnectTimer = null;
    }
  }

  private scheduleReconnect(): void {
    if (this.intentionallyClosed) {
      this.setStatus("offline");
      return;
    }

    if (this.reconnectTimer !== null) {
      return;
    }

    this.setStatus("reconnecting");
    const attempt = this.reconnectAttempt;
    const delayMs = computeReconnectDelayMs(attempt);
    this.reconnectAttempt += 1;

    this.reconnectTimer = setTimeout(() => {
      this.reconnectTimer = null;
      void this.attemptReconnect(attempt, delayMs);
    }, delayMs);
    this.reconnectTimer.unref?.();
  }

  private async attemptReconnect(
    attempt: number,
    delayMs: number
  ): Promise<void> {
    if (this.intentionallyClosed) {
      this.setStatus("offline");
      return;
    }

    try {
      this.resetSocket();
      await this.connect();
      this.reconnectAttempt = 0;
      this.setStatus("connected");
      this.startHeartbeat();
      this.notifyReconnect({
        event: "reconnect",
        timestamp: new Date().toISOString(),
        attempt: attempt + 1,
        success: true,
        delayMs,
      });
    } catch (error) {
      const message =
        error instanceof Error ? error.message : String(error);
      this.notifyReconnect({
        event: "reconnect",
        timestamp: new Date().toISOString(),
        attempt: attempt + 1,
        success: false,
        delayMs,
        error: message,
      });
      this.scheduleReconnect();
    }
  }

  private resetSocket(): void {
    this.socket.removeAllListeners();
    if (!this.socket.destroyed) {
      this.socket.destroy();
    }
    this.socket = this.createSocket();
    this.isConnected = false;
    this.connectPromise = null;
    this.byteBuffer = Buffer.alloc(0);
  }

  public connect(): Promise<void> {
    if (this.isConnected && !this.socket.destroyed) {
      return Promise.resolve();
    }

    if (this.connectPromise) {
      return this.connectPromise;
    }

    if (this.socket.destroyed) {
      this.resetSocket();
    }

    this.connectPromise = new Promise<void>((resolve, reject) => {
      const onConnect = () => {
        cleanup();
        this.isConnected = true;
        this.intentionallyClosed = false;
        resolve();
      };

      const onError = (error: Error) => {
        cleanup();
        this.isConnected = false;
        this.connectPromise = null;
        reject(
          new RevitConnectionError(
            "server_offline",
            formatRevitError("server_offline", error.message)
          )
        );
      };

      const onTimeout = () => {
        cleanup();
        this.isConnected = false;
        this.connectPromise = null;
        this.socket.destroy();
        reject(
          new RevitConnectionError(
            "server_offline",
            formatRevitError(
              "server_offline",
              `timeout ${CONNECT_TIMEOUT_MS} ms`
            )
          )
        );
      };

      const cleanup = () => {
        clearTimeout(timeoutId);
        this.socket.removeListener("connect", onConnect);
        this.socket.removeListener("error", onError);
      };

      this.socket.once("connect", onConnect);
      this.socket.once("error", onError);

      try {
        this.socket.connect(this.port, this.host);
      } catch (error) {
        cleanup();
        this.connectPromise = null;
        reject(
          new RevitConnectionError(
            "server_offline",
            formatRevitError(
              "server_offline",
              error instanceof Error ? error.message : String(error)
            )
          )
        );
        return;
      }

      const timeoutId = setTimeout(onTimeout, CONNECT_TIMEOUT_MS);
    });

    return this.connectPromise;
  }

  public async ensureConnected(): Promise<void> {
    if (this.intentionallyClosed) {
      throw new RevitConnectionError(
        "closed",
        formatRevitError("closed")
      );
    }

    if (this.isConnected && !this.socket.destroyed) {
      return;
    }

    this.clearReconnectTimer();

    if (this.socket.destroyed) {
      this.resetSocket();
    }

    try {
      await this.connect();
      this.reconnectAttempt = 0;
      this.setStatus("connected");
      this.startHeartbeat();
    } catch (error) {
      this.setStatus("reconnecting");
      this.scheduleReconnect();
      throw error;
    }
  }

  public disconnect(): void {
    this.intentionallyClosed = true;
    this.clearReconnectTimer();
    this.stopHeartbeat();
    this.connectPromise = null;
    this.isConnected = false;
    this.byteBuffer = Buffer.alloc(0);
    this.failPendingRequests("closed");
    this.setStatus("offline");

    if (!this.socket.destroyed) {
      this.socket.end();
      this.socket.destroy();
    }
  }

  private processByteBuffer(): void {
    while (this.byteBuffer.length >= 4) {
      const frameLength = this.byteBuffer.readUInt32BE(0);
      if (
        frameLength <= 0 ||
        frameLength > RevitClientConnection.MAX_FRAME_BYTES
      ) {
        console.error(
          `Invalid TCP frame length ${frameLength}; clearing buffer`
        );
        this.byteBuffer = Buffer.alloc(0);
        return;
      }
      const totalLength = 4 + frameLength;
      if (this.byteBuffer.length < totalLength) {
        return;
      }
      const json = this.byteBuffer.subarray(4, totalLength).toString("utf8");
      this.byteBuffer = this.byteBuffer.subarray(totalLength);
      this.handleResponse(json);
    }
  }

  private writeFramedMessage(payload: string): void {
    const body = Buffer.from(payload, "utf8");
    const header = Buffer.alloc(4);
    header.writeUInt32BE(body.length, 0);
    this.socket.write(Buffer.concat([header, body]));
  }

  private generateRequestId(): string {
    return Date.now().toString() + Math.random().toString().substring(2, 8);
  }

  private handleResponse(responseData: string): void {
    try {
      const response = JSON.parse(responseData);
      const requestId = response.id || "default";

      const callback = this.responseCallbacks.get(requestId);
      if (callback) {
        callback(responseData);
        this.responseCallbacks.delete(requestId);
      }
    } catch (error) {
      console.error("Error parsing response:", error);
    }
  }

  private sendFramedRequest(
    command: string,
    params: any,
    timeoutMs: number
  ): Promise<any> {
    return new Promise((resolve, reject) => {
      try {
        const requestId = this.generateRequestId();

        const commandObj = {
          jsonrpc: "2.0",
          method: command,
          params: params,
          id: requestId,
        };

        let timeoutHandle: ReturnType<typeof setTimeout> | null = null;

        const clearCommandTimeout = () => {
          if (timeoutHandle !== null) {
            clearTimeout(timeoutHandle);
            timeoutHandle = null;
          }
        };

        this.responseCallbacks.set(requestId, (responseData) => {
          clearCommandTimeout();
          try {
            const response = JSON.parse(responseData);
            if (response.error) {
              const kind =
                (response.error.data &&
                  response.error.data.kind) as RevitErrorKind | undefined;
              if (kind) {
                reject(
                  new RevitConnectionError(
                    kind,
                    response.error.message || formatRevitError(kind)
                  )
                );
              } else {
                reject(
                  new Error(
                    response.error.message || "Unknown error from Revit"
                  )
                );
              }
            } else {
              resolve(response.result);
            }
          } catch (error) {
            if (error instanceof Error) {
              reject(new Error(`Failed to parse response: ${error.message}`));
            } else {
              reject(new Error(`Failed to parse response: ${String(error)}`));
            }
          }
        });

        this.writeFramedMessage(JSON.stringify(commandObj));

        // Command timeout rejects the Promise only — it must NOT destroy the
        // persistent channel (REV-139 AC).
        timeoutHandle = setTimeout(() => {
          if (this.responseCallbacks.has(requestId)) {
            this.responseCallbacks.delete(requestId);
            reject(
              new RevitConnectionError(
                "command_timeout",
                formatRevitError(
                  "command_timeout",
                  `Команда превысила ${timeoutMs / 1000} с: ${command}`
                )
              )
            );
          }
        }, timeoutMs);
      } catch (error) {
        reject(error);
      }
    });
  }

  public async sendCommand(command: string, params: any = {}): Promise<any> {
    await this.ensureConnected();

    const timeoutMs = HEAVY_COMMANDS.has(command)
      ? HEAVY_COMMAND_TIMEOUT_MS
      : COMMAND_TIMEOUT_MS;

    return this.sendFramedRequest(command, params, timeoutMs);
  }
}
