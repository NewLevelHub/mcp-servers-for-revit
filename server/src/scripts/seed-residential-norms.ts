/**
 * Seed / upsert norms that apply to ordinary residential multi-apartment buildings.
 * - Re-extracts from residential PDFs in normatives/
 * - Tags buildingType = «жилые здания»
 * - Skips (or tags) MGN/спецжильё as housingType=mgn
 * - Upserts curated pack (площадь ванной, высота, тамбур, дверь…)
 *
 * Usage: npm run build && node build/scripts/seed-residential-norms.js
 */
import { readFile } from "node:fs/promises";
import { basename, join } from "node:path";
import pdfParse from "pdf-parse";
import db from "../database/db.js";
import { extractRulesFromText } from "../normatives/extractRules.js";
import {
  normalizeDocumentName,
  resolveNormativesDir,
} from "../normatives/fireDoorRules.js";
import { ensureCuratedResidentialRoomNorms } from "../normatives/normAudit/curatedResidentialRoomNorms.js";
import {
  getNormLibraryStats,
  saveNormRules,
  withSuggestedTags,
  type SaveableNormRule,
} from "../normatives/rulesStore.js";

/** PDFs that primarily regulate residential / multi-apartment buildings. */
const RESIDENTIAL_PDFS = [
  "SP_RK_3.02-101-2012_27.04.2021.pdf",
  "СН РК_3.02-01-2023.pdf",
  "Санитарно-эпидемиологические требования к административным и жилым зданиям.pdf",
  "SP_RK_3.02-109-2012_07.08.2018.pdf",
  "Тех.регламент Общие требования к пожарной.pdf",
  "СН РК_3.02-09-2019.pdf",
] as const;

const RES_HINT =
  /жил|тұрғын|квартир|пәтер|многоквартир|көп\s*пәтер|ф\s*1[.,]3|ф1\.3/i;
const MGN_HINT =
  /мүгедек|инвалид|престар|қарт\s+және|маломобил|коляск|специальн\w*\s+квартир|спецжил/i;
const PUBLIC_ONLY_HINT =
  /санатор|гостиниц|қонақ\s*үй|шипажай|школ|мектеп|больниц|детск|ясли|бақша/i;

function isMgnRule(rule: SaveableNormRule, fileName: string): boolean {
  const blob = `${fileName} ${rule.source.quote} ${rule.applicability?.raw ?? ""} ${rule.object}`;
  if (/3\.06-101|3\.06-31/i.test(fileName)) return true;
  return MGN_HINT.test(blob);
}

function isResidentialRelevant(
  rule: SaveableNormRule,
  fileName: string
): boolean {
  const blob = `${rule.source.quote} ${rule.applicability?.raw ?? ""} ${rule.object}`;
  if (PUBLIC_ONLY_HINT.test(blob) && !RES_HINT.test(blob)) return false;
  // Always keep numeric rules from the main residential code.
  if (/3\.02-101/i.test(fileName)) return true;
  if (/3\.02-01/i.test(fileName)) return true;
  if (/санитарно-эпидемиолог/i.test(fileName) && /жил/i.test(fileName)) {
    return true;
  }
  return RES_HINT.test(blob);
}

function tagResidential(
  rule: SaveableNormRule,
  fileName: string
): SaveableNormRule {
  const mgn = isMgnRule(rule, fileName);
  const tags = new Set(rule.tags ?? []);
  tags.add("жилое здание");
  tags.add("тұрғын үй");
  tags.add("многоквартирный");
  if (mgn) {
    tags.add("МГН");
    tags.add("спецжильё");
  } else {
    tags.add("обычное жильё");
  }

  return {
    ...rule,
    tags: [...tags].slice(0, 12),
    applicability: {
      raw:
        rule.applicability?.raw ??
        (mgn
          ? "МГН / спецжильё / квартиры для пожилых"
          : "жилые многоквартирные здания"),
      roomType: rule.applicability?.roomType ?? "жилые помещения",
      buildingType: mgn
        ? "жилые здания (МГН/спецжильё)"
        : "жилые здания",
      conditions: rule.applicability?.conditions,
    },
  };
}

