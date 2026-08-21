/**
 * Comparing how two models are set out — сверка общей площадки (REV-169).
 *
 * The mistake this looks for does not hurt on the day it is made. A link inserted
 * a little differently, or a КР floor 50 mm below ours, looks fine for a month;
 * then the beams sit at the wrong levels, the openings miss the pipes, and the
 * fix is no longer one place but the whole stык.
 *
 * Everything here is arithmetic on plain objects, so it is tested for real rather
 * than only against a running Revit.
 */

export interface SiteLevel {
  name: string;
  elevationMm: number;
}

export interface SiteGrid {
  name: string;
  startMm: { x: number; y: number; z: number };
  endMm: { x: number; y: number; z: number };
  isCurved?: boolean;
}

export interface SitePoints {
  projectBasePointMm?: { x: number; y: number; z: number };
  surveyPointMm?: { x: number; y: number; z: number };
  angleToTrueNorthDeg?: number;
}

export interface SiteSurvey {
  levels?: SiteLevel[];
  grids?: SiteGrid[];
  points?: SitePoints;
}

export interface LinkPlacement {
  originShared?: boolean;
  rotationDegrees?: number;
  mirrored?: boolean;
  originMm?: { x: number; y: number; z: number };
}

/** One thing that does not line up, in the words the architect needs. */
export interface SiteFinding {
  /** levels | grids | points | placement — what part of the setting-out. */
  area: "levels" | "grids" | "points" | "placement";
  /** mismatch | missing | extra */
  kind: "mismatch" | "missing" | "extra";
  /** The level or grid it is about. */
  subject: string;
  /** Ready to read out loud: both numbers and the difference. */
  text: string;
  /** How far apart, mm, when that is the point. */
  differenceMm?: number;
}

/** Below this two elevations are the same number written twice. */
export const DEFAULT_LEVEL_TOLERANCE_MM = 1;

/**
 * More missing (or extra) levels than this and they are folded into one line.
 *
 * A КР that models a foundation and two floors against our twenty produced 25
 * findings, of which 24 said the same thing: the two files are laid out
 * differently. The one that mattered — «1 этаж» and «Уровень 1» are the same
 * height under two names — was lost in the middle of them.
 */
export const LEVEL_LIST_LIMIT = 3;

/** Same idea for grids: a shifted axis is news, a whole missing scheme is one line. */
export const GRID_LIST_LIMIT = 3;

/** Grids drawn by hand never land on the exact same coordinate. */
export const DEFAULT_GRID_TOLERANCE_MM = 5;

export interface CompareOptions {
  levelToleranceMm?: number;
  gridToleranceMm?: number;
  /** Name of our model, for the wording. */
  hostName?: string;
  /** Name of the link, for the wording. */
  linkName?: string;
}

function normalise(name: string | undefined): string {
  return (name ?? "").trim().toLowerCase();
}

function round(value: number): number {
  return Math.round(value * 10) / 10;
}

/**
 * Levels of the two models.
 *
 * Matched by name first, because that is how people talk about them. What is left
 * over is matched by elevation — a level present in both at the same height under
 * two different names is a real finding, and one nobody spots by eye.
 */
export function compareLevels(
  host: SiteLevel[] | undefined,
  link: SiteLevel[] | undefined,
  options: CompareOptions = {}
): SiteFinding[] {
  const tolerance = options.levelToleranceMm ?? DEFAULT_LEVEL_TOLERANCE_MM;
  const findings: SiteFinding[] = [];

  const hostLevels = [...(host ?? [])];
  const linkLevels = [...(link ?? [])];
  const usedLink = new Set<SiteLevel>();
  const missing: SiteLevel[] = [];
  const extra: SiteLevel[] = [];

  for (const ours of hostLevels) {
    const byName = linkLevels.find(
      (theirs) => !usedLink.has(theirs) && normalise(theirs.name) === normalise(ours.name)
    );

    if (byName) {
      usedLink.add(byName);
      const difference = ours.elevationMm - byName.elevationMm;
      if (Math.abs(difference) > tolerance) {
        findings.push({
          area: "levels",
          kind: "mismatch",
          subject: ours.name,
          differenceMm: round(difference),
          text:
            `Уровень «${ours.name}»: у нас ${round(ours.elevationMm)} мм, ` +
            `в связи ${round(byName.elevationMm)} мм — разница ${round(Math.abs(difference))} мм.`,
        });
      }
      continue;
    }

    const byElevation = linkLevels.find(
      (theirs) =>
        !usedLink.has(theirs) && Math.abs(theirs.elevationMm - ours.elevationMm) <= tolerance
    );

    if (byElevation) {
      usedLink.add(byElevation);
      findings.push({
        area: "levels",
        kind: "mismatch",
        subject: ours.name,
        differenceMm: 0,
        text:
          `Отметка ${round(ours.elevationMm)} мм называется у нас «${ours.name}», ` +
          `а в связи «${byElevation.name}» — высота одна, имена разные.`,
      });
      continue;
    }

    missing.push(ours);
  }

  for (const theirs of linkLevels) {
    if (!usedLink.has(theirs)) extra.push(theirs);
  }

  // The differences that matter — a shared level at two heights, or one height
  // under two names — come first. What follows is «these two files are laid out
  // differently», which needs saying once.
  findings.push(...foldLevelList(missing, "missing"));
  findings.push(...foldLevelList(extra, "extra"));

  return findings;
}

