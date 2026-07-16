import type { StoredNormRule } from "../rulesStore.js";

/** Parse minimum area in m² from a norm rule (handles bad normalized mm on m² rules). */
export function parseAreaMinM2FromRule(rule: StoredNormRule): number | undefined {
  if (rule.unit === "m2") {
    const v = rule.normalized?.min ?? rule.normalized?.exact ?? rule.value;
    if (typeof v === "number" && v >= 1 && v <= 40) return v;
  }

  const quote = `${rule.object} ${rule.source.quote} ${rule.applicability?.raw ?? ""}`;
  const patterns = [
    /ванной\s*[-–—:]\s*(\d+(?:[.,]\d+)?)\s*м\s*2/gi,
    /ванной\s*[-–—:]\s*(\d+(?:[.,]\d+)?)\s*м²/gi,
    /уборной\s*[-–—:]\s*(\d+(?:[.,]\d+)?)\s*м\s*2/gi,
    /уборной\s*[-–—:]\s*(\d+(?:[.,]\d+)?)\s*м²/gi,
    /(?:ванн\w*|сануз\w*|уборн\w*|туалет\w*|кухн\w*|комнат\w*|жил\w*)[^.…]{0,40}?(?:не\s+менее|кемінде|-|–|—|:)\s*(\d+(?:[.,]\d+)?)\s*м\s*2/gi,
    /(?:ванн\w*|сануз\w*|уборн\w*|туалет\w*|кухн\w*|комнат\w*|жил\w*)[^.…]{0,40}?(?:не\s+менее|кемінде|-|–|—|:)\s*(\d+(?:[.,]\d+)?)\s*м²/gi,
    /не менее\s+(\d+(?:[.,]\d+)?)\s*м\s*2/gi,
    /кемінде\s+(\d+(?:[.,]\d+)?)\s*м\s*2/gi,
    /не менее\s+(\d+(?:[.,]\d+)?)\s*м²/gi,
    /кемінде\s+(\d+(?:[.,]\d+)?)\s*м²/gi,
  ];

  const values: number[] = [];
  for (const pattern of patterns) {
    for (const match of quote.matchAll(pattern)) {
      const parsed = Number.parseFloat(match[1].replace(",", "."));
      if (Number.isFinite(parsed) && parsed >= 1 && parsed <= 40) {
        values.push(parsed);
      }
    }
  }

  if (values.length > 0) return Math.min(...values);

  if (
    typeof rule.value === "number" &&
    rule.value >= 1 &&
    rule.value <= 40 &&
    /ванн|сануз|кухн|площад|жил|комнат/i.test(quote)
  ) {
    return rule.value;
  }

  return undefined;
}
