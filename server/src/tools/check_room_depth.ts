import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";
import { isLivingScopeAlias } from "../normatives/normAudit/roomPurpose.js";

export function registerCheckRoomDepthTool(server: McpServer) {
  server.tool(
    "check_room_depth",
    "Check actual room depths in the open Revit model against a normative depth limit (СП РК 3.02-101 п. 4.4.10.22 — max 6 m for living rooms). By default only living rooms are checked (спальня, гостиная, детская, кабинет…): stairs, corridors, PON, kitchens are excluded. Pass roomScope='all' to check every room. Filter «жилая» is treated as living scope, not a name substring. Obtain the limit from query_norm_rules / extract_norm_rules_from_pdf and pass source. Depth is the larger side of the room bounding footprint, in millimeters.",
    {
      minDepthMm: z
        .number()
        .positive()
        .optional()
        .describe("Minimum allowed room depth in millimeters."),
      maxDepthMm: z
        .number()
        .positive()
        .optional()
        .describe("Maximum allowed room depth in millimeters."),
      source: z
        .object({
          document: z.string().describe("Document code, e.g. СП РК 3.02-101"),
          clause: z.string().describe("Clause reference, e.g. п. 4.4.10.22"),
          quote: z.string().describe("Original normative sentence"),
          page: z.number().int().positive().optional(),
        })
        .optional()
        .describe(
          "Normative source of the limit — always pass it so the report cites the norm."
        ),
      mode: z
        .enum(["report", "highlight"])
        .optional()
        .default("report")
        .describe(
          "'report' returns data only; 'highlight' also colors violating room tag labels red in the active view."
        ),
      levelName: z.string().optional().default("").describe("Optional level name filter."),
      roomScope: z
        .enum(["living", "all"])
        .optional()
        .default("living")
        .describe(
          "'living' (default) — only жилые комнаты per п. 4.4.10.22; 'all' — every room with Area>0."
        ),
      roomNameFilter: z
        .string()
        .optional()
        .default("")
        .describe(
          "Optional extra name/purpose substring within roomScope. Values like «жилая» mean living scope (not substring)."
        ),
      includeCompliant: z
        .boolean()
        .optional()
        .default(false)
        .describe("Also return rooms that pass the check."),
      highlightColor: z
        .object({
          r: z.number().int().min(0).max(255),
          g: z.number().int().min(0).max(255),
          b: z.number().int().min(0).max(255),
        })
        .optional()
        .describe("Highlight color, defaults to red."),
    },
    async (args) => {
      if (args.minDepthMm === undefined && args.maxDepthMm === undefined) {
        return {
          content: [
            {
              type: "text",
              text: "check_room_depth failed: either minDepthMm or maxDepthMm is required. Get the value from a normative rule (query_norm_rules / extract_norm_rules_from_pdf) first.",
            },
          ],
          isError: true,
        };
      }

      const roomScope = isLivingScopeAlias(args.roomNameFilter)
        ? "living"
        : (args.roomScope ?? "living");
      const roomNameFilter = isLivingScopeAlias(args.roomNameFilter)
        ? ""
        : (args.roomNameFilter ?? "");

      const params = {
        minDepthMm: args.minDepthMm,
        maxDepthMm: args.maxDepthMm,
        mode: args.mode ?? "report",
        levelName: args.levelName ?? "",
        roomScope,
        roomNameFilter,
        includeCompliant: args.includeCompliant ?? false,
        highlightColor: args.highlightColor,
      };

      try {
        const response = await withRevitConnection(async (revitClient) => {
          return await revitClient.sendCommand("check_room_depth", params);
        });

        return {
          content: [
            {
              type: "text",
              text: JSON.stringify(
                {
                  norm: {
                    minDepthMm: args.minDepthMm ?? null,
                    maxDepthMm: args.maxDepthMm ?? null,
                    source: args.source ?? null,
                    roomScope,
                  },
                  ...(typeof response === "object" && response !== null
                    ? response
                    : { result: response }),
                },
                null,
                2
              ),
            },
          ],
        };
      } catch (error) {
        return {
          content: [
            {
              type: "text",
              text: `check_room_depth failed: ${
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