function describeLevel(level: SiteLevel): string {
  return `«${level.name}» (${round(level.elevationMm)} мм)`;
}

/**
 * A handful of unmatched levels are listed one by one; a whole storey scheme is
 * one line. Either way the names are there — the ticket asks that a missing level
 * be visible, not that every one of them get a paragraph.
 */
function foldLevelList(levels: SiteLevel[], kind: "missing" | "extra"): SiteFinding[] {
  if (levels.length === 0) return [];

  if (levels.length <= LEVEL_LIST_LIMIT) {
    return levels.map((level) => ({
      area: "levels" as const,
      kind,
      subject: level.name,
      text:
        kind === "missing"
          ? `Уровня ${describeLevel(level)} в связи нет.`
          : `В связи есть уровень ${describeLevel(level)}, которого нет у нас.`,
    }));
  }

  const shown = levels.slice(0, LEVEL_LIST_LIMIT).map(describeLevel).join(", ");
  const rest = levels.length - LEVEL_LIST_LIMIT;

  return [
    {
      area: "levels",
      kind,
      subject: kind === "missing" ? "уровни" : "уровни связи",
      text:
        kind === "missing"
          ? `В связи нет ${levels.length} наших уровней: ${shown} и ещё ${rest}. ` +
            "Похоже, разбивка по этажам у файлов разная."
          : `В связи ${levels.length} своих уровней, которых нет у нас: ${shown} и ещё ${rest}.`,
    },
  ];
}

function distanceMm(
  a: { x: number; y: number; z: number },
  b: { x: number; y: number; z: number }
): number {
  const dx = a.x - b.x;
  const dy = a.y - b.y;
  const dz = a.z - b.z;
  return Math.sqrt(dx * dx + dy * dy + dz * dz);
}

/**
 * How far apart two grid lines run.
 *
 * A grid drawn from the other end is the same grid, so the ends are compared both
 * ways round and the better reading wins — otherwise every second axis would be
 * reported as metres out.
 */
export function gridOffsetMm(ours: SiteGrid, theirs: SiteGrid): number {
  const straight =
    distanceMm(ours.startMm, theirs.startMm) + distanceMm(ours.endMm, theirs.endMm);
  const reversed =
    distanceMm(ours.startMm, theirs.endMm) + distanceMm(ours.endMm, theirs.startMm);

  return Math.min(straight, reversed) / 2;
}

/**
 * Grids of the two models, matched by name.
 *
 * A grid with the right name half a metre away is worse than a missing one,
 * because it looks correct on both plans.
 */
export function compareGrids(
  host: SiteGrid[] | undefined,
  link: SiteGrid[] | undefined,
  options: CompareOptions = {}
): SiteFinding[] {
  const tolerance = options.gridToleranceMm ?? DEFAULT_GRID_TOLERANCE_MM;
  const findings: SiteFinding[] = [];

  const hostGrids = host ?? [];
  const linkGrids = link ?? [];

  // A link with no grids at all is one statement, not forty of them.
  if (hostGrids.length > 0 && linkGrids.length === 0) {
    return [
      {
        area: "grids",
        kind: "missing",
        subject: "оси",
        text: `В связи нет ни одной оси, у нас их ${hostGrids.length} — сверить не с чем.`,
      },
    ];
  }

  const matched = new Set<SiteGrid>();
  const missing: SiteGrid[] = [];

  for (const ours of hostGrids) {
    const theirs = linkGrids.find(
      (candidate) => !matched.has(candidate) && normalise(candidate.name) === normalise(ours.name)
    );

    if (!theirs) {
      missing.push(ours);
      continue;
    }

    matched.add(theirs);

    if (ours.isCurved || theirs.isCurved) {
      findings.push({
        area: "grids",
        kind: "mismatch",
        subject: ours.name,
        text: `Ось «${ours.name}» криволинейная — сверьте её положение вручную.`,
      });
      continue;
    }

    const offset = gridOffsetMm(ours, theirs);
    if (offset > tolerance) {
      findings.push({
        area: "grids",
        kind: "mismatch",
        subject: ours.name,
        differenceMm: round(offset),
        text: `Ось «${ours.name}» в связи смещена на ${round(offset)} мм.`,
      });
    }
  }

  const extra = linkGrids.filter((theirs) => !matched.has(theirs));

  // A shifted axis is the finding; a whole grid somebody never drew is one line.
  // The live run reported «оси N в связи нет» twelve times around the one axis
  // that was actually out of place.
  findings.push(...foldGridList(missing, "missing"));
  findings.push(...foldGridList(extra, "extra"));

  return findings;
}

