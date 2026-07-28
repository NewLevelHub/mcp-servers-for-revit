import type { FireDoorNormRule, FireDoorScenario } from "./fireDoorRules.js";
import { isFireDoorRequirementQuote } from "./fireDoorRules.js";

export type FireDoorMarkSource = "none" | "parameter" | "schedule_note" | "both";

export interface DoorFireFacts {
  id: number;
  uniqueId: string;
  mark: string;
  family: string;
  type: string;
  level: string;
  fromRoom: string;
  toRoom: string;
  openingWidthMm?: number | null;
  isOnEgressPath: boolean;
  isMarkedAsFireDoor: boolean;
  /** Where ПД mark was found: parameter | schedule_note | both | none */
  markSource: FireDoorMarkSource;
  currentFireRating: string;
  /** Note text from door schedule «Примечание» (REV-47). */
  scheduleNote: string;
}

export interface FireDoorCheckItem extends DoorFireFacts {
  requiresFireDoor: boolean;
  ruleId: string;
  reason: string;
  source: {
    document: string;
    clause: string;
    quote: string;
  };
  compliant: boolean;
}

export interface FireDoorCheckSummary {
  success: boolean;
  message: string;
  totalDoors: number;
  requiredFireDoors: number;
  nonCompliantCount: number;
  doors: FireDoorCheckItem[];
  appliedRules: FireDoorNormRule[];
}

const SCENARIO_PRIORITY: FireDoorScenario[] = [
  "stair-to-corridor",
  "between-compartments",
  "evacuation-exit",
  "egress-route",
  "fire-compartment-door",
];

function containsEgressKeyword(value: string): boolean {
  if (!value.trim()) return false;
  const normalized = value.toLowerCase();
  return (
    normalized.includes("коридор") ||
    normalized.includes("лест") ||
    normalized.includes("эвак") ||
    normalized.includes("corridor") ||
    normalized.includes("stair") ||
    normalized.includes("egress") ||
    normalized.includes("hall")
  );
}

function isStairwell(roomName: string): boolean {
  const normalized = roomName.toLowerCase();
  return (
    normalized.includes("лестнич") ||
    normalized.includes("stair") ||
    normalized.includes("лк ")
  );
}

function isVestibule(roomName: string): boolean {
  const normalized = roomName.toLowerCase();
  return (
    normalized.includes("тамбур") ||
    normalized.includes("вестиб") ||
    normalized.includes("vestibule") ||
    normalized.includes("холл")
  );
}

function isResidentialSpace(roomName: string): boolean {
  const normalized = roomName.toLowerCase();
  return (
    normalized.includes("квартир") ||
    normalized.includes("жил") ||
    normalized.includes("комнат") ||
    normalized.includes("спальн") ||
    normalized.includes("гостин") ||
    normalized.includes("кухн") ||
    normalized.includes("прихож") ||
    normalized.includes("apartment") ||
    normalized.includes("bedroom") ||
    normalized.includes("living")
  );
}

/**
 * Door scenarios that can require a fire door.
 * Do NOT flag every «прихожая ↔ межквартирный коридор» as ПД: that previously
 * paired path-length PDF snippets («30 м от двери спальни») with apartment entries.
 * Require stair / vestibule-hall / explicit evacuation-exit geometry + a real ПД quote.
 */
export function detectDoorScenarios(door: DoorFireFacts): FireDoorScenario[] {
  const { fromRoom, toRoom, isOnEgressPath } = door;
  const scenarios = new Set<FireDoorScenario>();

  const fromEgress =
    containsEgressKeyword(fromRoom) || isStairwell(fromRoom) || isVestibule(fromRoom);
  const toEgress =
    containsEgressKeyword(toRoom) || isStairwell(toRoom) || isVestibule(toRoom);
  const fromResidential = isResidentialSpace(fromRoom);
  const toResidential = isResidentialSpace(toRoom);
  const fromVestibule = isVestibule(fromRoom);
  const toVestibule = isVestibule(toRoom);

  if (
    (isStairwell(fromRoom) && (containsEgressKeyword(toRoom) || toResidential || toVestibule)) ||
    (isStairwell(toRoom) && (containsEgressKeyword(fromRoom) || fromResidential || fromVestibule))
  ) {
    scenarios.add("stair-to-corridor");
  }

  // Vestibule / lift hall doors on egress (not interior apartment-only pairs).
  if (
    (fromVestibule || toVestibule) &&
    !(fromResidential && toResidential) &&
    (isOnEgressPath || fromEgress || toEgress)
  ) {
    scenarios.add("between-compartments");
  }

  if (/выход|exit/i.test(`${fromRoom} ${toRoom}`) && (fromEgress || toEgress)) {
    scenarios.add("evacuation-exit");
  }

  return [...scenarios];
}

function pickRuleForScenarios(
  scenarios: FireDoorScenario[],
  rules: FireDoorNormRule[]
): FireDoorNormRule | null {
  const usable = rules.filter((rule) =>
    isFireDoorRequirementQuote(rule.source.quote)
  );
  const rulesByScenario = new Map<FireDoorScenario, FireDoorNormRule[]>();
  for (const rule of usable) {
    const bucket = rulesByScenario.get(rule.scenario) ?? [];
    bucket.push(rule);
    rulesByScenario.set(rule.scenario, bucket);
  }

  for (const scenario of SCENARIO_PRIORITY) {
    if (!scenarios.includes(scenario)) continue;
    const bucket = rulesByScenario.get(scenario);
    if (bucket && bucket.length > 0) return bucket[0];
  }

  // Strong geometry (stair / compartment / exit) may use any real ПД quote as citation.
  const strong = scenarios.some(
    (s) =>
      s === "stair-to-corridor" ||
      s === "between-compartments" ||
      s === "evacuation-exit"
  );
  if (strong && usable.length > 0) {
    return usable[0];
  }

  return null;
}

export function applyFireDoorRules(
  doors: DoorFireFacts[],
  rules: FireDoorNormRule[]
): FireDoorCheckSummary {
  const usableRules = rules.filter((rule) =>
    isFireDoorRequirementQuote(rule.source.quote)
  );
  const enriched: FireDoorCheckItem[] = doors.map((door) => {
    const scenarios = detectDoorScenarios(door);
    const matchedRule = pickRuleForScenarios(scenarios, usableRules);
    const requiresFireDoor = matchedRule !== null && scenarios.length > 0;
    const markSource = door.markSource ?? "none";
    const scheduleNote = door.scheduleNote ?? "";

    return {
      ...door,
      markSource,
      scheduleNote,
      requiresFireDoor,
      ruleId: matchedRule?.id ?? "",
      reason: matchedRule?.reason ?? "",
      source: matchedRule?.source ?? { document: "", clause: "", quote: "" },
      compliant: !requiresFireDoor || door.isMarkedAsFireDoor,
    };
  });

  const required = enriched.filter((door) => door.requiresFireDoor);
  const nonCompliant = required.filter((door) => !door.compliant);

  return {
    success: true,
    message: `Проверено ${enriched.length} дверей по ${usableRules.length} правилам из normatives; требуется противопожарных: ${required.length}, несоответствий: ${nonCompliant.length}.`,
    totalDoors: enriched.length,
    requiredFireDoors: required.length,
    nonCompliantCount: nonCompliant.length,
    doors: enriched,
    appliedRules: usableRules,
  };
}
