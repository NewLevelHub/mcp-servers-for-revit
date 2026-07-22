import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";

const pointMm = z.object({
  x: z.number().describe("X in mm"),
  y: z.number().describe("Y in mm"),
  z: z.number().optional().describe("Z ignored — profile is plan (XY)"),
});

const rectMm = z.object({
  origin: pointMm.describe("SW corner before rotation (mm)"),
  widthMm: z.number().positive().describe("Size along local +X (mm)"),
  depthMm: z.number().positive().describe("Size along local +Y (mm)"),
  rotationDeg: z
    .number()
    .optional()
    .describe("CCW rotation around origin in degrees (default 0)"),
});

/**
 * REV-85 — cut openings in floors (one slab) or vertical shafts (level range).
 * Pair with create_stair: cut shaft/opening first, then place stair in the same contour.
 */
export function registerCreateFloorOpeningTool(server: McpServer) {
  server.tool(
    "create_floor_opening",
    "Cut an opening in a floor slab or create a vertical shaft through levels (REV-85). " +
      "mode=floor: requires hostFloorId OR levelId (finds Floor whose bbox contains opening centroid). " +
      "mode=shaft: requires baseLevelId + topLevelId (shaft opening for stair/lift/MEP). " +
      "Profile: boundaryPoints (≥3 corners, mm) OR rect {origin,widthMm,depthMm,rotationDeg?}. " +
      "Explicit fail if host/levels/profile invalid — no silent skip. Units mm. " +
      "Typical stair workflow: create_floor_opening (shaft or upper slab) → create_stair in same contour.",
    {
      data: z
        .array(
          z
            .object({
              mode: z
                .enum(["floor", "shaft"])
                .optional()
                .default("floor")
                .describe("floor = one slab cut; shaft = vertical opening between levels"),
              hostFloorId: z
                .number()
                .int()
                .positive()
                .optional()
                .describe("Floor ElementId to cut (mode=floor, preferred)"),
              levelId: z
                .number()
                .int()
                .positive()
                .optional()
                .describe(
                  "Level of the slab (mode=floor). Used when hostFloorId omitted — finds Floor containing centroid"
                ),
              baseLevelId: z
                .number()
                .int()
                .positive()
                .optional()
                .describe("Bottom Level ElementId (mode=shaft)"),
              topLevelId: z
                .number()
                .int()
                .positive()
                .optional()
                .describe("Top Level ElementId (mode=shaft, must be above base)"),
              boundaryPoints: z
                .array(pointMm)
                .min(3)
                .optional()
                .describe("Closed plan polygon corners in mm (Z ignored)"),
              rect: rectMm
                .optional()
                .describe("Rectangle shortcut — mutually exclusive with boundaryPoints"),
              perpendicularFace: z
                .boolean()
                .optional()
                .default(true)
                .describe("mode=floor only: cut perpendicular to host face (default true)"),
            })
            .superRefine((item, ctx) => {
              const mode = item.mode ?? "floor";
              const hasBoundary =
                Array.isArray(item.boundaryPoints) && item.boundaryPoints.length >= 3;
              const hasRect = item.rect != null;
              if (hasBoundary === hasRect) {
                ctx.addIssue({
                  code: z.ZodIssueCode.custom,
                  message:
                    "Provide exactly one of boundaryPoints (≥3) or rect (origin+widthMm+depthMm).",
                });
              }
              if (mode === "floor") {
                if (item.hostFloorId == null && item.levelId == null) {
                  ctx.addIssue({
                    code: z.ZodIssueCode.custom,
                    message: "mode=floor requires hostFloorId or levelId.",
                  });
                }
              } else if (mode === "shaft") {
                if (item.baseLevelId == null || item.topLevelId == null) {
                  ctx.addIssue({
                    code: z.ZodIssueCode.custom,
                    message: "mode=shaft requires baseLevelId and topLevelId.",
                  });
                }
              }
            })
        )
        .min(1)
        .describe("Array of openings to create"),
    },
    async (args) => {
      try {
        const payload = {
          data: args.data.map((item) => ({
            mode: item.mode ?? "floor",
            ...(item.hostFloorId != null ? { hostFloorId: item.hostFloorId } : {}),
            ...(item.levelId != null ? { levelId: item.levelId } : {}),
            ...(item.baseLevelId != null ? { baseLevelId: item.baseLevelId } : {}),
            ...(item.topLevelId != null ? { topLevelId: item.topLevelId } : {}),
            ...(item.boundaryPoints != null
              ? { boundaryPoints: item.boundaryPoints }
              : {}),
            ...(item.rect != null ? { rect: item.rect } : {}),
            perpendicularFace: item.perpendicularFace ?? true,
          })),
        };

        const response = await withRevitConnection(async (revitClient) => {
          return await revitClient.sendCommand("create_floor_opening", payload);
        });

        return {
          content: [
            {
              type: "text",
              text: JSON.stringify(response ?? { success: false }),
            },
          ],
        };
      } catch (error) {
        return {
          content: [
            {
              type: "text",
              text: `Create floor opening failed: ${
                error instanceof Error ? error.message : String(error)
              }`,
            },
          ],
        };
      }
    }
  );
}
