import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import db from "../database/db.js";
import { resolveRailingHeightLimitFromLibrary } from "../normatives/normAudit/resolveVerticalCirculation.js";
import type { NormAuditSource } from "../normatives/normAudit/types.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";
import {
  resolveRailingAuthoringDefaults,
  type RailingAuthoringInput,
} from "../normatives/authoring/stairRailingDefaults.js";

const pointMm = z.object({
  x: z.number().describe("X in mm"),
  y: z.number().describe("Y in mm"),
  z: z.number().optional().describe("Z in mm (path uses level plane)"),
});

/**
 * REV-83 — create railing by path or hosted on a stair.
 * Requires RailingType typeId; height comes from type (ADSK h NNNN) / library default.
 */
export function registerCreateRailingTool(server: McpServer) {
  server.tool(
    "create_railing",
    "Create a railing by path (pathPoints + levelId) OR hosted on a stair (hostElementId) — REV-83. " +
      "Requires typeId from get_available_family_types (RailingType / OST_StairsRailing). " +
      "Do not pass both host and path. " +
      "If heightMm is omitted, resolves min railing height from the norm library " +
      "(fail if empty — seed norms). Height is not mutated on the type; pick an ADSK type " +
      "whose name encodes height (e.g. h 1200). Units: mm. " +
      "Optional annotation via create_text_notes.",
    {
      data: z
        .array(
          z.object({
            typeId: z
              .number()
              .int()
              .positive()
              .describe(
                "Required RailingType ElementId from get_available_family_types. Missing/invalid fails."
              ),
            hostElementId: z
              .number()
              .int()
              .positive()
              .optional()
              .describe("Stairs ElementId to host railing on (replaces default railings)"),
            pathPoints: z
              .array(pointMm)
              .min(2)
              .optional()
              .describe("Path for free-standing railing (≥2 points, mm)"),
            levelId: z
              .number()
              .int()
              .positive()
              .optional()
              .describe("Base Level ElementId for path mode"),
            levelOffsetMm: z
              .number()
              .optional()
              .describe("Optional base offset mm for path mode"),
            isClosedLoop: z
              .boolean()
              .optional()
              .describe("Close path as a loop"),
            heightMm: z
              .number()
              .positive()
              .optional()
              .describe(
                "Desired min height mm (informational). If omitted, from norm library."
              ),
          })
        )
        .min(1)
        .describe("Array of railings to create"),
    },
    async (args) => {
      try {
        const resolvedItems: Array<
          RailingAuthoringInput & {
            heightMm: number;
            normSource?: NormAuditSource;
            warnings: string[];
          }
        > = [];

        for (const item of args.data) {
          const resolved = resolveRailingAuthoringDefaults(db, item, {
            resolveHeight: resolveRailingHeightLimitFromLibrary,
          });
          if (!resolved.ok) {
            return {
              content: [
                {
                  type: "text",
                  text: JSON.stringify(
                    {
                      success: false,
                      message: resolved.error,
                    },
                    null,
                    2
                  ),
                },
              ],
            };
          }
          resolvedItems.push(resolved.value);
        }

        const payload = {
          data: resolvedItems.map((item) => ({
            typeId: item.typeId,
            ...(item.hostElementId != null
              ? { hostElementId: item.hostElementId }
              : {}),
            ...(item.pathPoints != null ? { pathPoints: item.pathPoints } : {}),
            ...(item.levelId != null ? { levelId: item.levelId } : {}),
            ...(item.levelOffsetMm != null
              ? { levelOffsetMm: item.levelOffsetMm }
              : {}),
            ...(item.isClosedLoop != null
              ? { isClosedLoop: item.isClosedLoop }
              : {}),
            heightMm: item.heightMm,
          })),
        };

        const response = await withRevitConnection(async (revitClient) => {
          return await revitClient.sendCommand("create_railing", payload);
        });

        return {
          content: [
            {
              type: "text",
              text: JSON.stringify(
                {
                  ...((response && typeof response === "object"
                    ? response
                    : { revit: response }) as object),
                  authoring: {
                    items: resolvedItems.map((item) => ({
                      heightMm: item.heightMm,
                      normSource: item.normSource,
                      warnings: item.warnings,
                    })),
                  },
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
              text: `Create railing failed: ${
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