function foldGridList(grids: SiteGrid[], kind: "missing" | "extra"): SiteFinding[] {
  if (grids.length === 0) return [];

  if (grids.length <= GRID_LIST_LIMIT) {
    return grids.map((grid) => ({
      area: "grids" as const,
      kind,
      subject: grid.name,
      text:
        kind === "missing"
          ? `Оси «${grid.name}» в связи нет.`
          : `В связи есть ось «${grid.name}», которой нет у нас.`,
    }));
  }

  const shown = grids.slice(0, GRID_LIST_LIMIT).map((grid) => `«${grid.name}»`).join(", ");
  const rest = grids.length - GRID_LIST_LIMIT;

  return [
    {
      area: "grids",
      kind,
      subject: kind === "missing" ? "оси" : "оси связи",
      text:
        kind === "missing"
          ? `В связи нет ${grids.length} наших осей: ${shown} и ещё ${rest}.`
          : `В связи ${grids.length} своих осей, которых нет у нас: ${shown} и ещё ${rest}.`,
    },
  ];
}

/** Project base point and survey point — where each model thinks it stands. */
export function comparePoints(
  host: SitePoints | undefined,
  link: SitePoints | undefined,
  options: CompareOptions = {}
): SiteFinding[] {
  const tolerance = options.gridToleranceMm ?? DEFAULT_GRID_TOLERANCE_MM;
  const findings: SiteFinding[] = [];
  if (!host || !link) return findings;

  const pairs: Array<[string, keyof SitePoints]> = [
    ["Базовая точка проекта", "projectBasePointMm"],
    ["Точка съёмки", "surveyPointMm"],
  ];

  for (const [label, key] of pairs) {
    const ours = host[key] as { x: number; y: number; z: number } | undefined;
    const theirs = link[key] as { x: number; y: number; z: number } | undefined;
    if (!ours || !theirs) continue;

    const offset = distanceMm(ours, theirs);
    if (offset > tolerance) {
      findings.push({
        area: "points",
        kind: "mismatch",
        subject: label,
        differenceMm: round(offset),
        text: `${label} расходится на ${round(offset)} мм.`,
      });
    }
  }

  if (
    host.angleToTrueNorthDeg != null &&
    link.angleToTrueNorthDeg != null &&
    Math.abs(host.angleToTrueNorthDeg - link.angleToTrueNorthDeg) > 0.01
  ) {
    findings.push({
      area: "points",
      kind: "mismatch",
      subject: "Угол на север",
      text:
        `Угол на истинный север: у нас ${host.angleToTrueNorthDeg}°, ` +
        `в связи ${link.angleToTrueNorthDeg}°.`,
    });
  }

  return findings;
}

/**
 * How the link was inserted. A link somebody nudged by hand is the cheapest of
 * these problems to fix and the easiest to miss.
 */
export function comparePlacement(placement: LinkPlacement | undefined): SiteFinding[] {
  const findings: SiteFinding[] = [];
  if (!placement) return findings;

  if (placement.mirrored) {
    findings.push({
      area: "placement",
      kind: "mismatch",
      subject: "Зеркальность",
      text: "Связь вставлена зеркально — почти наверняка это ошибка вставки.",
    });
  }

  const rotation = placement.rotationDegrees ?? 0;
  if (Math.abs(rotation) > 0.01) {
    findings.push({
      area: "placement",
      kind: "mismatch",
      subject: "Поворот",
      text: `Связь повёрнута на ${round(rotation)}° относительно нашей модели.`,
    });
  }

  if (placement.originShared === false) {
    const origin = placement.originMm;
    const offset = origin ? distanceMm(origin, { x: 0, y: 0, z: 0 }) : 0;
    findings.push({
      area: "placement",
      kind: "mismatch",
      subject: "Начало координат",
      differenceMm: round(offset),
      text:
        offset > 0
          ? `Связь стоит не в нашем начале координат — смещение ${round(offset)} мм. ` +
            "Проверьте, что её вставляли «Авто — совмещение внутренних координат»."
          : "Связь вставлена не по внутренним координатам — проверьте способ вставки.",
    });
  }

  return findings;
}

/**
 * The line the architect reads first.
 *
 * A check that answers a healthy model with three screens of text stops being
 * run, so a clean result is one short sentence.
 */
export function buildSiteMessage(
  linkName: string,
  findings: SiteFinding[],
  checked: string[]
): string {
  if (findings.length === 0) {
    const what = checked.length > 0 ? checked.join(", ") : "площадка";
    return `Связь «${linkName}»: расхождений нет (сверено: ${what}).`;
  }

  const counts = new Map<SiteFinding["area"], number>();
  for (const finding of findings) {
    counts.set(finding.area, (counts.get(finding.area) ?? 0) + 1);
  }

  const label: Record<SiteFinding["area"], string> = {
    levels: "уровни",
    grids: "оси",
    points: "базовые точки",
    placement: "вставка связи",
  };

  const parts = [...counts.entries()].map(([area, count]) => `${label[area]} — ${count}`);
  return `Связь «${linkName}»: расхождений ${findings.length} (${parts.join(", ")}).`;
}
