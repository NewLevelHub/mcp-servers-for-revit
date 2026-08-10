import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";

export function registerCreatePointBasedElementTool(server: McpServer) {
  server.tool(
    "create_point_based_element",
    "Create one or more point-based elements in Revit such as doors, windows, or furniture. " +
      "Requires typeId from get_available_family_types. Doors/windows also require hostWallId " +
      "(no silent nearest-wall snap). width/height are informational — size comes from the family type. Units: mm.",
    {
      data: z
        .array(
          z.object({
            name: z
              .string()
              .describe("Description of the element (e.g., door, window)"),
            typeId: z
              .number()
              .describe(
                "Required. FamilySymbol ElementId from get_available_family_types. Missing or invalid typeId fails (no FirstOrDefault fallback)."
              ),
            locationPoint: z
              .object({
                x: z.number().describe("X coordinate"),
                y: z.number().describe("Y coordinate"),
                z: z.number().describe("Z coordinate"),
              })
              .describe(
                "The position coordinates where the element will be placed"
              ),
            width: z
              .number()
              .describe(
                "Informational only — opening width comes from typeId (family type), not this field."
              ),
            depth: z.number().optional().describe("Depth of the element in mm"),
            height: z
              .number()
              .describe(
                "Informational only for doors/windows — height comes from typeId. For windows, baseOffset maps to sill height."
              ),
            baseLevel: z.number().describe("Base level height"),
            baseOffset: z.number().describe("Offset from the base level (sill height for windows)"),
            rotation: z
              .number()
              .optional()
              .describe("Rotation angle in degrees (0-360), non-hosted elements only"),
            hostWallId: z
              .number()
              .optional()
              .describe(
                "Required for doors/windows: ElementId of the host wall. " +
                  "Without it the command fails (no auto nearest-wall snap). Optional for non-hosted families."
              ),
            facingFlipped: z
              .boolean()
              .optional()
              .default(false)
              .describe(
                "Whether to flip the facing direction of the door/window. " +
                  "When true, the element faces the opposite side of the wall."
              ),
            handFlipped: z
              .boolean()
              .optional()
              .default(false)
              .describe(
                "Force a door hand (hinge side) flip. A door has four states — facing × hand — " +
                  "so setting facing alone can still leave the swing mirrored. " +
                  "Prefer handHintPoint, which lets Revit decide from the real orientation."
              ),
            handHintPoint: z
              .object({
                x: z.number(),
                y: z.number(),
                z: z.number(),
              })
              .optional()
              .describe(
                "A point on the hinge side of the opening, along the wall. Revit compares it " +
                  "with the placed door's HandOrientation and flips the hand when mirrored. " +
                  "Family-convention agnostic — use this instead of guessing handFlipped."
              ),
            strictLocation: z
              .boolean()
              .optional()
              .default(false)
              .describe(
                "Place exactly at locationPoint: disables end clamping, junction nudging, " +
                  "batch auto-spacing and host re-resolution. An opening that cannot be honored " +
                  "fails with an error instead of being silently moved. Use for CAD redraw, " +
                  "where the caller already knows the exact position."
              ),
            strictToleranceMm: z
              .number()
              .optional()
              .default(50)
              .describe(
                "Max mm the placement may drift from locationPoint in strict mode before the " +
                  "item is rejected and removed (default 50)."
              ),
          })
        )
        .describe("Array of point-based elements to create"),
    },
    async (args, extra) => {
      const params = args;

      try {
        const response = await withRevitConnection(async (revitClient) => {
          return await revitClient.sendCommand(
            "create_point_based_element",
            params
          );
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
              text: `Create point-based element failed: ${
                error instanceof Error ? error.message : String(error)
              }`,
            },
          ],
        };
      }
    }
  );
}
