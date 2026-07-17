import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";

const mmPointSchema = z.object({
  x: z.number().describe("X in millimetres (project coordinates)"),
  y: z.number().describe("Y in millimetres (project coordinates)"),
  z: z.number().optional().describe("Z in millimetres (optional; plan uses view plane)"),
});

const noteSchema = z.object({
  text: z
    .string()
    .min(1)
    .describe(
      "Short remark text, e.g. «Лоджия: 1130 < 1400 мм · СП РК 3.02-101 п.4.6.5». Do not paste full norm quotes."
    ),
  locationMm: mmPointSchema
    .optional()
    .describe(
      "Text insertion point (mm). Omit when elementId is set — then the note is offset from the element centre."
    ),
  leaderToMm: mmPointSchema
    .optional()
    .describe(
      "Leader arrow end (mm). Omit with elementId to point at the element centre."
    ),
  elementId: z
    .number()
    .int()
    .optional()
    .describe(
      "Revit element id (room, door, …). Auto-places text near the element and draws a leader to it."
    ),
  textTypeName: z
    .string()
    .optional()
    .describe(
      "TextNoteType from the project (prefer ADSK_Замечания). Falls back to batch textTypeName / project default."
    ),
  widthMm: z
    .number()
    .positive()
    .optional()
    .describe("Text box width in mm (default ~80)."),
  offsetMm: z
    .number()
    .positive()
    .optional()
    .describe(
      "When auto-placing from elementId: distance from element centre to text (default 1500 mm)."
    ),
  leader: z
    .boolean()
    .optional()
    .default(true)
    .describe(
      "When true (default) and leaderToMm or elementId is set, draw a TextNote leader (выноска)."
    ),
});

const lineSchema = z.object({
  fromMm: mmPointSchema,
  toMm: mmPointSchema,
});

/**
 * Annotate → Text (+ optional leader / detail line) on the active floor plan.
 * REV-61 Phase 1. Types only from the project — never creates TextNoteType.
 */
export function registerCreateTextNotesTool(server: McpServer) {
  server.tool(
    "create_text_notes",
    "Place Annotate → Text notes on the active floor plan (or viewId), optionally with a leader (выноска) " +
      "to an element or explicit leaderToMm. Prefer text type ADSK_Замечания from the project template " +
      "(get_document_styles; text height from type, e.g. 2.5 mm). Coordinates in mm. " +
      "placement=outside (default): text за планом — nearer left edge → left margin, nearer right → right margin; " +
      "placement=near: offset beside the element. clearPrevious removes prior MCP-ANN notes. " +
      "For norm findings use annotate_norm_findings. Complementary to create_filled_regions.",
    {
      notes: z
        .array(noteSchema)
        .optional()
        .describe(
          "Text notes to create. Each needs text plus locationMm and/or elementId."
        ),
      lines: z
        .array(lineSchema)
        .optional()
        .describe(
          "Optional detail lines (Annotate → Detail Line) without text, fromMm → toMm in mm."
        ),
      clearPrevious: z
        .boolean()
        .optional()
        .default(true)
        .describe(
          "Delete existing TextNotes/detail curves on this view whose Comments start with commentTag (default MCP-ANN)."
        ),
      commentTag: z
        .string()
        .optional()
        .default("MCP-ANN")
        .describe("Written to Comments; used by clearPrevious."),
      textTypeName: z
        .string()
        .optional()
        .default("ADSK_Замечания")
        .describe(
          "Default TextNoteType name for notes that omit textTypeName. Must exist in the project (e.g. height 2.5 mm)."
        ),
      placement: z
        .enum(["outside", "near"])
        .optional()
        .default("outside")
        .describe(
          "'outside' (default): text за контуром плана — слева если элемент ближе к левому краю, справа если к правому. 'near': рядом с элементом."
        ),
      marginMm: z
        .number()
        .positive()
        .optional()
        .describe("Gap from plan+grids outline to the text column when placement=outside (default 4000 mm)."),
      paperWidthMm: z
        .number()
        .positive()
        .optional()
        .describe(
          "Text box width in PAPER mm (default 70). Converted with view.Scale — do not pass model mm."
        ),
      textSizeMm: z
        .number()
        .positive()
        .optional()
        .describe(
          "Sets TextNoteType TEXT_SIZE in paper mm (default 2.5). Keeps font/color from ADSK_Замечания (GOST Common, red)."
        ),
      viewId: z
        .number()
        .int()
        .optional()
        .describe("Target view element id. Omit to use the active view."),
    },
    async (args) => {
      try {
        const response = await withRevitConnection(async (revitClient) => {
          return await revitClient.sendCommand("create_text_notes", {
            notes: args.notes ?? [],
            lines: args.lines ?? [],
            clearPrevious: args.clearPrevious ?? true,
            commentTag: args.commentTag ?? "MCP-ANN",
            textTypeName: args.textTypeName ?? "ADSK_Замечания",
            placement: args.placement ?? "outside",
            marginMm: args.marginMm ?? 0,
            paperWidthMm: args.paperWidthMm ?? 0,
            textSizeMm: args.textSizeMm ?? 2.5,
            viewId: args.viewId ?? -1,
          });
        });

        return {
          content: [
            {
              type: "text",
              text: JSON.stringify(response, null, 2),
            },
          ],
        };
      } catch (error) {
        return {
          content: [
            {
              type: "text",
              text: `create_text_notes failed: ${
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
