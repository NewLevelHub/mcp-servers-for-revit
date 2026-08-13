import http from "node:http";
import { loadConfig } from "./config.js";
import { AgentSessionManager, type ChatImage, type ChatRequest } from "./agent-session.js";

function writeSse(res: http.ServerResponse, event: string, data: unknown): void {
  res.write(`event: ${event}\n`);
  res.write(`data: ${JSON.stringify(data)}\n\n`);
}

function readBody(req: http.IncomingMessage): Promise<string> {
  return new Promise((resolve, reject) => {
    const chunks: Buffer[] = [];
    req.on("data", (c) => chunks.push(Buffer.isBuffer(c) ? c : Buffer.from(c)));
    req.on("end", () => resolve(Buffer.concat(chunks).toString("utf8")));
    req.on("error", reject);
  });
}

function sendJson(res: http.ServerResponse, status: number, payload: unknown): void {
  res.writeHead(status, { "Content-Type": "application/json" });
  res.end(JSON.stringify(payload));
}

async function main(): Promise<void> {
  const config = loadConfig();
  const sessions = new AgentSessionManager(config);

  /** The port is local but open to every process on the box; gate it on a per-launch token. */
  const authorized = (req: http.IncomingMessage): boolean => {
    if (!config.token) return true;
    const header = req.headers["x-bridge-token"];
    const value = Array.isArray(header) ? header[0] : header;
    return value === config.token;
  };

  const server = http.createServer(async (req, res) => {
    if (req.method === "GET" && req.url === "/health") {
      sendJson(res, 200, { ok: true, model: config.modelLabel });
      return;
    }

    if (!authorized(req)) {
      sendJson(res, 401, { error: "unauthorized" });
      return;
    }

    if (req.method === "POST" && req.url === "/v1/cancel") {
      let sessionId = "default";
      try {
        sessionId = (JSON.parse(await readBody(req)) as { sessionId?: string }).sessionId ?? "default";
      } catch {
        /* keep default */
      }
      const cancelled = await sessions.cancel(sessionId);
      sendJson(res, 200, { cancelled });
      return;
    }

    if (req.method === "POST" && req.url === "/v1/confirm") {
      let body: { sessionId?: string; requestId?: string; approved?: boolean };
      try {
        body = JSON.parse(await readBody(req));
      } catch {
        sendJson(res, 400, { error: "invalid_json" });
        return;
      }
      const accepted = sessions.resolveConfirmation(
        body.sessionId ?? "default",
        body.requestId ?? "",
        body.approved === true,
      );
      sendJson(res, accepted ? 200 : 404, { accepted });
      return;
    }

    if (req.method !== "POST" || req.url !== "/v1/chat") {
      sendJson(res, 404, { error: "not_found" });
      return;
    }

    let body: ChatRequest;
    try {
      body = JSON.parse(await readBody(req)) as ChatRequest;
    } catch {
      sendJson(res, 400, { error: "invalid_json" });
      return;
    }

    res.writeHead(200, {
      "Content-Type": "text/event-stream; charset=utf-8",
      "Cache-Control": "no-cache",
      Connection: "keep-alive",
    });

    try {
      await sessions.handleChat(body, {
        status: (text) => writeSse(res, "status", { text }),
        textDelta: (text) => writeSse(res, "text_delta", { text }),
        toolStep: (step) => writeSse(res, "tool_step", step),
        confirm: (payload) => writeSse(res, "confirm", payload),
        done: (payload) => writeSse(res, "done", payload),
        error: (message) => writeSse(res, "error", { message }),
      });
    } catch (err) {
      const message = err instanceof Error ? err.message : String(err);
      writeSse(res, "error", { message });
    }

    res.end();
  });

  server.listen(config.port, "127.0.0.1", () => {
    // eslint-disable-next-line no-console
    console.log(
      `[assistant-bridge] listening on http://127.0.0.1:${config.port} model=${config.modelLabel}`,
    );
  });

  const shutdown = async () => {
    await sessions.disposeAll();
    server.close();
    process.exit(0);
  };

  process.on("SIGINT", shutdown);
  process.on("SIGTERM", shutdown);
}

main().catch((err) => {
  // eslint-disable-next-line no-console
  console.error("[assistant-bridge] fatal:", err);
  process.exit(1);
});

export type { ChatImage, ChatRequest };
