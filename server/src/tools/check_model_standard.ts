import fs from "fs";
import path from "path";
import { fileURLToPath } from "url";
import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";
import { paginateRows } from "../utils/responseTrimming.js";
import {
  evaluateStandard,
  summarizeFindings,
  type RawModelFacts,
  type StandardConfig,
} from "../quality/standardRules.js";

const __dirname = path.dirname(fileURLToPath(import.meta.url));

/**
 * Where an organization's own standard lives when no `configPath` is given —
 * one file per deployed copy of this server, edited once per office. Missing
 * is not an error: `check_model_standard` still runs the structural checks
 * that need no naming convention at all (REV-179's "разумный набор по умолчанию").
 */
function defaultConfigPath(): string {
  return path.resolve(__dirname, "../../model-standard.config.json");
}

function loadConfig(configPath: string | undefined): { config: StandardConfig; configSource: string } {
  const resolvedPath = configPath ? path.resolve(configPath) : defaultConfigPath();
  try {
    const raw = fs.readFileSync(resolvedPath, "utf-8");
    return { config: JSON.parse(raw) as StandardConfig, configSource: resolvedPath };
  } catch {
    return { config: {}, configSource: "(default — no config file found)" };
  }
}

export function registerCheckModelStandardTool(server: McpServer) {
  server.tool(
    "check_model_standard",
    "Audit the model against the organization's own BIM standard — the checks currently done by " +
      "eye on acceptance: type/family names against a naming pattern, elements sitting without a " +
      "level, a category split across worksets in a way that looks like a mistake, near-duplicate " +
      "type names («Дверь 900» / «дверь_900»), loaded-but-never-placed types, empty groups, and " +
      "links that are not loaded. Findings come back graded критично / поправить / на усмотрение, " +
      "most severe first. The naming rules are entirely config-driven — without a config file this " +
      "runs only the structural checks (level, workset, duplicates, unused types, groups, links), " +
      "since there is no organization-wide default for what a type name should look like. Point " +
      "`configPath` at a JSON file shaped like StandardConfig in server/src/quality/standardRules.ts " +
      "to add naming patterns and tune the rest; omitting it looks for server/model-standard.config.json.",
    {
      configPath: z
        .string()
        .optional()
        .describe(
          "Path to a JSON config file (StandardConfig shape). Omit to use " +
            "server/model-standard.config.json if it exists, else the structural-only default."
        ),
      offset: z
        .number()
        .int()
        .min(0)
        .optional()
        .default(0)
        .describe("Findings to skip. Use findingsPagination.nextOffset from the previous call."),
      limit: z
        .number()
        .int()
        .min(1)
        .max(200)
        .optional()
        .default(50)
        .describe("Max findings per call (default 50). The summary counts always describe the whole audit."),
    },
    async (args) => {
      try {
        const { config, configSource } = loadConfig(args.configPath);

        const facts = (await withRevitConnection(async (revitClient) => {
          return await revitClient.sendCommand("check_model_standard", {});
        })) as RawModelFacts;

        const findings = evaluateStandard(facts, config);
        const summary = summarizeFindings(findings);

        const trimmed = paginateRows(
          { findings },
          {
            key: "findings",
            offset: args.offset ?? 0,
            limit: args.limit ?? 50,
            fields: ["all"],
          }
        );

        return {
          content: [
            {
              type: "text" as const,
              text: JSON.stringify({ configSource, summary, ...(trimmed as object) }),
            },
          ],
        };
      } catch (error) {
        return {
          content: [
            {
              type: "text" as const,
              text: `check_model_standard не выполнен: ${
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
