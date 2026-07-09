import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { z } from "zod";
import { isAbsolute, resolve } from "node:path";
import { extractRulesFromPdfFile } from "../normatives/extractRulesFromPdf.js";

function resolvePdfPath(inputPath: string): string {
  if (isAbsolute(inputPath)) return inputPath;

  // Server cwd is usually <repo>/server, so relative paths can target
  // files inside repo, e.g. "../normatives/SP_RK_3.02-101-2012_27.04.2021.pdf".
  return resolve(process.cwd(), inputPath);
}

export function registerExtractNormRulesFromPdfTool(server: McpServer) {
  server.tool(
    "extract_norm_rules_from_pdf",
    "Read a text-based normative PDF and convert requirement text into structured rules: type, object, value, unit, applicability, and source clause/document.",
    {
      pdfPath: z
        .string()
        .min(1)
        .describe(
          "Absolute path to PDF or path relative to server cwd (usually <repo>/server)."
        ),
      document: z
        .string()
        .optional()
        .describe("Optional document title/code override, e.g. СП РК 3.02-101."),
      clauseHint: z
        .string()
        .optional()
        .describe("Optional clause hint if extracting from a selected paragraph."),
      page: z.number().int().positive().optional().describe("Optional source page."),
      maxPages: z
        .number()
        .int()
        .positive()
        .optional()
        .describe("Optional max pages to parse from PDF for faster extraction."),
      metadata: z
        .object({
          mode: z.enum(["embedded-text", "ocr"]).optional(),
          confidence: z.number().min(0).max(1).optional(),
          extractedAt: z.string().datetime().optional(),
        })
        .optional(),
    },
    async (args) => {
      try {
        const pdfPath = resolvePdfPath(args.pdfPath);
        const result = await extractRulesFromPdfFile({
          pdfPath,
          document: args.document,
          clauseHint: args.clauseHint,
          page: args.page,
          maxPages: args.maxPages,
          metadata: args.metadata,
        });

        return {
          content: [
            {
              type: "text",
              text: JSON.stringify(
                {
                  success: true,
                  pdfPath,
                  ruleCount: result.rules.length,
                  ...result,
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
              text: `extract_norm_rules_from_pdf failed: ${
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

