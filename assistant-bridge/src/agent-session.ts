import fs from "node:fs";
import { Agent, JsonlLocalAgentStore } from "@cursor/sdk";
import type { LocalAgentStore, ModelSelection, Run, SDKCustomTool } from "@cursor/sdk";
import type { BridgeConfig } from "./config.js";
import { AUTO_MODEL_ID, modelLabel } from "./config.js";

export type ChatImage = {
  mimeType: string;
  dataBase64: string;
};

export type ChatRequest = {
  sessionId: string;
  message: string;
  images?: ChatImage[];
  reset?: boolean;
};

export type SseEmitter = {
  status(text: string): void;
  textDelta(text: string): void;
  toolStep(step: ToolStepPayload): void;
  confirm(payload: {
    requestId: string;
    action: string;
    details: string;
    tool: string;
    elementIds: string[];
  }): void;
  done(payload: { reply: string; model: string; doneSummary: string[] }): void;
  error(message: string): void;
};

export type ToolStepPayload = {
  callId: string;
  name: string;
  status: "running" | "ok" | "error";
  args?: string;
  result?: string;
};

type SdkAgent = Awaited<ReturnType<typeof Agent.create>>;

type SessionRecord = {
  agentId: string;
  agent: SdkAgent;
  /** Run in flight, so /v1/cancel can stop the agent server-side. */
  activeRun?: Run;
  /** Emitter of the request currently streaming this session. */
  emitter?: SseEmitter;
  /** Confirmations awaiting an answer from the Revit panel, by requestId. */
  pendingConfirms: Map<string, (approved: boolean) => void>;
};

/** Generic wrappers Cursor reports instead of the real MCP tool name. */
const GENERIC_TOOL_NAMES = new Set(["mcp", "CallMcpTool", "call_mcp_tool"]);

const CONFIRM_TIMEOUT_MS = 5 * 60 * 1000;

const MAX_JOURNAL_CHARS = 2000;

