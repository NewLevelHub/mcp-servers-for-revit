import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";

export function registerLoadFamilyTool(server: McpServer) {
  server.tool(
    "load_family",
    "Load .rfa families into the open project and report their types, ready to pass to place_detail_component or create_point_based_element. Paths are read on the machine running Revit, not on the MCP client. Give explicit paths, or a directory plus names (or a directory alone to take every .rfa in it). A missing file or a non-.rfa path comes back as a named warning instead of a Revit exception.",
    {
      paths: z
        .array(z.string())
        .optional()
        .describe("Full paths to .rfa files on the machine running Revit."),
      directory: z
        .string()
        .optional()
        .describe("Folder to take families from; used with names, or alone to load every .rfa in it."),
      names: z
        .array(z.string())
        .optional()
        .describe("Family file names inside directory, with or without the .rfa extension."),
      overwriteParameterValues: z
        .boolean()
        .optional()
        .describe("Overwrite parameter values of a family already in the project. Default false."),
      activateSymbols: z
        .boolean()
        .optional()
        .describe("Activate every loaded type so it can be placed immediately. Default true."),
    },
    async (args) => {
      const params = {
        paths: args.paths ?? [],
        directory: args.directory ?? "",
        names: args.names ?? [],
        overwriteParameterValues: args.overwriteParameterValues ?? false,
        activateSymbols: args.activateSymbols ?? true,
      };

      try {
        const response = await withRevitConnection(async (revitClient) => {
          return await revitClient.sendCommand("load_family", params);
        });

        return {
          content: [
            {
              type: "text",
              text: JSON.stringify(response),
            },
          ],
        };
      } catch (error) {
        return {
          content: [
            {
              type: "text",
              text: `Load family failed: ${error instanceof Error ? error.message : String(error)}`,
            },
          ],
        };
      }
    }
  );
}
