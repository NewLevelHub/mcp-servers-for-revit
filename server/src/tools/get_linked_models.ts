import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";

export function registerGetLinkedModelsTool(server: McpServer) {
  server.tool(
    "get_linked_models",
    "List the Revit links of the open model — the смежники files: АР, КР and the ИОС trades. " +
      "Each link comes back with its file name, the раздел read off that name, Revit's own load " +
      "status, how many elements it holds, and its placement expressed in OUR coordinates " +
      "(origin in mm, rotation, mirroring) from GetTotalTransform. coordinateSamples returns " +
      "sample elements in both coordinate systems, so a link inserted away from 0,0 can be " +
      "checked against the model rather than trusted. Unloaded and missing links are reported " +
      "with a status instead of failing the call. " +
      "STRICTLY READ-ONLY IN BOTH DIRECTIONS: it opens no transaction here and writes nothing " +
      "into the linked file — a link belongs to another office and is never modified. " +
      "This is Revit .rvt links only; for DWG underlays use get_cad_link_geometry instead.",
    {
      includeElementCounts: z
        .boolean()
        .optional()
        .default(true)
        .describe(
          "Count the elements of each loaded link (default true). The count is taken without " +
            "materialising elements, so it is cheap even on a large ИОС file; set false when only " +
            "the list and the placement matter."
        ),
      includeCategories: z
        .boolean()
        .optional()
        .default(false)
        .describe(
          "Break each link down by Revit category. Off by default: this is the one reading that " +
            "walks every element of every link, and on a big MEP model it dominates the call. " +
            "Pair it with levelName when you need it."
        ),
      categoryLimit: z
        .number()
        .int()
        .min(1)
        .max(50)
        .optional()
        .default(8)
        .describe("Categories kept per link, largest first (default 8). Only used with includeCategories."),
      coordinateSamples: z
        .number()
        .int()
        .min(0)
        .max(20)
        .optional()
        .default(1)
        .describe(
          "Sample elements per link reported as linkPointMm (as the linked file stores them) and " +
            "hostPointMm (the same point in our coordinates). Default 1 — enough to verify an " +
            "offset link; 0 skips the sampling."
        ),
      levelName: z
        .string()
        .optional()
        .describe(
          "Count and sample only what sits on this level INSIDE the link. The link names its floors " +
            "its own way, so a name that does not resolve there is reported per link and the count " +
            "falls back to the whole file."
        ),
      nameFilter: z
        .string()
        .optional()
        .describe("Keep only links whose file name contains this text (case-insensitive)."),
    },
    async (args) => {
      try {
        const response = await withRevitConnection(async (revitClient) => {
          return await revitClient.sendCommand("get_linked_models", {
            includeElementCounts: args.includeElementCounts ?? true,
            includeCategories: args.includeCategories ?? false,
            categoryLimit: args.categoryLimit ?? 8,
            coordinateSamples: args.coordinateSamples ?? 1,
            ...(args.levelName ? { levelName: args.levelName } : {}),
            ...(args.nameFilter ? { nameFilter: args.nameFilter } : {}),
          });
        });

        // No pagination here on purpose: a project has links in the tens, not the
        // thousands, and dropping one of them from the list is exactly the failure
        // this tool exists to prevent.
        return {
          content: [{ type: "text" as const, text: JSON.stringify(response) }],
        };
      } catch (error) {
        return {
          content: [
            {
              type: "text" as const,
              text: `get_linked_models не выполнен: ${
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