const REVIT_SYSTEM_PREFIX =
  "Ты AI-ассистент проектировщика внутри Autodesk Revit. Работай ТОЛЬКО через MCP-tools " +
  "mcp-server-for-revit-local. Следуй правилам из .cursor/rules проекта (revit-mcp, размеры, DWG, нормы). " +
  "Координаты и размеры — в мм. Перед create_* проверь активный вид через get_current_view_info.\n" +
  "ОБЯЗАТЕЛЬНО: перед необратимым действием (delete_element, send_code_to_revit, " +
  "изменение более 20 элементов разом) сначала вызови инструмент revit_confirm — передай action, " +
  "tool (имя MCP-инструмента) и elementIds, если действие касается конкретных элементов. " +
  "Если он вернул approved=false — не выполняй действие и объясни это пользователю.\n" +
  // The reader is an architect looking at the model, not an engineer reading a log.
  "КТО ЧИТАЕТ ОТВЕТ: архитектор-проектировщик. Он не знает и не должен знать, какими " +
  "инструментами ты пользуешься.\n" +
  "ФОРМАТ ОТВЕТА (важнее правил оформления из .cursor/rules):\n" +
  "1. Пиши по-русски, коротко, языком проектировщика — обычно 1–5 предложений или короткий список.\n" +
  "2. Никаких заголовков (#, ##), горизонтальных линий и таблиц.\n" +
  "3. ЗАПРЕЩЕНО упоминать в ответе имена инструментов (get_current_view_info, trace_walls_from_cad, " +
  "MCP и т.п.) и технический жаргон (dry-run, host, strict location, fallback, ElementId). " +
  "Названия слоёв DWG и типов Revit — можно, это язык проектировщика.\n" +
  "4. НЕ пиши таблицу «Использованные MCP-функции» и не пересказывай правила, которым следуешь: " +
  "выполненные шаги пользователь видит в панели.\n" +
  "5. ЧИСЛА ПРОВЕРЯЙ ПО МОДЕЛИ. Прежде чем назвать, сколько элементов создано, посмотри " +
  "фактическое количество в модели (get_current_view_elements по нужной категории). " +
  "Не складывай свои промежуточные результаты — при повторных проходах это даёт неверные цифры.\n" +
  "5а. НЕ ПУТАЙ ОХВАТ ЧИСЛА. У каждой цифры есть область: весь проект, уровень или текущий вид. " +
  "Счётчики из analyze_model_statistics — это ВЕСЬ ПРОЕКТ, а не уровень; elementCount уровня " +
  "не раскладывается по этим категориям (оси, листы и типы к уровню вообще не привязаны). " +
  "Не подавай проектные числа как «на этом уровне» — либо назови охват словами " +
  "(«в проекте», «на этом плане»), либо посчитай нужный охват отдельно.\n" +
  "5б. ГОВОРИ ТОЧНОЕ ЧИСЛО, а не «сотни», «много», «большая библиотека». Если инструмент " +
  "вернул список — назови его длину. Если список длинный, дай число и 5–7 характерных примеров.\n" +
  "6. Если работал по DWG — обязательно скажи одной строкой, что осталось неперенесённым " +
  "(оси, лестницы, ограждения, перекрытия, балки и т.п.), чтобы пользователь знал объём остатка.\n" +
  "7. Если что-то не получилось — объясни причину простыми словами и что с этим делать.\n" +
  // Every extra call is latency the architect watches in the steps panel, and this
  // model is 5000+ elements — a stray "read the whole view" costs real seconds.
  "\nЭКОНОМЬ ШАГИ. Каждый вызов — это ожидание для пользователя. Бери минимум данных, " +
  "которого хватает для ответа:\n" +
  "— вопрос про выделенное — get_selected_elements, затем get_element_parameters по этим id; " +
  "НЕ читай весь вид и НЕ анализируй модель;\n" +
  "— вопрос про доступные типы и семейства — get_available_family_types с фильтром категории; " +
  "НЕ анализируй модель и НЕ перебирай размещённые элементы;\n" +
  "— вопрос «сколько элементов в этом виде» — get_current_view_elements по нужным категориям, " +
  "по одному вызову на категорию, без повторов;\n" +
  "— analyze_model_statistics вызывай только когда спросили про модель ЦЕЛИКОМ;\n" +
  "— не вызывай один и тот же инструмент с теми же аргументами дважды: результат уже у тебя.\n" +
  "Перед каждым вызовом спроси себя, изменит ли он ответ. Если нет — не вызывай.\n" +
  // The architect watched the agent build a sheet, delete it, and build it again —
  // seven elements gone per pass and a minute of waiting for nothing.
  "— НЕ ПЕРЕДЕЛЫВАЙ через удаление. Если лист/спека/вид получились не так, поправь на месте " +
  "(переместить — place_view_on_sheet или auto_layout_sheet, сузить — fit_schedule_to_sheet, " +
  "формат — параметр листа). delete_element + повторное создание — только если пользователь " +
  "прямо просит удалить. Удаление листа уносит с собой видовые экраны и штамп.\n" +
  "— ЧИТАЙ warnings из ответа инструмента. Там написано, что не влезло и что делать; " +
  "повторять тот же вызов с другими координатами бессмысленно — размещение само держит " +
  "содержимое внутри рамки и в стороне от штампа.\n\n" +
  "ЛИСТЫ:\n" +
  "— рабочий лист = основная надпись (ADSK_ОсновнаяНадпись). ADSK_Титул — это титульный лист " +
  "проекта, на него ничего не размещают.\n" +
  "— create_sheet без имени семейства сам берёт основную надпись; размер задавай " +
  "sheetFormat:\"A3\"/\"A2\", а не именем типа.\n" +
  "— спецификацию на лист ставят одним place_view_on_sheet (например x=20, y=70); " +
  "несколько таблиц на листе раскладывает auto_layout_sheet.\n\n";