/** Extra curated residential rules verified in СП РК 3.02-101 PDF text. */
function curatedResidentialBuildingNorms(): SaveableNormRule[] {
  return [
    {
      id: "сп рк 3.02-101-2012|табл. 1 прим. 4|уборная|min_value",
      type: "min_value",
      object: "уборная",
      value: 1.2,
      unit: "m2",
      source: {
        document: "СП РК 3.02-101-2012",
        clause: "табл. 1 прим. 4",
        quote:
          "В минимальные площади квартир включены: … уборной - 1,2 м², прихожей - из расчета ширины не менее 1,2 м …",
      },
      applicability: {
        raw: "минимальные площади квартир",
        roomType: "жилые помещения",
        buildingType: "жилые здания",
      },
      normalized: { exact: 1.2 },
      tags: [
        "уборная",
        "туалет",
        "площадь уборной",
        "дәретхана",
        "жилое здание",
        "обычное жильё",
      ],
    },
    {
      id: "сп рк 3.02-101-2012|табл. 1 прим. 4|кухня|min_value",
      type: "min_value",
      object: "кухня",
      value: 5,
      unit: "m2",
      source: {
        document: "СП РК 3.02-101-2012",
        clause: "табл. 1 прим. 4",
        quote:
          "В минимальные площади квартир включены: … минимальные площади кухни – 5 м² …",
      },
      applicability: {
        raw: "минимальные площади квартир (в т.ч. кухня-ниша в низших классах)",
        roomType: "жилые помещения",
        buildingType: "жилые здания",
      },
      normalized: { exact: 5 },
      tags: ["кухня", "площадь кухни", "ас үй", "жилое здание", "обычное жильё"],
    },
    {
      id: "сп рк 3.02-101-2012|п. 4.4.10.6|тамбур|min_value",
      type: "min_value",
      object: "тамбур",
      value: 1.65,
      unit: "m",
      source: {
        document: "СП РК 3.02-101-2012",
        clause: "п. 4.4.10.6",
        quote:
          "…тұрғын ғимаратқа негізгі кіреберіске көлемі кемінде 1,65 м × 1,65 м тамбур қарастырылады.",
      },
      applicability: {
        raw: "входной тамбур жилого здания",
        roomType: "тамбур",
        buildingType: "жилые здания",
      },
      normalized: { exact: 1650 },
      tags: [
        "тамбур",
        "входной тамбур",
        "1,65",
        "жилое здание",
        "обычное жильё",
      ],
    },
    {
      id: "сп рк 3.02-101-2012|п. 4.6.11|дверь|min_value",
      type: "min_value",
      object: "дверь",
      value: 0.9,
      unit: "m",
      source: {
        document: "СП РК 3.02-101-2012",
        clause: "п. 4.6.11",
        quote:
          "Жарыққа ашық және есік ойықтарының, жайлардан және дәліздерден баспалдақ торына шығу ені кемінде 0,9 м болуы тиіс.",
      },
      applicability: {
        raw: "выходы из помещений и коридоров на лестничную клетку",
        roomType: "жилые помещения",
        buildingType: "жилые здания",
      },
      normalized: { exact: 900 },
      tags: [
        "дверь",
        "ширина двери",
        "эвакуация",
        "0,9 м",
        "жилое здание",
        "обычное жильё",
      ],
    },
    {
      id: "сп рк 3.02-101-2012|п. 4.4.4.26|коридор|min_value",
      type: "min_value",
      object: "коридор",
      value: 2.1,
      unit: "m",
      source: {
        document: "СП РК 3.02-101-2012",
        clause: "п. 4.4.4.26",
        quote:
          "Пәтер ішіндегі дәліздер, холдар биіктігі … кемінде 2,1 м құрауы тиіс.",
      },
      applicability: {
        raw: "внутриквартирные коридоры и холлы",
        roomType: "жилые помещения",
        buildingType: "жилые здания",
      },
      normalized: { exact: 2100 },
      tags: [
        "коридор",
        "высота коридора",
        "дәліз",
        "жилое здание",
        "обычное жильё",
      ],
    },
  ];
}

async function extractFromResidentialPdf(
  normativesDir: string,
  fileName: string
): Promise<{ rules: SaveableNormRule[]; error?: string }> {
  const pdfPath = join(normativesDir, fileName);
  try {
    const buf = await readFile(pdfPath);
    const parsed = await pdfParse(buf, { max: 200 });
    const document = normalizeDocumentName(fileName);
    const extracted = extractRulesFromText({
      text: parsed.text,
      document,
    });
    // Keep numeric limits always; also keep requirement/prohibition for
    // residential codes that are mostly qualitative (e.g. СН РК 3.02-01).
    const keepTypes = new Set([
      "min_value",
      "max_value",
      "range",
      "exact_value",
      "requirement",
      "prohibition",
    ]);
    const relevant = extracted.rules
      .filter((r) => keepTypes.has(r.type))
      .filter((r) => isResidentialRelevant(r, fileName))
      .map((r) => tagResidential(r, fileName));
    return { rules: withSuggestedTags(relevant) };
  } catch (error) {
    return {
      rules: [],
      error: error instanceof Error ? error.message : String(error),
    };
  }
}

const normativesDir = await resolveNormativesDir();
let inserted = 0;
let updated = 0;
const files: Array<{
  fileName: string;
  ruleCount: number;
  inserted: number;
  updated: number;
  error?: string;
}> = [];

for (const fileName of RESIDENTIAL_PDFS) {
  const { rules, error } = await extractFromResidentialPdf(
    normativesDir,
    fileName
  );
  if (error) {
    files.push({
      fileName,
      ruleCount: 0,
      inserted: 0,
      updated: 0,
      error,
    });
    continue;
  }
  if (rules.length === 0) {
    files.push({ fileName, ruleCount: 0, inserted: 0, updated: 0 });
    continue;
  }
  const versionMatch = basename(fileName, ".pdf").match(/(\d{2}\.\d{2}\.\d{4})/);
  const save = saveNormRules(db, rules, {
    documentVersion: versionMatch?.[1],
  });
  inserted += save.inserted;
  updated += save.updated;
  files.push({
    fileName,
    ruleCount: rules.length,
    inserted: save.inserted,
    updated: save.updated,
  });
}

const curatedRoom = ensureCuratedResidentialRoomNorms(db);
inserted += curatedRoom.inserted;
updated += curatedRoom.updated;

const curatedExtra = saveNormRules(db, curatedResidentialBuildingNorms(), {
  documentVersion: "27.04.2021",
});
inserted += curatedExtra.inserted;
updated += curatedExtra.updated;

const library = getNormLibraryStats(db);

console.log(
  JSON.stringify(
    {
      success: true,
      scope: "residential-buildings",
      normativesDir,
      files,
      curatedRoom,
      curatedExtra,
      inserted,
      updated,
      library,
    },
    null,
    2
  )
);
