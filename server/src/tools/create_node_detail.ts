import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";

const extraLayerSchema = z.object({
  name: z
    .string()
    .describe("Layer label, e.g. Звукоизоляция, Подложка, Выравнивающий слой, Пароизоляция."),
  thicknessMm: z.number().min(0).describe("Thickness in mm; 0 draws a single line."),
  target: z
    .enum(["floor", "wall"])
    .optional()
    .describe("Which assembly to add it to in junction mode. Default floor."),
  function: z
    .string()
    .optional()
    .describe("Layer function used to guess a hatch: Structure, Substrate, Insulation, Finish1, Finish2, Membrane."),
  fillPatternName: z.string().optional().describe("Explicit hatch pattern name."),
  insertAt: z
    .number()
    .int()
    .optional()
    .describe("0-based position in the build-up (0 = outermost/top). Omit to append at the end."),
});

export function registerCreateNodeDetailTool(server: McpServer) {
  server.tool(
    "create_node_detail",
    "Generate a construction node on a new drafting view from the real layered build-up of a wall/floor type (CompoundStructure): layer contours, material hatches, a dimension chain of thicknesses and labelled leaders. mode=junction draws a floor meeting a wall (needs wallTypeId and floorTypeId); mode=single draws one build-up in section. Ids may be a type id or the id of a placed wall/floor. Build-up layers the type itself does not carry (an extra screed, underlay, sound insulation) go in extraLayers — each one runs the full length of its assembly, so local pieces such as a skirting, edge tape or a bead of mastic cannot be placed this way; draw those with place_detail_component or create_detail_lines afterwards. This automates the drafting of a node whose design decision was already made — it does not choose the node or check it against norms.",
    {
      mode: z
        .enum(["junction", "single"])
        .optional()
        .describe("junction — floor meeting a wall (default); single — one build-up in section."),
      wallTypeId: z
        .number()
        .int()
        .optional()
        .describe("Wall type id, or the id of a placed wall whose type should be read."),
      floorTypeId: z
        .number()
        .int()
        .optional()
        .describe("Floor type id, or the id of a placed floor whose type should be read."),
      orientation: z
        .enum(["horizontal", "vertical"])
        .optional()
        .describe("single mode: vertical stacks layers left to right (a wall), horizontal top down (a floor)."),
      name: z.string().optional().describe("View name, e.g. «Узел 1. Примыкание пола к стене»."),
      scale: z.number().int().optional().describe("View scale denominator. Default 10 (1:10)."),
      lengthMm: z.number().optional().describe("Visible run of the assembly, mm. Default 600."),
      wallRunMm: z.number().optional().describe("How far the wall is drawn above the floor, mm. Default 500."),
      gapMm: z
        .number()
        .optional()
        .describe(
          "Expansion gap between the floor finish build-up and the wall, mm. Default 20. Applied to layers above the structural core."
        ),
      annotate: z.boolean().optional().describe("Draw the dimension chain and layer labels. Default true."),
      drawHatches: z.boolean().optional().describe("Fill layers with material hatches. Default true."),
      createMissingTypes: z
        .boolean()
        .optional()
        .describe("Duplicate a filled region type when a hatch pattern has none. Default true."),
      dimensionTypeName: z.string().optional().describe("Linear dimension type from the project."),
      textTypeName: z.string().optional().describe("Text note type from the project, e.g. ADSK_Замечания."),
      lineStyleName: z.string().optional().describe("Line style for the node outlines (OST_Lines subcategory)."),
      extraLayers: z
        .array(extraLayerSchema)
        .optional()
        .describe("Layers to draw that the model does not carry."),
      sheetNumber: z.string().optional().describe("Place the finished node on this existing sheet."),
      activateView: z.boolean().optional().describe("Switch the Revit UI to the node view. Default true."),
    },
    async (args) => {
      const params = {
        mode: args.mode ?? "junction",
        wallTypeId: args.wallTypeId ?? 0,
        floorTypeId: args.floorTypeId ?? 0,
        orientation: args.orientation ?? "",
        name: args.name ?? "",
        scale: args.scale ?? 10,
        lengthMm: args.lengthMm ?? 600,
        wallRunMm: args.wallRunMm ?? 500,
        gapMm: args.gapMm ?? 20,
        annotate: args.annotate ?? true,
        drawHatches: args.drawHatches ?? true,
        createMissingTypes: args.createMissingTypes ?? true,
        dimensionTypeName: args.dimensionTypeName ?? "",
        textTypeName: args.textTypeName ?? "",
        lineStyleName: args.lineStyleName ?? "",
        extraLayers: (args.extraLayers ?? []).map((layer) => ({
          name: layer.name,
          thicknessMm: layer.thicknessMm,
          target: layer.target ?? "floor",
          function: layer.function ?? "Finish1",
          fillPatternName: layer.fillPatternName ?? "",
          insertAt: layer.insertAt ?? -1,
        })),
        sheetNumber: args.sheetNumber ?? "",
        activateView: args.activateView ?? true,
      };

      try {
        const response = await withRevitConnection(async (revitClient) => {
          return await revitClient.sendCommand("create_node_detail", params);
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
              text: `Create node detail failed: ${
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
