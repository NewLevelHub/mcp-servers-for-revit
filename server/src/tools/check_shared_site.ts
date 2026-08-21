import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";
import {
  type LinkPlacement,
  type SiteFinding,
  type SiteSurvey,
  buildSiteMessage,
  compareGrids,
  compareLevels,
  comparePlacement,
  comparePoints,
} from "../utils/siteComparison.js";

interface LinkRow {
  name?: string;
  section?: string;
  instanceId?: number;
  isReadable?: boolean;
  statusText?: string;
  note?: string;
  placement?: LinkPlacement;
  site?: SiteSurvey;
}

interface LinkedModelsReply {
  success?: boolean;
  message?: string;
  hostModel?: string;
  hostSite?: SiteSurvey;
  links?: LinkRow[];
}

export function registerCheckSharedSiteTool(server: McpServer) {
  server.tool(
    "check_shared_site",
    "Сверка общей площадки: does our model and each Revit link stand on the same setting-out? " +
      "Compares levels (names and elevations), grids (names and where they run), the project base " +
      "point and survey point, and how the link was inserted — origin-to-origin, rotated, mirrored " +
      "or nudged by hand. Each row names both numbers and the difference, so the answer needs no " +
      "second model open to be understood. A model that agrees gets one short sentence, not a wall " +
      "of text. This is the thirty-second check that catches the mistake nobody feels for a month " +
      "and then costs the whole стык: a КР floor 50 mm low, an axis with the right name in the wrong " +
      "place, a link somebody moved. STRICTLY READ-ONLY. Run it before check_link_clashes and " +
      "create_mep_openings — both trust the coordinates this one verifies.",
    {
      linkNameFilter: z
        .string()
        .optional()
        .describe(
          "Check only links whose file name contains this text, e.g. «КР» or «ОВ». Omit for every " +
            "loaded link; unloaded ones are reported with a note rather than skipped."
        ),
      checkLevels: z.boolean().optional().default(true).describe("Compare levels (default true)."),
      checkGrids: z.boolean().optional().default(true).describe("Compare grids (default true)."),
      checkPoints: z
        .boolean()
        .optional()
        .default(true)
        .describe("Compare the project base point, the survey point and the angle to true north."),
      levelToleranceMm: z
        .number()
        .min(0)
        .max(1000)
        .optional()
        .default(1)
        .describe(
          "Elevations closer than this are the same number (default 1 mm). Revit stores them in " +
            "feet, so an exact 3900 comes back as 3899.9999999999995."
        ),
      gridToleranceMm: z
        .number()
        .min(0)
        .max(1000)
        .optional()
        .default(5)
        .describe(
          "Grids and points closer than this are in the same place (default 5 mm) — hand-drawn " +
            "axes never land on the identical coordinate."
        ),
    },
    async (args) => {
      try {
        const checkLevels = args.checkLevels ?? true;
        const checkGrids = args.checkGrids ?? true;
        const checkPoints = args.checkPoints ?? true;

        const reply = (await withRevitConnection(async (revitClient) => {
          return await revitClient.sendCommand("get_linked_models", {
            includeElementCounts: false,
            includeCategories: false,
            coordinateSamples: 0,
            includeLevels: checkLevels,
            includeGrids: checkGrids,
            includeSitePoints: checkPoints,
            ...(args.linkNameFilter ? { nameFilter: args.linkNameFilter } : {}),
          });
        })) as LinkedModelsReply;

        const options = {
          levelToleranceMm: args.levelToleranceMm ?? 1,
          gridToleranceMm: args.gridToleranceMm ?? 5,
        };

        const checked = [
          ...(checkLevels ? ["уровни"] : []),
          ...(checkGrids ? ["оси"] : []),
          ...(checkPoints ? ["базовые точки"] : []),
        ];

        const hostSite = reply.hostSite ?? {};
        const links = reply.links ?? [];

        const results = links.map((link) => {
          const name = link.name ?? "связь";

          // An unloaded link is an answer, not a silence: сверка that quietly skips it
          // reads as «всё сходится» on the one file nobody checked.
          if (!link.isReadable) {
            return {
              link: name,
              section: link.section,
              checked: false,
              message: `Связь «${name}» не прочитана (${link.statusText ?? "недоступна"}) — сверить не с чем.`,
              findings: [] as SiteFinding[],
            };
          }

          const site = link.site ?? {};
          const findings: SiteFinding[] = [
            ...comparePlacement(link.placement),
            ...(checkLevels ? compareLevels(hostSite.levels, site.levels, options) : []),
            ...(checkGrids ? compareGrids(hostSite.grids, site.grids, options) : []),
            ...(checkPoints ? comparePoints(hostSite.points, site.points, options) : []),
          ];

          return {
            link: name,
            section: link.section,
            instanceId: link.instanceId,
            checked: true,
            message: buildSiteMessage(name, findings, checked),
            findings,
          };
        });

        const totalFindings = results.reduce((sum, row) => sum + row.findings.length, 0);
        const readable = results.filter((row) => row.checked).length;

        const summary =
          readable === 0
            ? "Ни одной загруженной связи — сверять не с чем."
            : totalFindings === 0
              ? `Расхождений нет ни в одной из ${readable} связей (сверено: ${checked.join(", ")}).`
              : `Расхождений: ${totalFindings} в ${readable} ${readable === 1 ? "связи" : "связях"}.`;

        return {
          content: [
            {
              type: "text" as const,
              text: JSON.stringify({
                success: true,
                message: summary,
                hostModel: reply.hostModel ?? "",
                checked,
                hostLevelCount: hostSite.levels?.length ?? 0,
                hostGridCount: hostSite.grids?.length ?? 0,
                totalFindings,
                links: results,
              }),
            },
          ],
        };
      } catch (error) {
        return {
          content: [
            {
              type: "text" as const,
              text: `check_shared_site не выполнен: ${
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