function truncate(value: unknown, limit = MAX_JOURNAL_CHARS): string | undefined {
  if (value === undefined || value === null) return undefined;
  const text = typeof value === "string" ? value : JSON.stringify(value);
  if (!text) return undefined;
  return text.length > limit ? text.slice(0, limit) + "…" : text;
}

/**
 * Cursor reports MCP calls under a wrapper name; the real tool sits in the args.
 * Falls back to the reported name when the shape is unfamiliar.
 */
function resolveToolName(reported: string | undefined, args: unknown): string {
  const name = reported ?? "tool";
  if (!GENERIC_TOOL_NAMES.has(name)) return name;

  if (args && typeof args === "object") {
    const bag = args as Record<string, unknown>;
    for (const key of ["toolName", "tool_name", "name", "tool"]) {
      const candidate = bag[key];
      if (typeof candidate === "string" && candidate.trim()) return candidate.trim();
    }
  }
  return name;
}

export class AgentSessionManager {
  private readonly config: BridgeConfig;
  private readonly sessions = new Map<string, SessionRecord>();
  private readonly store: LocalAgentStore;

  constructor(config: BridgeConfig) {
    this.config = config;
    fs.mkdirSync(config.storeDir, { recursive: true });
    // Flat JSONL store on a short path — see resolveStoreDir in config.ts.
    this.store = new JsonlLocalAgentStore(config.storeDir);
  }

  private buildMcpServers(): Record<string, import("@cursor/sdk").McpServerConfig> {
    return {
      "mcp-server-for-revit-local": {
        type: "stdio",
        command: this.config.mcpNode,
        args: [this.config.mcpServerJs],
        cwd: this.config.mcpServerCwd,
        env: {
          MCP_TOOL_PROFILE: process.env.MCP_TOOL_PROFILE ?? "default",
        },
      },
    };
  }

  /**
   * In-process tool the agent must call before irreversible edits. Routes the
   * question to the Revit chat panel over SSE and blocks until it answers.
   */
  private buildCustomTools(sessionId: string): Record<string, SDKCustomTool> {
    return {
      revit_confirm: {
        description:
          "Спросить подтверждение у пользователя перед необратимым действием в Revit " +
          "(удаление элементов, send_code_to_revit, массовое изменение). " +
          "Возвращает { approved: boolean }. При approved=false действие выполнять нельзя.",
        inputSchema: {
          type: "object",
          properties: {
            action: { type: "string", description: "Что будет сделано, одной строкой" },
            details: { type: "string", description: "Детали: элементы, количество, вид" },
            tool: {
              type: "string",
              description: "MCP-инструмент, который будет вызван, например delete_element",
            },
            elementIds: {
              type: "array",
              items: { type: "string" },
              description: "ElementId элементов, которых коснётся действие (обязательно для удаления)",
            },
          },
          required: ["action"],
        },
        execute: async (args) => {
          const action = typeof args.action === "string" ? args.action : "Действие в модели";
          const details = typeof args.details === "string" ? args.details : "";
          const tool = typeof args.tool === "string" ? args.tool : "";
          const elementIds = Array.isArray(args.elementIds)
            ? args.elementIds.filter((id): id is string => typeof id === "string")
            : [];
          const approved = await this.askConfirmation(sessionId, action, details, tool, elementIds);
          return {
            content: [
              {
                type: "text",
                text: approved
                  ? "approved=true — пользователь подтвердил, можно выполнять."
                  : "approved=false — пользователь отказал. Не выполняй действие.",
              },
            ],
            structuredContent: { approved },
          };
        },
      },
    };
  }

