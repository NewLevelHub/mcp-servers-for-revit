import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import db from "../database/db.js";
import {
  resolveStairRiserTreadLimitsFromLibrary,
  resolveStairWidthLimitFromLibrary,
} from "../normatives/normAudit/resolveVerticalCirculation.js";
import type { NormAuditSource } from "../normatives/normAudit/types.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";
import {
  resolveStairAuthoringDefaults,
  type StairAuthoringInput,
} from "../normatives/authoring/stairRailingDefaults.js";

const pointMm = z.object({
  x: z.number().describe("X in mm"),
  y: z.number().describe("Y in mm"),
  z: z
    .number()
    .optional()
    .describe("Z in mm (overridden to base/run elevation in Revit)"),
});

const shaftRectMm = z.object({
  origin: pointMm.describe("SW corner before rotation (mm)"),
  widthMm: z.number().positive().describe("Shaft clear width mm (two runs side-by-side)"),
  depthMm: z.number().positive().describe("Shaft clear depth mm (run + landing)"),
  rotationDeg: z
    .number()
    .optional()
    .describe("CCW rotation of shaft around origin (default 0)"),
});

/**
 * REV-83+ — create stairs: straight, L (Г), U (П) with normative width.
 * Prefer shaftRect / mirrorElementId so U-stairs stay compact like typical-floor cells.
 */
