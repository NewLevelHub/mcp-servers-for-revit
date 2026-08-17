import type { NormAuditFinding } from "./types.js";

/**
 * Short plan annotation for a norm finding (REV-61).
 * Template: `{name}: {actual} < {required} · {document} {clause}`
 * Never copies the full source.quote.
 */
export function formatFindingAnnotation(finding: NormAuditFinding): string {
  const { name, detail, source } = annotationParts(finding);
  return joinAnnotation(name, detail, source);
}

interface AnnotationParts {
  name: string;
  detail: string;
  source: string;
}

function annotationParts(finding: NormAuditFinding): AnnotationParts {
  const name = (finding.name || `id ${finding.elementId}`).trim();
  const doc = (finding.source?.document || "").trim();
  const clause = (finding.source?.clause || "").trim();

  const comparison = formatComparison(finding);
  const detail =
    comparison ?? (finding.note?.trim() || finding.metric?.trim() || "");

  return {
    name,
    detail,
    source: [doc, clause].filter(Boolean).join(" ").trim(),
  };
}

/** Pass name=null for a continuation line — the element is already named above. */
function joinAnnotation(
  name: string | null,
  detail: string,
  source: string
): string {
  const parts = [name, detail].filter(Boolean) as string[];
  const head = parts.join(": ");
  if (!source) return head;
  return head ? `${head} · ${source}` : source;
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

/**
 * One note per element, not per finding. A stair failing both march width and
 * tread used to get two notes whose leaders landed on the same point, drawing
 * two lines on top of each other across the plan.
 *
 * Findings on one element stack as lines inside a single note; the element name
 * is printed once, so line 2+ reads «проступь 250 < 300 мм · СП РК …».
 */
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
  findingCount: number;
  textTypeName?: string;
  offsetMm?: number;
}> {
  const statuses = new Set(
    options?.statuses ?? (["violation", "nearLimit"] as const)
  );

  const grouped = new Map<
    number,
    {
      elementId: number;
      name: string;
      lines: string[];
      /** Named form of every line already added — dedup key, see below. */
      seen: Set<string>;
      findingCount: number;
    }
  >();

  for (const finding of findings) {
    if (!statuses.has(finding.status) || !(finding.elementId > 0)) continue;

    const { name, detail, source } = annotationParts(finding);
    // Dedup on the named form: the rendered line drops the name from line 2+,
    // so comparing rendered lines would never match the first one.
    const named = joinAnnotation(name, detail, source);

    const entry = grouped.get(finding.elementId);
    if (!entry) {
      grouped.set(finding.elementId, {
        elementId: finding.elementId,
        name,
        lines: [named],
        seen: new Set([named]),
        findingCount: 1,
      });
      continue;
    }

    entry.findingCount += 1;
    if (entry.seen.has(named)) continue;
    entry.seen.add(named);
    // Repeat the name only when this finding calls the element something else.
    entry.lines.push(
      joinAnnotation(name === entry.name ? null : name, detail, source)
    );
  }

  return [...grouped.values()].map((entry) => ({
    text: entry.lines.join("\n"),
    elementId: entry.elementId,
    findingCount: entry.findingCount,
    ...(options?.textTypeName ? { textTypeName: options.textTypeName } : {}),
    ...(options?.offsetMm != null ? { offsetMm: options.offsetMm } : {}),
  }));
}