  private askConfirmation(
    sessionId: string,
    action: string,
    details: string,
    tool: string,
    elementIds: string[],
  ): Promise<boolean> {
    const session = this.sessions.get(sessionId);
    const emitter = session?.emitter;
    // No live panel to ask — fail closed.
    if (!session || !emitter) return Promise.resolve(false);

    const requestId = `${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 8)}`;

    return new Promise<boolean>((resolve) => {
      let settled = false;
      const finish = (approved: boolean) => {
        if (settled) return;
        settled = true;
        clearTimeout(timer);
        session.pendingConfirms.delete(requestId);
        resolve(approved);
      };

      const timer = setTimeout(() => finish(false), CONFIRM_TIMEOUT_MS);
      session.pendingConfirms.set(requestId, finish);
      emitter.confirm({ requestId, action, details, tool, elementIds });
    });
  }

  /** Answer a pending revit_confirm. Returns false when nothing was waiting. */
  resolveConfirmation(sessionId: string, requestId: string, approved: boolean): boolean {
    const session = this.sessions.get(sessionId);
    const pending = session?.pendingConfirms.get(requestId);
    if (!pending) return false;
    pending(approved);
    return true;
  }

  /** Stop the run in flight. Returns false when the session has nothing running. */
  async cancel(sessionId: string): Promise<boolean> {
    const session = this.sessions.get(sessionId);
    if (!session) return false;

    // Unblock any confirmation the agent is waiting on, else cancel() stalls.
    for (const [, resolve] of session.pendingConfirms) resolve(false);
    session.pendingConfirms.clear();

    const run = session.activeRun;
    if (!run) return false;

    try {
      if (run.supports("cancel")) {
        await run.cancel();
        return true;
      }
    } catch {
      /* run already finished */
    }
    return false;
  }

  private async getOrCreateSession(sessionId: string, reset: boolean): Promise<SessionRecord> {
    if (!reset) {
      const existing = this.sessions.get(sessionId);
      if (existing) return existing;
    } else {
      const old = this.sessions.get(sessionId);
      if (old) {
        try {
          await old.agent.close();
        } catch {
          /* ignore */
        }
        this.sessions.delete(sessionId);
      }
    }

    const agent = await Agent.create({
      apiKey: this.config.apiKey,
      model: this.config.model,
      tools: ["mcp"],
      local: {
        cwd: this.config.rulesCwd,
        settingSources: ["project"],
        customTools: this.buildCustomTools(sessionId),
        store: this.store,
      },
      mcpServers: this.buildMcpServers(),
    });

    const record: SessionRecord = {
      agentId: agent.agentId,
      agent,
      pendingConfirms: new Map(),
    };
    this.sessions.set(sessionId, record);
    return record;
  }

  /**
   * A run left active by a crashed bridge or a killed Revit wedges the agent
   * ("already has active run"). Expire it once and retry rather than making the
   * architect start a new chat.
   */
  private async sendWithRecovery(
    session: SessionRecord,
    message: { text: string; images?: Array<{ data: string; mimeType: string }> },
  ): Promise<Run> {
    try {
      return await session.agent.send(message);
    } catch (err) {
      const text = err instanceof Error ? err.message : String(err);
      if (!/already has active run/i.test(text)) throw err;

      return await session.agent.send(message, { local: { force: true } });
    }
  }

