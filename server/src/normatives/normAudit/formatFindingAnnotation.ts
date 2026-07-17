import type { NormAuditFinding } from "./types.js";

/**
 * Short plan annotation for a norm finding (REV-61).
 * Template: `{name}: {actual} < {required} · {document} {clause}`
 * Never copies the full source.quote.
 */
export function formatFindingAnnotation(finding: NormAuditFinding): string {
  const name = (finding.name || `id ${finding.elementId}`).trim();
  const doc = (finding.source?.document || "").trim();
  const clause = (finding.source?.clause || "").trim();
  const sourceBit = [doc, clause].filter(Boolean).join(" ").trim();

  const comparison = formatComparison(finding);
  const parts = [name];
  if (comparison) parts.push(comparison);
  else if (finding.note?.trim()) parts.push(finding.note.trim());
  else if (finding.metric?.trim()) parts.push(finding.metric.trim());

  let text = parts.join(": ");
  if (sourceBit) text = `${text} · ${sourceBit}`;
  return text;
}

function formatComparison(finding: NormAuditFinding): string | null {
  if (finding.actualMm == null || finding.requiredMm == null) return null;

  const op =
    finding.actualMm < finding.requiredMm
      ? "<"
      : finding.actualMm > finding.requiredMm
        ? ">"
        : "=";

  if (finding.checkType === "room_area_min") {
    return `${finding.actualMm} ${op} ${finding.requiredMm} м²`;
  }

  const metric = finding.metric?.trim();
  const values = `${finding.actualMm} ${op} ${finding.requiredMm} мм`;
  return metric ? `${metric} ${values}` : values;
}

export function findingsToAnnotationNotes(
  findings: NormAuditFinding[],
  options?: {
    statuses?: Array<NormAuditFinding["status"]>;
    textTypeName?: string;
    offsetMm?: number;
  }
): Array<{
  text: string;
  elementId: number;
  textTypeName?: string;
  offsetMm?: number;
}> {
  const statuses = new Set(
    options?.statuses ?? (["violation", "nearLimit"] as const)
  );

  return findings
    .filter((f) => statuses.has(f.status) && f.elementId > 0)
    .map((f) => ({
      text: formatFindingAnnotation(f),
      elementId: f.elementId,
      ...(options?.textTypeName
        ? { textTypeName: options.textTypeName }
        : {}),
      ...(options?.offsetMm != null ? { offsetMm: options.offsetMm } : {}),
    }));
}
