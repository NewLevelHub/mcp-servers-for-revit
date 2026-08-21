import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";

const fieldWidthSchema = z.object({
  fieldIndex: z
    .number()
    .int()
    .optional()
    .default(-1)
    .describe("0-based column index. -1 = match by parameterName."),
  parameterName: z
    .string()
    .optional()
    .default("")
    .describe("Column name to match when fieldIndex is -1."),
  widthMm: z.number().positive().describe("New column width in mm."),
});

const scheduleFilterSchema = z.object({
  fieldName: z.string().describe("Parameter name of the field to filter by."),
  fieldIndex: z.number().optional().default(-1),
  filterType: z
    .enum([
      "Equal",
      "NotEqual",
      "GreaterThan",
      "GreaterThanOrEqual",
      "LessThan",
      "LessThanOrEqual",
      "Contains",
      "NotContains",
      "BeginsWith",
      "EndsWith",
    ])
    .optional()
    .default("Equal"),
  filterValue: z.string().optional().default(""),
  filterElementId: z
    .number()
    .optional()
    .describe("ElementId for element-based filters (e.g. Level id)."),
});

const scheduleSortSchema = z.object({
  fieldName: z.string(),
  fieldIndex: z.number().optional().default(-1),
  sortOrder: z.enum(["Ascending", "Descending"]).optional().default("Ascending"),
});

const scheduleGroupSchema = z.object({
  fieldName: z.string(),
  fieldIndex: z.number().optional().default(-1),
  sortOrder: z.enum(["Ascending", "Descending"]).optional().default("Ascending"),
  showHeader: z.boolean().optional().default(true),
  showFooter: z.boolean().optional().default(false),
  showBlankLine: z.boolean().optional().default(false),
});

export function registerConfigureScheduleTool(server: McpServer) {
  server.tool(
    "configure_schedule",
    "Edit an existing Revit ViewSchedule in-place (REV-68). Use to change column widths, hide or show columns, update filters/sorts/groups, and toggle display options (title, headers, grid lines). Does NOT add new columns — use create_schedule for that. Identify the schedule by scheduleId, scheduleUniqueId, or scheduleName (from get_schedule_definition).",
    {
      scheduleId: z
        .number()
        .int()
        .optional()
        .describe("Numeric element id of the schedule to edit."),
      scheduleUniqueId: z
        .string()
        .optional()
        .describe("UniqueId of the schedule to edit."),
      scheduleName: z
        .string()
        .optional()
        .describe("Schedule name when id / uniqueId are not available."),

      // Display options
      showTitle: z
        .boolean()
        .optional()
        .describe("Show/hide the schedule title row."),
      showHeaders: z
        .boolean()
        .optional()
        .describe("Show/hide column header row."),
      showGridLines: z.boolean().optional().describe("Show/hide grid lines."),
      isItemized: z
        .boolean()
        .optional()
        .describe(
          "true = itemized rows; false = collapsed (экспликация style with totals)."
        ),

      // Field mutations
      fieldWidths: z
        .array(fieldWidthSchema)
        .optional()
        .default([])
        .describe(
          "Column-width overrides. Applied to existing columns matched by fieldIndex or parameterName."
        ),
      hideFields: z
        .array(z.string())
        .optional()
        .default([])
        .describe("Column names to hide (IsHidden = true)."),
      showFields: z
        .array(z.string())
        .optional()
        .default([])
        .describe("Column names to show (IsHidden = false)."),

      // Filters
      clearExistingFilters: z
        .boolean()
        .optional()
        .default(false)
        .describe("Remove all existing filters before applying new ones."),
      filters: z.array(scheduleFilterSchema).optional().default([]),

      // Sorts / groups
      clearExistingSorts: z.boolean().optional().default(false),
      sortFields: z.array(scheduleSortSchema).optional().default([]),
      clearExistingGroups: z.boolean().optional().default(false),
      groupFields: z.array(scheduleGroupSchema).optional().default([]),
    },
    async (args) => {
      try {
        const response = await withRevitConnection(async (revitClient) => {
          return await revitClient.sendCommand("configure_schedule", args);
        });

        return {
          content: [{ type: "text", text: JSON.stringify(response) }],
        };
      } catch (error) {
        return {
          content: [
            {
              type: "text",
              text: `configure_schedule failed: ${
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
