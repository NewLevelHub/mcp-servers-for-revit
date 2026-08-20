import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";
import { paginateRows } from "../utils/responseTrimming.js";

export function registerCreateMepOpeningsTool(server: McpServer) {
  server.tool(
    "create_mep_openings",
    "Build the задание на отверстия: where the pipes, ducts, trays and conduits of an ИОС link pass " +
      "through our walls and slabs, how big a hole each needs, and — once confirmed — the holes " +
      "themselves. The size is measured, not guessed: the overlap body is projected into the plane of " +
      "the wall (or the plan of the slab), so a pipe crossing at an angle gets the wider hole it really " +
      "needs. A bundle of pipes running side by side becomes ONE opening rather than five holes with " +
      "fins of masonry between them. Each row carries a марка, the size, the отметка низа above the " +
      "level, the room, and the ids of both our element and the engineering runs it is cut for. " +
      "PREVIEW BY DEFAULT: apply=false returns the plan and touches nothing; call again with apply=true " +
      "after the architect has read it. A re-run does not duplicate — openings already in the model come " +
      "back as status 'exists'. Use check_link_clashes first to see what is hitting what; this tool is " +
      "the answer for the МЕР runs that are supposed to pass through.",
    {
      apply: z
        .boolean()
        .optional()
        .default(false)
        .describe(
          "false (default) = preview only, nothing is written; true = cut the openings after the " +
            "architect confirmed the plan. Never pass true on the first call."
        ),
      linkNameFilter: z
        .string()
        .optional()
        .describe(
          "Only links whose file name contains this text, e.g. «ОВ» or «ВК». Omit for every loaded " +
            "link. Unloaded links are reported with a note rather than skipped in silence."
        ),
      levelName: z
        .string()
        .optional()
        .describe("Limit our side to this level — the usual way to work through a building floor by floor."),
      mepCategories: z
        .array(z.string())
        .optional()
        .describe(
          "Categories inside the link to cut for. Default: трубы, гибкие трубы, воздуховоды, гибкие " +
            "воздуховоды, лотки, короба. Fittings and equipment are out on purpose — a bend inside a " +
            "wall is a coordination argument, not a hole to order."
        ),
      clearanceMm: z
        .number()
        .min(0)
        .max(500)
        .optional()
        .default(50)
        .describe(
          "Free space left around the run on every side (default 50 mm). A hole cut exactly to the " +
            "pipe cannot be built — the sleeve and the seal need room."
        ),
      mergeGapMm: z
        .number()
        .min(0)
        .max(2000)
        .optional()
        .default(200)
        .describe(
          "Two openings closer than this become one (default 200 mm). Raise it to group a wide bundle, " +
            "drop it to 0 to get one opening per run."
        ),
      sizeStepMm: z
        .number()
        .min(0)
        .max(500)
        .optional()
        .default(50)
        .describe(
          "Sizes are rounded up to this step (default 50 mm) — nobody cuts a 137 mm hole. Pass 0 for " +
            "the measured size to the millimetre."
        ),
      openingTypeId: z
        .number()
        .int()
        .positive()
        .optional()
        .describe(
          "FamilySymbol of the opening family to place in walls (get_available_family_types). The tool " +
            "duplicates it to the exact size of each hole. Without it walls get a plain Revit opening, " +
            "which carries no марка and will not appear in a ведомость — the preview says so."
        ),
      maxOpenings: z
        .number()
        .int()
        .min(1)
        .max(2000)
        .optional()
        .default(200)
        .describe("Stop after this many openings (default 200). Hitting it sets truncated."),
      timeBudgetSeconds: z
        .number()
        .int()
        .min(5)
        .max(150)
        .optional()
        .default(90)
        .describe("Wall-clock budget for the measuring pass (default 90 s, max 150)."),
      offset: z
        .number()
        .int()
        .min(0)
        .optional()
        .default(0)
        .describe("Openings to skip. Use openingsPagination.nextOffset from the previous call."),
      limit: z
        .number()
        .int()
        .min(1)
        .max(200)
        .optional()
        .default(50)
        .describe(
          "Max openings per call (default 50). totalOpenings still describes the whole задание, so the " +
            "summary is never a summary of one page."
        ),
    },
    async (args) => {
      try {
        const response = await withRevitConnection(async (revitClient) => {
          return await revitClient.sendCommand("create_mep_openings", {
            apply: args.apply ?? false,
            clearanceMm: args.clearanceMm ?? 50,
            mergeGapMm: args.mergeGapMm ?? 200,
            sizeStepMm: args.sizeStepMm ?? 50,
            maxOpenings: args.maxOpenings ?? 200,
            timeBudgetSeconds: args.timeBudgetSeconds ?? 90,
            ...(args.linkNameFilter ? { linkNameFilter: args.linkNameFilter } : {}),
            ...(args.levelName ? { levelName: args.levelName } : {}),
            ...(args.mepCategories?.length ? { mepCategories: args.mepCategories } : {}),
            ...(args.openingTypeId ? { openingTypeId: args.openingTypeId } : {}),
          });
        });

        const trimmed = paginateRows(response, {
          key: "openings",
          offset: args.offset ?? 0,
          limit: args.limit ?? 50,
          fields: ["all"],
        });

        return {
          content: [{ type: "text" as const, text: JSON.stringify(trimmed) }],
        };
      } catch (error) {
        return {
          content: [
            {
              type: "text" as const,
              text: `create_mep_openings не выполнен: ${
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
