import { AUDIT_SCOPE_NOTE } from "./checklist.js";
import { formatFindingNote } from "./normalizeFindings.js";
import type { NormAuditFinding, NormAuditResult } from "./types.js";

const CHECK_TITLES: Record<string, string> = {
  evacuation_width: "Эвак. ширина",
  room_depth: "Глубина помещений",
  min_dimensions: "Лоджии / балконы / простенки",
  fire_doors: "Противопожарные двери",
  door_clear_width: "Ширина двери в свету",
  tambour_size_min: "Габариты входного тамбура",
  room_area_min: "Мин. площадь помещений",
  room_height_min: "Мин. высота помещений",
  storey_height: "Высота этажа",
  egress_opening_width: "Ширина эвак. выхода",
  passage_width: "Ширина прохода",
  mgn_room_geometry: "МГН: геометрия помещений",
  mgn_turning_circle: "МГН: зона разворота кресла-коляски 1,5 м",
  mgn_corridor_width: "МГН: ширина коридора",
  mgn_wc_dimensions: "МГН: габариты доступного санузла",
  mgn_door_width: "МГН: ширина двери 0,9 м",
  mgn_ramp_slope: "МГН: уклон пандуса",
  mgn_door_maneuvering: "МГН: зона маневрирования у двери",
};

function statusBadge(status: NormAuditFinding["status"]): string {
  switch (status) {
    case "violation":
      return "❌ нарушение";
    case "nearLimit":
      return "⚠️ погранично";
    case "compliant":
      return "✅ ок";
    case "skipped":
      return "⏭ пропущено";
    default:
      return status;
  }
}

function formatSourceLine(finding: NormAuditFinding): string {
  const { document, clause, quote } = finding.source;
  const head = clause ? `${document}, ${clause}` : document;
  const q = quote.length > 160 ? `${quote.slice(0, 160)}…` : quote;
  return `${head} — «${q}»`;
}

/**
 * Compact markdown report for the agent / customer.
 * Violations first; compliant only when includeCompliant produced them.
 */
export function formatNormAuditReport(result: NormAuditResult): string {
  const lines: string[] = [
    "## Нормоконтроль модели (измеримые проверки)",
    "",
    result.message,
    "",
    `> ${result.scopeNote || AUDIT_SCOPE_NOTE}`,
    "",
    `- Область: **${result.scope === "project" ? "весь проект" : "этаж"}**` +
      (result.levelName ? ` — \`${result.levelName}\`` : " (текущий вид / без фильтра)"),
    `- Режим: **${result.mode}**`,
    `- Нарушений: **${result.summary.violations}**`,
    `- Пограничных: **${result.summary.nearLimit}**`,
    `- Соответствует: **${result.summary.compliant}**`,
    `- Пропущено / нет достоверных данных: **${result.summary.skipped}**`,
    `- Чекеров запущено: **${result.summary.checksRun}**` +
      (result.summary.checksFailed > 0
        ? `, ошибок: **${result.summary.checksFailed}**`
        : ""),
  ];

  if (result.highlightedCount != null) {
    if (result.filledRegionCount != null || result.doorHighlightCount != null) {
      const parts: string[] = [];
      if (result.filledRegionCount != null && result.filledRegionCount > 0) {
        parts.push(`цветовых областей: **${result.filledRegionCount}**`);
      }
      if (result.doorHighlightCount != null && result.doorHighlightCount > 0) {
        parts.push(`дверей: **${result.doorHighlightCount}**`);
      }
      if (
        result.otherElementHighlightCount != null &&
        result.otherElementHighlightCount > 0
      ) {
        parts.push(`прочих элементов: **${result.otherElementHighlightCount}**`);
      }
      lines.push(`- Заливка на плане: ${parts.join(", ") || "—"}`);
    } else {
      lines.push(`- Подсвечено элементов: **${result.highlightedCount}**`);
    }
  }

  if (result.warnings.length > 0) {
    lines.push("", "### Предупреждения");
    for (const warning of result.warnings) {
      lines.push(`- ${warning}`);
    }
  }

  const violations = result.findings.filter(
    (f) => f.status === "violation" || f.status === "nearLimit"
  );
  const compliant = result.findings.filter((f) => f.status === "compliant");
  const skippedFindings = result.findings.filter((f) => f.status === "skipped");

  lines.push("", "### Нарушения и пограничные");
  if (violations.length === 0) {
    lines.push("_Нарушений по запущенным проверкам не найдено._");
  } else {
    for (const finding of violations) {
      const title = CHECK_TITLES[finding.checkType] ?? finding.checkType;
      lines.push(
        `- ${statusBadge(finding.status)} **${finding.name}**` +
          (finding.level ? ` (${finding.level})` : "") +
          ` — ${title}: ${formatFindingNote(finding)}` +
          ` · id ${finding.elementId}`
      );
      lines.push(`  - ${formatSourceLine(finding)}`);
      if (finding.note && finding.checkType === "fire_doors") {
        lines.push(`  - ${finding.note}`);
      }
    }
  }

  if (compliant.length > 0) {
    lines.push("", "### Соответствует");
    for (const finding of compliant.slice(0, 40)) {
      const title = CHECK_TITLES[finding.checkType] ?? finding.checkType;
      lines.push(
        `- ✅ **${finding.name}** — ${title}: ${formatFindingNote(finding)}`
      );
    }
    if (compliant.length > 40) {
      lines.push(`- … и ещё ${compliant.length - 40}`);
    }
  }

  if (skippedFindings.length > 0) {
    lines.push("", "### Пропущенные измерения");
    for (const finding of skippedFindings) {
      const title = CHECK_TITLES[finding.checkType] ?? finding.checkType;
      lines.push(
        `- ⏭ **${finding.name}**` +
          (finding.level ? ` (${finding.level})` : "") +
          ` — ${title}: ${finding.note || "нет достоверных данных"}` +
          ` · id ${finding.elementId}`
      );
      lines.push(`  - ${formatSourceLine(finding)}`);
    }
  }

  if (result.skippedRules.length > 0) {
    lines.push("", "### Правила без checker (skipped)");
    for (const skipped of result.skippedRules) {
      const title = CHECK_TITLES[skipped.checkType] ?? skipped.checkType;
      lines.push(`- **${title}** — ${skipped.reason}`);
    }
  }

  if (result.checks.length > 0) {
    lines.push("", "### Прогоны");
    for (const check of result.checks) {
      const title = CHECK_TITLES[check.checkType] ?? check.checkType;
      const icon =
        check.status === "ok" ? "✅" : check.status === "error" ? "❌" : "⏭";
      lines.push(
        `- ${icon} **${title}** — ${check.status}` +
          (check.checkedCount > 0 ? `, проверено: ${check.checkedCount}` : "") +
          (check.message ? ` (${check.message})` : "")
      );
    }
  }

  return lines.join("\n");
}
