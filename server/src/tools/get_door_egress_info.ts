import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";
import { annotateDoorEgressResponse } from "../normatives/normAudit/annotateDoorManeuvering.js";

export function registerGetDoorEgressInfoTool(server: McpServer) {
  server.tool(
    "get_door_egress_info",
    "Extract door nominal/clear width, maneuvering space, egress-path hints, and ramp slopes for accessibility checks. " +
      "Each door carries maneuveringVerdict (ok / near_limit / violation / not_measured) with the required depth AND width " +
      "already applied, plus maneuveringSummary over the set — read those, do not compare the raw millimetres yourself. " +
      "Maneuvering space is an МГН (accessibility) requirement, not a fire one: for fire-rated doors use check_fire_doors.",
    {
      levelName: z
        .string()
        .optional()
        .default("")
        .describe("Optional level name filter."),
    },
    async (args) => {
      const params = {
        levelName: args.levelName ?? "",
      };

      try {
        const response = await withRevitConnection(async (revitClient) => {
          return await revitClient.sendCommand("get_door_egress_info", params);
        });

        return {
          content: [
            {
              type: "text",
              text: JSON.stringify(annotateDoorEgressResponse(response)),
            },
          ],
        };
      } catch (error) {
        return {
          content: [
            {
              type: "text",
              text: `get_door_egress_info failed: ${
                error instanceof Error ? error.message : String(error)
              }`,
            },
          ],
        };
      }
    }
  );
}
