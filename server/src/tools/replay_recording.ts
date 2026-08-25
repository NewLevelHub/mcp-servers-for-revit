import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { readdir, readFile } from "node:fs/promises";
import { homedir } from "node:os";
import { join } from "node:path";
import { withRevitConnection } from "../utils/ConnectionManager.js";

/** Matches plugin/Core/Recorder/RecordingStore.cs's CatalogDirectory-style path exactly. */
const RECORDINGS_DIRECTORY = join(homedir(), ".mcp-servers-for-revit", "recordings");

interface RecordedRecipeFile {
  id: string;
  name: string;
  recordedUtc: string;
  sourceLevelName?: string;
  summaryText?: string;
  actions?: unknown[];
}

async function listRecordings() {
  let files: string[];
  try {
    files = (await readdir(RECORDINGS_DIRECTORY)).filter((f) => f.endsWith(".json"));
  } catch {
    return { recordings: [], message: "Записей ещё нет — включите запись кнопкой в панели, сделайте несколько действий, выключите." };
  }

  const recordings = [];
  for (const file of files) {
    try {
      const raw = JSON.parse(await readFile(join(RECORDINGS_DIRECTORY, file), "utf-8")) as RecordedRecipeFile;
      recordings.push({
        id: raw.id,
        name: raw.name,
        recordedUtc: raw.recordedUtc,
        sourceLevelName: raw.sourceLevelName,
        summaryText: raw.summaryText,
        actionCount: raw.actions?.length ?? 0,
      });
    } catch {
      // A corrupt file must not take down the whole list.
    }
  }
  recordings.sort((a, b) => (a.recordedUtc < b.recordedUtc ? 1 : -1));

  return {
    recordings,
    message: recordings.length === 0 ? "Записей ещё нет." : `Найдено записей: ${recordings.length}.`,
  };
}

export function registerReplayRecordingTool(server: McpServer) {
  server.tool(
    "replay_recording",
    "REV-177: replays a hand-recorded sequence of actions (walls, hosted doors/windows, and any " +
      "Mark/Comments set while recording) on other levels — the panel's own record button captures " +
      "the recipe, this tool reproduces it. action:\"list\" (no Revit needed) shows what's recorded; " +
      "give targetLevelNames (e.g. [\"4 этаж\",\"5 этаж\"]) or fromFloor/toFloor (e.g. 3..16) to replay. " +
      "Always previews first: confirm omitted or false computes and reports what WOULD happen — matched " +
      "hosts, resolved types — without creating anything; confirm:true actually creates. Only creations " +
      "replay: parameter edits and deletions of pre-existing (not recorded-created) elements are reported " +
      "but never applied to the target level, and any action replay can't place (missing type, no host " +
      "wall found within ~50mm) is listed with its own reason, never silently skipped.",
    {
      action: z.enum(["list", "replay"]).optional().describe("Omit with recordingId set to replay; omit both for list."),
      recordingId: z.string().optional().describe("Id from action:\"list\" — required to replay."),
      targetLevelNames: z
        .array(z.string())
        .optional()
        .describe("Explicit level names to replay onto, e.g. [\"4 этаж\", \"5 этаж\"]."),
      fromFloor: z.number().int().optional().describe("Start of a numeric floor range, e.g. 3 for «этажи 3–16». Alternative to targetLevelNames."),
      toFloor: z.number().int().optional().describe("End of a numeric floor range, e.g. 16 for «этажи 3–16»."),
      confirm: z
        .boolean()
        .optional()
        .default(false)
        .describe("If true, actually creates the elements. Default false: preview only, nothing is written."),
    },
    async (args) => {
      try {
        const action = args.action ?? (args.recordingId ? "replay" : "list");

        if (action === "list") {
          const result = await listRecordings();
          return { content: [{ type: "text" as const, text: JSON.stringify(result) }] };
        }

        if (!args.recordingId) {
          throw new Error("replay requires recordingId — call action:\"list\" first to find one.");
        }
        if (!args.targetLevelNames?.length && (args.fromFloor == null || args.toFloor == null)) {
          throw new Error("Specify targetLevelNames, or both fromFloor and toFloor.");
        }

        const response = await withRevitConnection(async (revitClient) => {
          return await revitClient.sendCommand("replay_recording", {
            recordingId: args.recordingId,
            targetLevelNames: args.targetLevelNames,
            fromFloor: args.fromFloor,
            toFloor: args.toFloor,
            confirm: args.confirm ?? false,
          });
        });

        return { content: [{ type: "text" as const, text: JSON.stringify(response) }] };
      } catch (error) {
        return {
          content: [
            {
              type: "text" as const,
              text: `replay_recording failed: ${error instanceof Error ? error.message : String(error)}`,
            },
          ],
          isError: true,
        };
      }
    }
  );
}
