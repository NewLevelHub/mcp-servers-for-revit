import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";
import { capLists, filterListsByName } from "../utils/responseTrimming.js";

/**
 * Lists the model needs whole, because a name it cannot see is a name it will
 * invent: these are the ones fed straight back as arguments to create_* calls,
 * and they are short in every real project.
 */
const UNCAPPED_STYLE_LISTS = [
  "dimensionTypes",
  "gridTypes",
  "textNoteTypes",
  "titleBlocks",
  "filledRegionTypes",
] as const;

export function registerGetDocumentStylesTool(server: McpServer) {
  server.tool(
    "get_document_styles",
    "Return project annotation and detailing styles: dimension types, grid types, text note types (text height in mm), line patterns, line styles (OST_Lines subcategories with weight, pattern and colour), filled region types, fill patterns (drafting/model hatches), and title block types. Call this before create_detail_lines (lineStyleName), create_detail_regions (filledRegionTypeName / fillPatternName) and create_node_detail. Raw GraphicsStyle entries are omitted by default (can be thousands); lineStyles is the short list you normally want.",
    {
      includeGraphicsStyles: z
        .boolean()
        .optional()
        .describe(
          "Include GraphicsStyle entries (can be thousands). Default false for faster responses."
        ),
      nameFilter: z
        .string()
        .optional()
        .describe(
          "Keep only styles whose name contains this text (case-insensitive), across every list. " +
            "Use it to find a specific style — e.g. nameFilter:\"ADSK\" or nameFilter:\"бетон\" — " +
            "instead of raising listLimit and reading hundreds of entries."
        ),
      listLimit: z
        .number()
        .int()
        .min(1)
        .max(2000)
        .optional()
        .default(60)
        .describe(
          "Max entries per long list (line patterns, line styles, fill patterns, graphics styles). " +
            "Default 60. listsTruncated.totals reports the real length of anything cut. " +
            "Prefer nameFilter over a large listLimit."
        ),
    },
    async (args) => {
      try {
        const response = await withRevitConnection(async (revitClient) => {
          return await revitClient.sendCommand("get_document_styles", {
            includeGraphicsStyles: args.includeGraphicsStyles ?? false,
          });
        });

        // 158 KB on «Короткий блок» even with graphicsStyles off — the hatch and
        // line-pattern lists run to hundreds of entries nobody reads (REV-42).
        // Filter first, then cap: a search that matched must not be cut off by
        // the cap meant for the unfiltered list.
        const filtered = args.nameFilter
          ? filterListsByName(response, args.nameFilter)
          : response;

        const trimmed = capLists(filtered, {
          limit: args.listLimit ?? 60,
          keep: [...UNCAPPED_STYLE_LISTS],
          narrowHint:
            "повтори вызов с nameFilter (подстрока имени) — это надёжнее, чем большой listLimit.",
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
              text: `get document styles failed: ${
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
