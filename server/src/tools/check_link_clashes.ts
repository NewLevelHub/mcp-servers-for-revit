import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";
import { paginateRows } from "../utils/responseTrimming.js";

export function registerCheckLinkClashesTool(server: McpServer) {
  server.tool(
    "check_link_clashes",
    "Find where the open model runs into its Revit links — «балка режет проём», «труба в стене», " +
      "«воздуховод съел высоту в коридоре». Each row names both sides with their element ids, the " +
      "category and type, the level and the room, the point in OUR coordinates, and how deep the two " +
      "went into each other (mm) — so operate_element can highlight the host element straight from the " +
      "report. Above the rows, byCategoryPair folds the whole run into «Балки ↔ Проёмы — 12» instead of " +
      "300 lines to skim. Defaults compare our walls/floors/ceilings/roofs/doors/windows/stairs against " +
      "their framing, columns, foundations, ducts, pipes, cable trays and conduits; both sides are " +
      "overridable. STRICTLY READ-ONLY: no transaction here and nothing written into a link. " +
      "It is not a Navisworks clash regime — it answers «я поменял планировку, что теперь бьётся». " +
      "To see what is linked in at all, and whether a link is loaded, use get_linked_models first.",
    {
      linkNameFilter: z
        .string()
        .optional()
        .describe(
          "Check only links whose file name contains this text (case-insensitive), e.g. «КР» or «ОВ». " +
            "Omit to check every loaded link. Links that are unloaded or missing are reported in `links` " +
            "with a note rather than silently skipped."
        ),
      hostCategories: z
        .array(z.string())
        .optional()
        .describe(
          "Categories of OUR model to test. Names in either UI language or OST_ enum names. " +
            "Default: стены, перекрытия, потолки, крыши, двери, окна, лестницы."
        ),
      linkCategories: z
        .array(z.string())
        .optional()
        .describe(
          "Categories inside the LINK to test against. Default: несущие конструкции (балки, колонны, " +
            "фундаменты) plus воздуховоды, трубы, лотки, короба and оборудование ОВ. Narrow it when the " +
            "question is narrow — «только балки» costs one argument and a fraction of the time."
        ),
      toleranceMm: z
        .number()
        .min(0)
        .max(500)
        .optional()
        .default(5)
        .describe(
          "Overlaps thinner than this are treated as touching and dropped (default 5 mm); " +
            "ignoredBelowTolerance reports how many. Raise it on a model full of small modelling slips, " +
            "lower it to 0 to see literally every contact."
        ),
      levelName: z
        .string()
        .optional()
        .describe(
          "Limit the OUR-side elements to this level — the fast way to ask «что бьётся на 3 этаже». " +
            "The link side is never limited by level: a link names its floors its own way, and the beam " +
            "hitting our floor may well sit on theirs."
        ),
      includeRooms: z
        .boolean()
        .optional()
        .default(true)
        .describe(
          "Name the room each clash falls in (default true). Costs one point-in-room lookup per clash; " +
            "set false on a model with no room volumes computed."
        ),
      maxClashes: z
        .number()
        .int()
        .min(1)
        .max(5000)
        .optional()
        .default(500)
        .describe(
          "Stop the scan after this many clashes (default 500). Hitting it sets truncated: what came " +
            "back is a part of the model, not all of it."
        ),
      maxHostElements: z
        .number()
        .int()
        .min(1)
        .max(100000)
        .optional()
        .default(50000)
        .describe(
          "Stop after putting this many elements of OUR model through the test (default 50000). " +
            "This is a runaway guard, not the working limit — timeBudgetSeconds is what normally ends " +
            "a long scan. hostElementsScanned reports how many were actually reached."
        ),
      timeBudgetSeconds: z
        .number()
        .int()
        .min(5)
        .max(150)
        .optional()
        .default(90)
        .describe(
          "Wall-clock budget for the scan (default 90 s, max 150). The scan returns what it found so " +
            "far and sets truncated rather than timing out with nothing."
        ),
      offset: z
        .number()
        .int()
        .min(0)
        .optional()
        .default(0)
        .describe("Clashes to skip. Use clashesPagination.nextOffset from the previous call."),
      limit: z
        .number()
        .int()
        .min(1)
        .max(200)
        .optional()
        .default(50)
        .describe(
          "Max clashes per call (default 50). totalClashes and byCategoryPair still describe the whole " +
            "run, so the summary is never a summary of one page."
        ),
    },
    async (args) => {
      try {
        const response = await withRevitConnection(async (revitClient) => {
          return await revitClient.sendCommand("check_link_clashes", {
            toleranceMm: args.toleranceMm ?? 5,
            includeRooms: args.includeRooms ?? true,
            maxClashes: args.maxClashes ?? 500,
            maxHostElements: args.maxHostElements ?? 50000,
            timeBudgetSeconds: args.timeBudgetSeconds ?? 90,
            ...(args.linkNameFilter ? { linkNameFilter: args.linkNameFilter } : {}),
            ...(args.hostCategories?.length ? { hostCategories: args.hostCategories } : {}),
            ...(args.linkCategories?.length ? { linkCategories: args.linkCategories } : {}),
            ...(args.levelName ? { levelName: args.levelName } : {}),
          });
        });

        // Hundreds of clashes against a real ИОС file is the normal case, not the bad
        // one. The plugin already sorted them deepest first, so page one is the page
        // worth arguing about.
        const trimmed = paginateRows(response, {
          key: "clashes",
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
              text: `check_link_clashes не выполнен: ${
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
