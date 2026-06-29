import * as fs from "fs";
import * as os from "os";
import * as path from "path";
import { RevitClientConnection } from "./SocketClient.js";

// Mutex to serialize all Revit connections - prevents race conditions
// when multiple requests are made in parallel
let connectionMutex: Promise<void> = Promise.resolve();

const LOG_DIR = path.join(os.homedir(), ".mcp-servers-for-revit", "logs");

interface CommandMetrics {
  event: "command";
  timestamp: string;
  command: string;
  durationMs: number;
  success: boolean;
  responseSize: number;
  error?: string;
}

function ensureLogDir(): void {
  if (!fs.existsSync(LOG_DIR)) {
    fs.mkdirSync(LOG_DIR, { recursive: true });
  }
}

function getMetricsLogPath(): string {
  const dateStamp = new Date().toISOString().slice(0, 10).replace(/-/g, "");
  return path.join(LOG_DIR, `command-metrics_${dateStamp}.jsonl`);
}

export function getCommandMetricsLogPath(): string {
  ensureLogDir();
  return getMetricsLogPath();
}

function logCommandMetrics(metrics: CommandMetrics): void {
  const line = JSON.stringify(metrics);
  console.error(`[METRICS] ${line}`);

  try {
    ensureLogDir();
    fs.appendFileSync(getMetricsLogPath(), line + "\n", "utf8");
  } catch (error) {
    console.error("Failed to write command metrics log:", error);
  }
}

function wrapSendCommand(client: RevitClientConnection): void {
  const originalSendCommand = client.sendCommand.bind(client);

  client.sendCommand = async (command: string, params: any = {}) => {
    const start = Date.now();
    let responseSize = 0;

    try {
      const result = await originalSendCommand(command, params);
      responseSize = Buffer.byteLength(JSON.stringify(result ?? null), "utf8");
      logCommandMetrics({
        event: "command",
        timestamp: new Date().toISOString(),
        command,
        durationMs: Date.now() - start,
        success: true,
        responseSize,
      });
      return result;
    } catch (error) {
      const message =
        error instanceof Error
          ? `${error.message}${error.stack ? `\n${error.stack}` : ""}`
          : String(error);
      logCommandMetrics({
        event: "command",
        timestamp: new Date().toISOString(),
        command,
        durationMs: Date.now() - start,
        success: false,
        responseSize,
        error: message,
      });
      throw error;
    }
  };
}

/**
 * Connect to the Revit client and execute an operation.
 */
export async function withRevitConnection<T>(
  operation: (client: RevitClientConnection) => Promise<T>
): Promise<T> {
  const previousMutex = connectionMutex;
  let releaseMutex: () => void;
  connectionMutex = new Promise<void>((resolve) => {
    releaseMutex = resolve;
  });
  await previousMutex;

  const revitClient = new RevitClientConnection("localhost", 8080);
  wrapSendCommand(revitClient);

  try {
    if (!revitClient.isConnected) {
      await new Promise<void>((resolve, reject) => {
        const onConnect = () => {
          revitClient.socket.removeListener("connect", onConnect);
          revitClient.socket.removeListener("error", onError);
          resolve();
        };

        const onError = () => {
          revitClient.socket.removeListener("connect", onConnect);
          revitClient.socket.removeListener("error", onError);
          reject(new Error("connect to revit client failed"));
        };

        revitClient.socket.on("connect", onConnect);
        revitClient.socket.on("error", onError);

        revitClient.connect();

        setTimeout(() => {
          revitClient.socket.removeListener("connect", onConnect);
          revitClient.socket.removeListener("error", onError);
          reject(new Error("连接到Revit客户端失败"));
        }, 5000);
      });
    }

    return await operation(revitClient);
  } finally {
    revitClient.disconnect();
    releaseMutex!();
  }
}
