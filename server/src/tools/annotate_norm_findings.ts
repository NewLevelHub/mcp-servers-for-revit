import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";
import { findingsToAnnotationNotes } from "../normatives/normAudit/formatFindingAnnotation.js";
import type { NormAuditFinding } from "../normatives/normAudit/types.js";

const findingSchema = z.object({
  checkType: z.string(),
  status: z.enum(["violation", "nearLimit", "compliant", "skipped"]),
  elementId: z.number().int(),
  uniqueId: z.string().optional(),
  name: z.string(),
  level: z.string().optional().default(""),
  metric: z.string().optional(),
  actualMm: z.number().nullable().optional(),
  requiredMm: z.number().nullable().optional(),
  deviationMm: z.number().nullable().optional(),
  note: z.string().optional(),
  source: z.object({
    document: z.string(),
    clause: z.string().optional().default(""),
    quote: z.string().optional().default(""),
    page: z.number().optional(),
  }),
});

/**
 * REV-61 Phase 2 — map run_norm_audit findings → create_text_notes with leaders.
 */
export function registerAnnotateNormFindingsTool(server: McpServer) {
  server.tool(
    "annotate_norm_findings",
    "Place short Text + leader annotations on the active plan for norm-audit findings " +
      "(from run_norm_audit). Text template: «{name}: {actual} < {required} · {document} {clause}» — " +
      "never pastes full quotes. Uses create_text_notes (ADSK_Замечания, placement=outside): " +
      "text за планом — слева если нарушение ближе к левому краю, справа если к правому. " +
      "After «покажи нарушения и подпиши»: run_norm_audit → create_filled_regions → annotate_norm_findings. " +
      "style=text_only skips leaders.",
    {
      findings: z
        .array(findingSchema)
        .min(1)
        .describe(
          "Findings array from run_norm_audit (typically violation + nearLimit)."
        ),
      style: z
        .enum(["leader", "text_only"])
        .optional()
        .default("leader")
        .describe(
          "'leader' (default) draws a leader to each element; 'text_only' places text near the element without a leader."
        ),
      textTypeName: z
        .string()
        .optional()
        .default("ADSK_Замечания")
        .describe("TextNoteType from the project template (height from type, e.g. 2.5 mm)."),
      clearPrevious: z
        .boolean()
        .optional()
        .default(true)
        .describe("Remove prior MCP-ANN annotations on the view before placing."),
      statuses: z
        .array(z.enum(["violation", "nearLimit", "compliant", "skipped"]))
        .optional()
        .describe(
          "Which finding statuses to annotate (default: violation + nearLimit)."
        ),
      offsetMm: z
        .number()
        .positive()
        .optional()
        .describe("Only for placement=near. For outside use marginMm."),
      marginMm: z
        .number()
        .positive()
        .optional()
        .describe("Gap from plan+grids to text column (default 4000 mm)."),
      viewId: z.number().int().optional(),
    },
    async (args) => {
      try {
        const findings = args.findings as NormAuditFinding[];
        const withLeader = (args.style ?? "leader") !== "text_only";
        const notes = findingsToAnnotationNotes(findings, {
          statuses: args.statuses as NormAuditFinding["status"][] | undefined,
          textTypeName: args.textTypeName ?? "ADSK_Замечания",
          offsetMm: args.offsetMm,
        }).map((n) => ({ ...n, leader: withLeader }));

        if (notes.length === 0) {
          return {
            content: [
              {
                type: "text",
                text: JSON.stringify(
                  {
                    success: false,
                    message:
                      "No findings matched the selected statuses (or missing elementId).",
                    annotatedCount: 0,
                  },
                  null,
                  2
                ),
              },
            ],
          };
        }

        const response = await withRevitConnection(async (revitClient) => {
          return await revitClient.sendCommand("create_text_notes", {
            notes,
            lines: [],
            clearPrevious: args.clearPrevious ?? true,
            commentTag: "MCP-ANN",
            textTypeName: args.textTypeName ?? "ADSK_Замечания",
            placement: "outside",
            marginMm: args.marginMm ?? 0,
            paperWidthMm: 70,
            textSizeMm: 2.5,
            viewId: args.viewId ?? -1,
          });
        });

        return {
          content: [
            {
              type: "text",
              text: JSON.stringify(
                {
                  annotatedCount: notes.length,
                  style: args.style ?? "leader",
                  result: response,
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
              text: `annotate_norm_findings failed: ${
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
