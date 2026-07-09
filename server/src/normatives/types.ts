import { z } from "zod";

/** Supported engineering units for normative checks (check_* commands). */
export const normativeUnitSchema = z.enum([
  "mm",
  "m",
  "m2",
  "m3",
  "percent",
  "pcs",
  "none",
]);

export type NormativeUnit = z.infer<typeof normativeUnitSchema>;

/**
 * Rule comparison semantics reused by check_* commands.
 * - min_value / max_value / exact_value / range — numeric limits
 * - requirement / prohibition / note — qualitative rules
 */
export const normativeRuleTypeSchema = z.enum([
  "min_value",
  "max_value",
  "exact_value",
  "range",
  "requirement",
  "prohibition",
  "note",
]);

export type NormativeRuleType = z.infer<typeof normativeRuleTypeSchema>;

/**
 * How normative text was obtained. OCR can be plugged in later without
 * changing NormativeRule shape — only metadata.mode / confidence differ.
 */
export const normativeTextExtractionModeSchema = z.enum([
  "embedded-text",
  "ocr",
]);

export type NormativeTextExtractionMode = z.infer<
  typeof normativeTextExtractionModeSchema
>;

export const normativeSourceRefSchema = z.object({
  document: z.string().describe("Document code or title, e.g. СП РК 3.02-101"),
  clause: z.string().describe("Clause reference, e.g. 4.2.3 or п. 4.2.3"),
  quote: z.string().describe("Original normative sentence or fragment"),
  page: z.number().int().positive().optional(),
});

export type NormativeSourceRef = z.infer<typeof normativeSourceRefSchema>;

export const normativeApplicabilitySchema = z.object({
  raw: z.string().describe("Original applicability phrase from the norm"),
  buildingType: z.string().optional(),
  roomType: z.string().optional(),
  conditions: z.array(z.string()).optional(),
});

export type NormativeApplicability = z.infer<typeof normativeApplicabilitySchema>;

export const normativeNumericRangeSchema = z.object({
  min: z.number().optional(),
  max: z.number().optional(),
  exact: z.number().optional(),
});

export type NormativeNumericRange = z.infer<typeof normativeNumericRangeSchema>;

export const normativeRuleValueSchema = z.union([
  z.number(),
  z.string(),
  normativeNumericRangeSchema,
]);

export type NormativeRuleValue = z.infer<typeof normativeRuleValueSchema>;

/**
 * Structured normative requirement extracted from chat-attached PDF text.
 * This is the stable contract for check_* tools and rulesStore (REV-30).
 */
export const normativeRuleSchema = z.object({
  id: z.string(),
  type: normativeRuleTypeSchema,
  object: z.string().describe("What the rule applies to, e.g. коридор, дверь"),
  value: normativeRuleValueSchema,
  unit: normativeUnitSchema,
  applicability: normativeApplicabilitySchema.nullable(),
  source: normativeSourceRefSchema,
  /**
   * Values normalized for Revit geometry checks:
   * lengths → mm, areas → m².
   */
  normalized: normativeNumericRangeSchema.optional(),
});

export type NormativeRule = z.infer<typeof normativeRuleSchema>;

export const normativeExtractionMetadataSchema = z.object({
  mode: normativeTextExtractionModeSchema,
  confidence: z.number().min(0).max(1).optional(),
  extractedAt: z.string().datetime(),
});

export type NormativeExtractionMetadata = z.infer<
  typeof normativeExtractionMetadataSchema
>;

export const normativeExtractionInputSchema = z.object({
  /** Plain text already extracted from PDF by Cursor (no server-side PDF parsing). */
  text: z.string().min(1),
  /** Optional document hint, e.g. ГОСТ 21.101-97 */
  document: z.string().optional(),
  /** Optional clause hint when user selected a specific paragraph */
  clauseHint: z.string().optional(),
  page: z.number().int().positive().optional(),
  metadata: normativeExtractionMetadataSchema.partial().optional(),
});

export type NormativeExtractionInput = z.infer<
  typeof normativeExtractionInputSchema
>;

export const normativeExtractionResultSchema = z.object({
  rules: z.array(normativeRuleSchema),
  warnings: z.array(z.string()),
  metadata: normativeExtractionMetadataSchema,
});

export type NormativeExtractionResult = z.infer<
  typeof normativeExtractionResultSchema
>;