export function registerCreateStairTool(server: McpServer) {
  server.tool(
    "create_stair",
    "Create a stair between two levels (REV-83+). Layouts: " +
      "straight, L/Г, U/П (residential shaft). " +
      "Preferred for U like typical floors: pass shaftRect (cell clear size) OR " +
      "mirrorElementId (copy plan bbox of a correct stair on another level). " +
      "fitMode: clamp (default, stay in cell) | extend | strict. " +
      "Requires typeId (StairsType). widthMm from norms if omitted. Units mm.",
    {
      data: z
        .array(
          z
            .object({
              typeId: z
                .number()
                .int()
                .positive()
                .describe(
                  "Required StairsType ElementId from get_available_family_types. Missing/invalid fails."
                ),
              baseLevelId: z.number().int().positive().describe("Base Level ElementId"),
              topLevelId: z
                .number()
                .int()
                .positive()
                .describe("Top Level ElementId (must be above base)"),
              layout: z
                .enum(["straight", "L", "U", "g", "п", "Г", "П"])
                .optional()
                .default("straight")
                .describe(
                  "straight | L (Г-образная) | U (П-образная). Aliases: g/Г→L, п/П→U."
                ),
              startPoint: pointMm
                .optional()
                .describe(
                  "Start of first run centerline (mm). Optional if shaftRect or mirrorElementId set."
                ),
              endPoint: pointMm
                .optional()
                .describe(
                  "Required for straight. For L/U: optional direction hint (length ignored)."
                ),
              bearingDeg: z
                .number()
                .optional()
                .describe(
                  "First-run bearing for L/U: 0=+X/east, 90=+Y/north (CCW). Use if endPoint omitted."
                ),
              turn: z
                .enum(["left", "right"])
                .optional()
                .default("right")
                .describe("Turn when ascending for L/U (default right)"),
              landingDepthMm: z
                .number()
                .positive()
                .optional()
                .describe("Landing depth mm (default = widthMm)"),
              firstRunLengthMm: z
                .number()
                .positive()
                .optional()
                .describe("Override first run length mm (else from riser/tread / shaft fit)"),
              secondRunLengthMm: z
                .number()
                .positive()
                .optional()
                .describe("Override second run length mm for L/U"),
              widthMm: z
                .number()
                .positive()
                .optional()
                .describe(
                  "Run width mm. If omitted, resolved from norm library (ширина марша)."
                ),
              riserHeightMm: z
                .number()
                .positive()
                .optional()
                .describe("Desired riser mm (used to split L/U risers; type still applies)"),
              treadDepthMm: z
                .number()
                .positive()
                .optional()
                .describe("Desired tread mm (used to size L/U run paths)"),
              shaftRect: shaftRectMm
                .optional()
                .describe(
                  "Fit U/L into this clear cell (mm). Prefer over free startPoint for compact typical-floor layout."
                ),
              mirrorElementId: z
                .number()
                .int()
                .positive()
                .optional()
                .describe(
                  "Existing Stairs id — use its plan bbox as shaft (stack under a correct reference stair)."
                ),
              fitMode: z
                .enum(["clamp", "extend", "strict"])
                .optional()
                .default("clamp")
                .describe(
                  "clamp=stay in shaft (default); extend=allow longer than shaft; strict=fail if ideal run > shaft"
                ),
            })
            .superRefine((item, ctx) => {
              const layout = item.layout ?? "straight";
              const hasShaft = item.shaftRect != null || item.mirrorElementId != null;
              if (layout === "straight" && item.startPoint == null) {
                ctx.addIssue({
                  code: z.ZodIssueCode.custom,
                  message: "layout=straight requires startPoint (+ endPoint).",
                });
              }
              if (
                (layout === "L" ||
                  layout === "U" ||
                  layout === "g" ||
                  layout === "п" ||
                  layout === "Г" ||
                  layout === "П") &&
                item.startPoint == null &&
                !hasShaft
              ) {
                ctx.addIssue({
                  code: z.ZodIssueCode.custom,
                  message:
                    "L/U requires startPoint+bearingDeg, or shaftRect / mirrorElementId.",
                });
              }
            })
        )
        .min(1)
        .describe("Array of stairs to create"),
    },
    async (args) => {
      try {
        const resolvedItems: Array<
          StairAuthoringInput & {
            widthMm: number;
            normSource?: NormAuditSource;
            warnings: string[];
            shaftRect?: z.infer<typeof shaftRectMm>;
            mirrorElementId?: number;
            fitMode?: string;
          }
        > = [];

        for (const item of args.data) {
          const resolved = resolveStairAuthoringDefaults(db, item, {
            resolveWidth: resolveStairWidthLimitFromLibrary,
            resolveRiserTread: resolveStairRiserTreadLimitsFromLibrary,
          });
          if (!resolved.ok) {
            return {
              content: [
                {
                  type: "text",
                  text: JSON.stringify(
                    { success: false, message: resolved.error },
                    null,
                    2
                  ),
                },
              ],
            };
          }
          resolvedItems.push({
            ...resolved.value,
            ...(item.shaftRect != null ? { shaftRect: item.shaftRect } : {}),
            ...(item.mirrorElementId != null
              ? { mirrorElementId: item.mirrorElementId }
              : {}),
            ...(item.fitMode != null ? { fitMode: item.fitMode } : {}),
          });
        }

        const payload = {
          data: resolvedItems.map((item) => ({
            typeId: item.typeId,
            baseLevelId: item.baseLevelId,
            topLevelId: item.topLevelId,
            layout: item.layout ?? "straight",
            ...(item.startPoint != null ? { startPoint: item.startPoint } : {}),
            ...(item.endPoint != null ? { endPoint: item.endPoint } : {}),
            ...(item.bearingDeg != null ? { bearingDeg: item.bearingDeg } : {}),
            ...(item.turn != null ? { turn: item.turn } : {}),
            ...(item.landingDepthMm != null
              ? { landingDepthMm: item.landingDepthMm }
              : {}),
            ...(item.firstRunLengthMm != null
              ? { firstRunLengthMm: item.firstRunLengthMm }
              : {}),
            ...(item.secondRunLengthMm != null
              ? { secondRunLengthMm: item.secondRunLengthMm }
              : {}),
            widthMm: item.widthMm,
            ...(item.riserHeightMm != null
              ? { riserHeightMm: item.riserHeightMm }
              : {}),
            ...(item.treadDepthMm != null
              ? { treadDepthMm: item.treadDepthMm }
              : {}),
            ...(item.shaftRect != null ? { shaftRect: item.shaftRect } : {}),
            ...(item.mirrorElementId != null
              ? { mirrorElementId: item.mirrorElementId }
              : {}),
            ...(item.fitMode != null ? { fitMode: item.fitMode } : {}),
          })),
        };

        const response = await withRevitConnection(async (revitClient) => {
          return await revitClient.sendCommand("create_stair", payload);
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
                      layout: item.layout ?? "straight",
                      widthMm: item.widthMm,
                      normSource: item.normSource,
                      warnings: item.warnings,
                      riserHeightMm: item.riserHeightMm,
                      treadDepthMm: item.treadDepthMm,
                      fitMode: item.fitMode ?? "clamp",
                      mirrorElementId: item.mirrorElementId,
                      shaftRect: item.shaftRect,
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
              text: `Create stair failed: ${
                error instanceof Error ? error.message : String(error)
              }`,
            },
          ],
        };
      }
    }
  );
}
