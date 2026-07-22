import * as net from "net";

const CONNECT_TIMEOUT_MS = 5000;
const COMMAND_TIMEOUT_MS = 120000;

// Commands whose plugin-side wait is 120–180 s. The socket timeout must outlast the
// plugin's own timeout so its TimeoutException reaches the chat instead of a blind
// socket cut-off (observed on large models, e.g. 572-room «Короткий блок»).
const HEAVY_COMMAND_TIMEOUT_MS = 210000;
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

export class RevitClientConnection {
  host: string;
  port: number;
  socket: net.Socket;
  isConnected: boolean = false;
  private intentionallyClosed: boolean = false;
  private connectPromise: Promise<void> | null = null;
  private disconnectCallbacks: Array<() => void> = [];
  responseCallbacks: Map<string, (response: string) => void> = new Map();
  private byteBuffer: Buffer = Buffer.alloc(0);

  /** Max framed JSON payload (50 MB) — guards against corrupt length headers. */
  private static readonly MAX_FRAME_BYTES = 50 * 1024 * 1024;

  constructor(host: string, port: number) {
    this.host = host;
    this.port = port;
    this.socket = this.createSocket();
  }

  private createSocket(): net.Socket {
    const socket = new net.Socket();
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
      this.handleDisconnect();
    });

    socket.on("error", (error) => {
      console.error("RevitClientConnection error:", error);
      this.handleDisconnect();
    });
  }

  public onDisconnect(callback: () => void): void {
    this.disconnectCallbacks.push(callback);
  }

  private notifyDisconnect(): void {
    for (const callback of this.disconnectCallbacks) {
      callback();
    }
  }

  private handleDisconnect(): void {
    this.notifyDisconnect();
    this.isConnected = false;
    this.connectPromise = null;
    this.byteBuffer = Buffer.alloc(0);

    for (const [requestId, callback] of this.responseCallbacks) {
      callback(
        JSON.stringify({
          jsonrpc: "2.0",
          id: requestId,
          error: {
            code: -32000,
            message: "Connection to Revit lost",
          },
        })
      );
    }
    this.responseCallbacks.clear();
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
    if (this.isConnected) {
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
        resolve();
      };

      const onError = (error: Error) => {
        cleanup();
        this.isConnected = false;
        this.connectPromise = null;
        reject(
          new Error(`Failed to connect to Revit client: ${error.message}`)
        );
      };

      const onTimeout = () => {
        cleanup();
        this.isConnected = false;
        this.connectPromise = null;
        this.socket.destroy();
        reject(new Error("连接到Revit客户端失败"));
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
        reject(error);
        return;
      }

      const timeoutId = setTimeout(onTimeout, CONNECT_TIMEOUT_MS);
    });

    return this.connectPromise;
  }

  public async ensureConnected(): Promise<void> {
    if (this.intentionallyClosed) {
      throw new Error("Revit connection was closed");
    }

    if (this.isConnected && !this.socket.destroyed) {
      return;
    }

    if (this.socket.destroyed) {
      this.resetSocket();
    }

    await this.connect();
  }

  public disconnect(): void {
    this.intentionallyClosed = true;
    this.connectPromise = null;
    this.isConnected = false;
    this.byteBuffer = Buffer.alloc(0);

    for (const [requestId, callback] of this.responseCallbacks) {
      callback(
        JSON.stringify({
          jsonrpc: "2.0",
          id: requestId,
          error: {
            code: -32000,
            message: "Connection to Revit closed",
          },
        })
      );
    }
    this.responseCallbacks.clear();

    if (!this.socket.destroyed) {
      this.socket.end();
      this.socket.destroy();
    }
  }

  private processByteBuffer(): void {
    while (this.byteBuffer.length >= 4) {
      const frameLength = this.byteBuffer.readUInt32BE(0);
      if (frameLength <= 0 || frameLength > RevitClientConnection.MAX_FRAME_BYTES) {
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

  public async sendCommand(command: string, params: any = {}): Promise<any> {
    await this.ensureConnected();

    return new Promise((resolve, reject) => {
      try {
        const requestId = this.generateRequestId();

        const commandObj = {
          jsonrpc: "2.0",
          method: command,
          params: params,
          id: requestId,
        };

        const timeoutMs = HEAVY_COMMANDS.has(command)
          ? HEAVY_COMMAND_TIMEOUT_MS
          : COMMAND_TIMEOUT_MS;
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
              reject(
                new Error(response.error.message || "Unknown error from Revit")
              );
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

        const commandString = JSON.stringify(commandObj);
        this.writeFramedMessage(commandString);

        timeoutHandle = setTimeout(() => {
          if (this.responseCallbacks.has(requestId)) {
            this.responseCallbacks.delete(requestId);
            reject(
              new Error(
                `Command timed out after ${timeoutMs / 1000} s: ${command}`
              )
            );
          }
        }, timeoutMs);
      } catch (error) {
        reject(error);
      }
    });
  }
}