  async handleChat(req: ChatRequest, emit: SseEmitter): Promise<void> {
    const sessionId = (req.sessionId ?? "default").trim() || "default";
    const message = (req.message ?? "").trim();
    if (!message) {
      emit.error("Пустой запрос");
      return;
    }

    emit.status("Подключаю Cursor…");
    const session = await this.getOrCreateSession(sessionId, !!req.reset);
    session.emitter = emit;

    const promptText = REVIT_SYSTEM_PREFIX + message;
    const images =
      req.images?.map((img) => ({
        data: img.dataBase64,
        mimeType: img.mimeType,
      })) ?? [];

    emit.status("Думает…");

    let reply = "";
    const doneSummary: string[] = [];
    const toolNamesByCallId = new Map<string, string>();
    let selectedModel: ModelSelection | undefined;
    let resumedAfterTool = false;
    // Text written after the last tool call — the actual answer. Everything before
    // it is the agent narrating what it is about to do, which the steps journal
    // already shows.
    let finalSegment = "";

    try {
      const run = await this.sendWithRecovery(session, {
        text: promptText,
        images: images.length > 0 ? images : undefined,
      });
      session.activeRun = run;

      for await (const event of run.stream()) {
        if (event.type === "system" && event.model) {
          selectedModel = event.model;
        }

        if (event.type === "thinking") {
          emit.status("Думает…");
        }

        if (event.type === "assistant") {
          for (const block of event.message.content) {
            if (block.type === "text" && block.text) {
              // Text blocks stream in as deltas, so never separate them blindly.
              // Only the first text after a tool call starts a new paragraph —
              // otherwise the preamble and the answer run into one sentence.
              if (resumedAfterTool && reply && !/\s$/.test(reply)) reply += "\n\n";
              resumedAfterTool = false;
              reply += block.text;
              finalSegment += block.text;
              emit.textDelta(reply);
            }
          }
        }

        if (event.type === "tool_call") {
          resumedAfterTool = true;
          finalSegment = "";
          const callId = event.call_id ?? event.name ?? "tool";
          const name = resolveToolName(
            event.name,
            event.args ?? toolNamesByCallId.get(callId),
          );
          if (event.status === "running") {
            toolNamesByCallId.set(callId, name);
          }
          const stableName = toolNamesByCallId.get(callId) ?? name;

          if (event.status === "running") {
            emit.toolStep({
              callId,
              name: stableName,
              status: "running",
              args: truncate(event.args),
            });
          } else if (event.status === "completed") {
            emit.toolStep({
              callId,
              name: stableName,
              status: "ok",
              args: truncate(event.args),
              result: truncate(event.result),
            });
            doneSummary.push(stableName);
            toolNamesByCallId.delete(callId);
          } else if (event.status === "error") {
            emit.toolStep({
              callId,
              name: stableName,
              status: "error",
              args: truncate(event.args),
              result: truncate(event.result),
            });
            toolNamesByCallId.delete(callId);
          }
        }
      }

      const result = await run.wait();
      if (result.model) selectedModel = result.model;

      if (result.status === "cancelled") {
        emit.done({
          reply: reply.trim() || "Остановлено.",
          model: this.describeModel(selectedModel),
          doneSummary: [...new Set(doneSummary)],
        });
        return;
      }

      if (result.status === "error") {
        emit.error(result.error?.message ?? `Cursor agent завершился с ошибкой. run=${result.id}`);
        return;
      }

      // Prefer the answer written after the last tool call; the narration before it
      // duplicates the steps journal.
      const answer = finalSegment.trim() || reply.trim() || "Готово.";

      emit.done({
        reply: answer,
        model: this.describeModel(selectedModel),
        doneSummary: [...new Set(doneSummary)],
      });
    } finally {
      session.activeRun = undefined;
      for (const [, resolve] of session.pendingConfirms) resolve(false);
      session.pendingConfirms.clear();
      if (session.emitter === emit) session.emitter = undefined;
    }
  }

  /** Label of the model Cursor actually ran, falling back to the configured one. */
  private describeModel(selected: ModelSelection | undefined): string {
    // The panel shows a bare "Авто" whenever the router does not name the model it
    // picked, and "no model reported" looks identical to "reported: default". Log the
    // raw value so the two can be told apart without guessing.
    console.error(
      "[assistant-bridge] model reported by Cursor:",
      selected ? JSON.stringify(selected) : "none",
    );
    if (!selected) return this.config.modelLabel;
    const actual = modelLabel(selected);
    // Show what the router actually picked, e.g. "Авто → Composer 2.5".
    if (this.config.model.id !== AUTO_MODEL_ID || selected.id === AUTO_MODEL_ID) return actual;
    return `Авто → ${actual}`;
  }

  async disposeAll(): Promise<void> {
    for (const [, record] of this.sessions) {
      try {
        await record.agent.close();
      } catch {
        /* ignore */
      }
    }
    this.sessions.clear();
  }
}
