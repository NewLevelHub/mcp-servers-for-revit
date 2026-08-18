/**
 * Which tools a turn is allowed to see (REV-41, REV-49).
 *
 * The bridge had no tests at all, and this is the decision it makes on every
 * single turn. On 18.08.2026 an architect asked «проверь противопожарные двери»
 * in the Revit panel: no hint matched, the turn stayed on `lite`,
 * `check_fire_doors` was hidden — and the model, rather than saying it could not
 * do it, assembled an answer from the door schedule and told them that automatic
 * СП/ГОСТ checking does not exist in this build. It does, and it answers in five
 * seconds.
 *
 * That is the shape of every failure here: a miss does not look like a failure,
 * it looks like a confident wrong answer. Hence a test per real phrase.
 */
import test from "node:test";
import assert from "node:assert/strict";
import {
  HINT_TOOL_GROUPS,
  isHeavyRequest,
  pickToolProfile,
  requestText,
} from "./agent-session.js";

const LITE = "lite";
const profile = (text: string, hasImages = false) =>
  pickToolProfile(text, hasImages, LITE);

/** How the panel actually delivers a turn: view context, then the question. */
const withContext = (question: string) =>
  `[Контекст] Вид: 2 этаж (План этажа, 1:100) · Уровень: 2 этаж\n[Запрос] ${question}`;

// --- the regression that started this ---------------------------------------

test("«проверь противопожарные двери» reaches the norm tools", () => {
  const chosen = profile("проверь противопожарные двери");
  assert.notEqual(chosen, LITE, "стался бы lite — check_fire_doors скрыт");
  assert.match(chosen, /norms/);
});

test("the same question through the panel's context prefix", () => {
  assert.match(profile(withContext("проверь противопожарные двери")), /norms/);
});

// --- a check request must never be answered from the small set ---------------

for (const phrase of [
  "проверь противопожарные двери",
  "проверить ширину эвакуационного коридора",
  "проверь глубину помещений по нормативу",
  "соответствует ли лоджия нормам",
  "проверь тамбур по ГОСТ",
  "проверь доступность для МГН",
  "проверь модель",
]) {
  test(`«${phrase}» never stays on the everyday set`, () => {
    assert.notEqual(
      profile(phrase),
      LITE,
      "запрос на проверку без инструментов проверки — модель придумает ответ"
    );
  });
}

test("a check whose subject is unclear opens the full catalog, not a guess", () => {
  // Better to overpay in tokens than to hide the one tool the turn needed.
  assert.equal(profile("проверь модель"), "default");
});

// --- targeted groups still work ---------------------------------------------

test("wording that names its subject gets only that group", () => {
  assert.equal(profile("проставь размеры помещений"), "lite+annotation");
  assert.equal(profile("собери спецификацию дверей"), "lite+schedules");
  assert.equal(profile("перечерти стены по подложке dwg"), "lite+cad");
  assert.equal(profile("покажи предупреждения модели"), "lite+quality");
});

test("two subjects in one request open both groups, in wording order", () => {
  assert.equal(
    profile("размести виды на листе и проставь размеры"),
    "lite+sheets,annotation"
  );
});

test("a group named twice is listed once", () => {
  assert.equal(profile("проставь размеры и марки"), "lite+annotation");
});

// --- everyday turns stay small ----------------------------------------------

for (const phrase of [
  "сколько помещений на этаже",
  "какая площадь этой комнаты",
  "поставь стену от 0,0 до 5000,0",
  "переименуй помещение 12 в кухню",
]) {
  test(`«${phrase}» stays on the everyday set`, () => {
    assert.equal(profile(phrase), LITE);
  });
}

// --- inputs with nothing to route on ----------------------------------------

test("an image means the whole catalog — a picture names no group", () => {
  assert.equal(profile("вот скрин, поправь", true), "default");
});

test("a long brief means the whole catalog", () => {
  assert.equal(profile("сделай ".repeat(120)), "default");
});

test("an empty request stays on the everyday set rather than erroring", () => {
  assert.equal(profile(""), LITE);
  assert.equal(profile(withContext("")), LITE);
});

// --- helpers ----------------------------------------------------------------

test("requestText keeps only what the architect typed", () => {
  assert.equal(requestText(withContext("проверь двери")), "проверь двери");
  assert.equal(requestText("без контекста"), "без контекста");
});

test("routing ignores case", () => {
  assert.match(profile("ПРОВЕРЬ ПРОТИВОПОЖАРНЫЕ ДВЕРИ"), /norms/);
});

test("every hint in the group map is itself heavy", () => {
  // The two lists were maintained by hand until a word in one and not the other
  // left a turn on `lite` with the tool it asked for hidden. HEAVY_TASK_HINTS is
  // now derived from this map; this test is what keeps it derived.
  const notHeavy = HINT_TOOL_GROUPS.filter(([hint]) => !isHeavyRequest(hint, false));
  assert.deepEqual(
    notHeavy.map(([hint]) => hint),
    [],
    "hint opens a tool group but does not make the turn heavy — the group is never requested"
  );
});

test("every group named by a hint exists on the server", () => {
  // Mirrors TOOL_GROUP_NAMES in server/src/utils/toolCatalog.ts. A name that
  // drifts apart is logged and ignored there, so the turn silently loses the
  // group rather than failing loudly.
  const known = new Set([
    "norms",
    "quality",
    "schedules",
    "sheets",
    "annotation",
    "cad",
    "modeling",
    "advanced",
  ]);
  const unknown = [...new Set(HINT_TOOL_GROUPS.map(([, group]) => group))].filter(
    (group) => !known.has(group)
  );
  assert.deepEqual(unknown, []);
});
