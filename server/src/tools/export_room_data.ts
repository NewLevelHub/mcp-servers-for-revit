import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";
import { paginateRows } from "../utils/responseTrimming.js";

/**
 * Returned when the caller names no fields (REV-42).
 *
 * The Revit model carries 19 fields per room, most of them heights and phases
 * that only the norm audit reads — and it fetches them over `sendCommand`, not
 * through this tool. On «Короткий блок» the untrimmed answer was 314 743 B for
 * 572 rooms, the largest single payload in the metrics log.
 */
const DEFAULT_ROOM_FIELDS = ["id", "name", "number", "level", "area"] as const;

/** Everything the C# RoomDataModel can return, for the `fields` description. */
const ALL_ROOM_FIELDS = [
  "id",
  "uniqueId",
  "name",
  "number",
  "level",
  "area",
  "volume",
  "perimeter",
  "unboundedHeight",
  "storeyHeight",
  "floorThickness",
  "clearHeight",
  "heightSource",
  "upperLimitLevel",
  "limitOffset",
  "department",
  "comments",
  "phase",
  "occupancy",
] as const;

export function registerExportRoomDataTool(server: McpServer) {
  server.tool(
    "export_room_data",
    "Export room data from the Revit project. Returns ElementIds, names, numbers, levels, areas (m²), etc. " +
      "For «сколько помещений на этаже» use filterByActiveView=true or levelName — otherwise all project rooms are returned. " +
      "Counts and totals (totalRooms, totalArea) always describe the whole filtered set, never just the returned page — " +
      "answer «сколько» from those and do not page for it. " +
      `Rows carry ${DEFAULT_ROOM_FIELDS.join("/")} by default; ask for more via fields, or fields:["all"].`,
    {
      includeUnplacedRooms: z
        .boolean()
        .optional()
        .default(false)
        .describe("Whether to include unplaced rooms (rooms not yet placed in the model). Defaults to false."),
      includeNotEnclosedRooms: z
        .boolean()
        .optional()
        .default(false)
        .describe("Whether to include rooms that are not fully enclosed. Defaults to false."),
      filterByActiveView: z
        .boolean()
        .optional()
        .default(false)
        .describe("When true, only rooms on the active floor plan level. Use for «на этаже» queries."),
      levelName: z
        .string()
        .optional()
        .describe("Filter by level name (e.g. «2 этаж»). Ignored when filterByActiveView resolves a level."),
      levelId: z
        .number()
        .optional()
        .describe("Filter by level ElementId. Takes precedence over levelName when set."),
      fields: z
        .array(z.string())
        .optional()
        .describe(
          `Which room fields to return. Default ${JSON.stringify([...DEFAULT_ROOM_FIELDS])}. ` +
            `Pass ["all"] for every field, or name the ones you need: ${ALL_ROOM_FIELDS.join(", ")}. ` +
            "Heights (clearHeight, storeyHeight, unboundedHeight) are not returned unless asked for."
        ),
      offset: z
        .number()
        .int()
        .min(0)
        .optional()
        .default(0)
        .describe("Rooms to skip. Use roomsPagination.nextOffset from the previous call."),
      limit: z
        .number()
        .int()
        .min(1)
        .max(2000)
        .optional()
        .default(300)
        .describe(
          "Max rooms returned per call (default 300). totalRooms still reports the full count."
        ),
    },
    async (args) => {
      const params = {
        includeUnplacedRooms: args.includeUnplacedRooms ?? false,
        includeNotEnclosedRooms: args.includeNotEnclosedRooms ?? false,
        filterByActiveView: args.filterByActiveView ?? false,
        ...(args.levelName ? { levelName: args.levelName } : {}),
        ...(args.levelId != null ? { levelId: args.levelId } : {}),
      };

      try {
        const response = await withRevitConnection(async (revitClient) => {
          return await revitClient.sendCommand("export_room_data", params);
        });

        // Revit still returns every room and every field; the trim happens here,
        // so the norm audit (which calls sendCommand directly) keeps the full set.
        const trimmed = paginateRows(response, {
          key: "rooms",
          offset: args.offset ?? 0,
          limit: args.limit ?? 300,
          fields: args.fields ?? [...DEFAULT_ROOM_FIELDS],
        });

        return {
          content: [
            {
              type: "text",
              text: JSON.stringify(trimmed),
            },
          ],
        };
      } catch (error) {
        return {
          content: [
            {
              type: "text",
              text: `Export room data failed: ${
                error instanceof Error ? error.message : String(error)
              }`,
            },
          ],
          isError: true,
        };
      }
    }
  );
}
