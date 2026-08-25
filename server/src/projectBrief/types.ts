import { z } from "zod";

/**
 * REV-182: project brief (ТЗ / задание на проектирование / протокол совещания)
 * requirements — the same idea as server/src/normatives/types.ts, but the
 * source document is free-form project prose, not a numbered normative code.
 * Kept as its own module rather than reusing NormativeRule: a brief requirement
 * answers "what does THIS project's client want" (room programme, counts),
 * not "what does the law require" — mixing the two into one table would make
 * check_against_brief and check_room_norms silently compete over the same rows.
 */

export const briefRequirementUnitSchema = z.enum(["pcs", "m2", "none"]);
export type BriefRequirementUnit = z.infer<typeof briefRequirementUnitSchema>;

/**
 * room_count / room_area_min are the two types check_against_brief can compare
 * against the model numerically. requirement/note are qualitative — surfaced by
 * query_project_brief with their quote, but nothing to compute against.
 */
export const briefRequirementTypeSchema = z.enum([
  "room_count",
  "room_area_min",
  "requirement",
  "note",
]);
export type BriefRequirementType = z.infer<typeof briefRequirementTypeSchema>;

export const briefSourceRefSchema = z.object({
  document: z.string().describe("Document title, e.g. ТЗ на проектирование ЖК «Сарыарка»"),
  clause: z.string().describe("Section/paragraph reference, if the document has one"),
  quote: z.string().describe("Original sentence or fragment the requirement was read from"),
  page: z.number().int().positive().optional(),
});
export type BriefSourceRef = z.infer<typeof briefSourceRefSchema>;

export const briefRequirementSchema = z.object({
  id: z.string(),
  type: briefRequirementTypeSchema,
  object: z.string().describe("What the requirement is about, e.g. студия, кладовая"),
  value: z.union([z.number(), z.string()]),
  unit: briefRequirementUnitSchema,
  source: briefSourceRefSchema,
});
export type BriefRequirement = z.infer<typeof briefRequirementSchema>;

export const briefExtractionInputSchema = z.object({
  text: z.string().min(1),
  document: z.string().optional(),
  clauseHint: z.string().optional(),
  page: z.number().int().positive().optional(),
});
export type BriefExtractionInput = z.infer<typeof briefExtractionInputSchema>;

export const briefExtractionResultSchema = z.object({
  requirements: z.array(briefRequirementSchema),
  warnings: z.array(z.string()),
});
export type BriefExtractionResult = z.infer<typeof briefExtractionResultSchema>;
