import type { FireDoorNormRule, FireDoorScenario } from "./fireDoorRules.js";

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
    normalized.includes("apartment") ||
    normalized.includes("bedroom") ||
    normalized.includes("living")
  );
}

export function detectDoorScenarios(door: DoorFireFacts): FireDoorScenario[] {
  const { fromRoom, toRoom, isOnEgressPath } = door;
  const scenarios = new Set<FireDoorScenario>();

  if (isOnEgressPath || containsEgressKeyword(fromRoom) || containsEgressKeyword(toRoom)) {
    scenarios.add("egress-route");
  }

  const fromEgress =
    containsEgressKeyword(fromRoom) || isStairwell(fromRoom) || isVestibule(fromRoom);
  const toEgress =
    containsEgressKeyword(toRoom) || isStairwell(toRoom) || isVestibule(toRoom);
  const fromResidential = isResidentialSpace(fromRoom);
  const toResidential = isResidentialSpace(toRoom);

  if (
    (fromEgress && toResidential) ||
    (toEgress && fromResidential) ||
    (isStairwell(fromRoom) && containsEgressKeyword(toRoom)) ||
    (isStairwell(toRoom) && containsEgressKeyword(fromRoom))
  ) {
    scenarios.add("between-compartments");
  }

  if (
    (isStairwell(fromRoom) && (containsEgressKeyword(toRoom) || toResidential)) ||
    (isStairwell(toRoom) && (containsEgressKeyword(fromRoom) || fromResidential))
  ) {
    scenarios.add("stair-to-corridor");
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
  const rulesByScenario = new Map<FireDoorScenario, FireDoorNormRule[]>();
  for (const rule of rules) {
    const bucket = rulesByScenario.get(rule.scenario) ?? [];
    bucket.push(rule);
    rulesByScenario.set(rule.scenario, bucket);
  }

  for (const scenario of SCENARIO_PRIORITY) {
    if (!scenarios.includes(scenario)) continue;
    const bucket = rulesByScenario.get(scenario);
    if (bucket && bucket.length > 0) return bucket[0];
  }

  if (rules.length > 0 && scenarios.length > 0) {
    return rules[0];
  }

  return null;
}

export function applyFireDoorRules(
  doors: DoorFireFacts[],
  rules: FireDoorNormRule[]
): FireDoorCheckSummary {
  const enriched: FireDoorCheckItem[] = doors.map((door) => {
    const scenarios = detectDoorScenarios(door);
    const matchedRule = pickRuleForScenarios(scenarios, rules);
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
    message: `Проверено ${enriched.length} дверей по ${rules.length} правилам из normatives; требуется противопожарных: ${required.length}, несоответствий: ${nonCompliant.length}.`,
    totalDoors: enriched.length,
    requiredFireDoors: required.length,
    nonCompliantCount: nonCompliant.length,
    doors: enriched,
    appliedRules: rules,
  };
}
