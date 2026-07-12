import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";

export function registerCheckRoomDepthTool(server: McpServer) {
  server.tool(
    "check_room_depth",
    "Check actual room depths in the open Revit model against a normative depth limit. Obtain the limit from a normative source first (extract_norm_rules_from_pdf / query_norm_rules) and pass it here together with its source — the report echoes the norm with the original quote, lists every checked room with its actual depth, and returns violators with element ids. Mode 'highlight' colors violating room tag labels red in the active view without filling the room. Depth is the larger side of the room's bounding footprint, in millimeters.",
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
          clause: z.string().describe("Clause reference, e.g. п. 5.2.4"),
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
      roomNameFilter: z
        .string()
        .optional()
        .default("")
        .describe(
          "Optional case-insensitive room name substring filter, e.g. 'жилая'."
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

      const params = {
        minDepthMm: args.minDepthMm,
        maxDepthMm: args.maxDepthMm,
        mode: args.mode ?? "report",
        levelName: args.levelName ?? "",
        roomNameFilter: args.roomNameFilter ?? "",
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
