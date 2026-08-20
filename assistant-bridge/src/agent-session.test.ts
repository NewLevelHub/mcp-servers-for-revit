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
  mergeProfiles,
  pickToolProfile,
  profileCovers,
  requestText,
  toolResultFailed,
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

test("a pinned profile is never narrowed per turn", () => {
  // MCP_TOOL_PROFILE=default is how an operator says "все инструменты, всегда".
  // Narrowing it to lite+annotation on «покрась помещения» would hide the very
  // tools they pinned it open for (наблюдалось 19.08.2026: color_elements was
  // hidden and the model answered that цветовые схемы are not in the catalog).
  assert.equal(pickToolProfile("покрась помещения цветами по зонам", false, "default"), "default");
  assert.equal(pickToolProfile("сколько помещений на этаже", false, "default"), "default");
  assert.equal(pickToolProfile("проставь марки помещений", false, "default"), "default");
});

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
  assert.equal(profile("какие связи подгружены в модель"), "lite+links");
  assert.equal(profile("покажи модели смежников"), "lite+links");
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
    "links",
    "modeling",
    "advanced",
  ]);
  const unknown = [...new Set(HINT_TOOL_GROUPS.map(([, group]) => group))].filter(
    (group) => !known.has(group)
  );
  assert.deepEqual(unknown, []);
});


// --- widening a live connection ---------------------------------------------

/**
 * Picking the right profile is only half the job: the agent and its MCP server
 * are built once per chat, and the tool list freezes when that connection opens.
 * On 19.08.2026 an architect opened a chat by placing doors and windows, then
 * asked «марки помещений поставь с квадратурой». The profile for that turn was
 * `lite+annotation` and the hint matched — but the connection was still the
 * `lite` one from the first turn, so `tag_all_rooms` was hidden. The model
 * answered that room tags are not in the catalog, then wrote the names as plain
 * text notes with `create_text_note`, four calls, one per room.
 *
 * profileCovers is what decides whether that connection has to be reopened.
 */
test("a connection is reopened when the turn reaches past it", () => {
  assert.equal(profileCovers("lite", "lite+annotation"), false);
  assert.equal(profileCovers("lite+sheets", "lite+annotation"), false);
  assert.equal(profileCovers("lite", "default"), false);
});

test("a connection is left alone when it already lists enough", () => {
  // Reopening costs the architect a wait; a catalog wider than the turn needs
  // costs only tokens. So only reaching past it is worth the boot.
  assert.equal(profileCovers("lite", "lite"), true);
  assert.equal(profileCovers("lite+annotation", "lite+annotation"), true);
  assert.equal(profileCovers("lite+sheets,annotation", "lite+annotation"), true);
  assert.equal(profileCovers("lite+annotation", "lite"), true);
  assert.equal(profileCovers("default", "lite+norms"), true);
  assert.equal(profileCovers("lite+all", "lite+norms"), true);
});

test("widening keeps the groups the chat already opened", () => {
  // Otherwise the second subject would hide the first: a chat that laid out a
  // sheet and then asked for tags would lose the sheet tools, and the next
  // sheet question would pay for a third reconnect.
  assert.equal(mergeProfiles("lite+sheets", "lite+annotation"), "lite+sheets,annotation");
  assert.equal(mergeProfiles("lite", "lite+annotation"), "lite+annotation");
  assert.equal(mergeProfiles("lite+annotation", "lite+annotation"), "lite+annotation");
  assert.equal(mergeProfiles("lite+annotation", "default"), "default");
  assert.equal(mergeProfiles("lite+all", "lite+norms"), "default");
});

test("the real chat from 19.08.2026 widens on the second turn", () => {
  const first = profile("добавь двери и окна");
  const second = profile("марки помещений поставь с квадратурой и укажи что где");

  assert.equal(second, "lite+annotation");
  assert.equal(
    profileCovers(first, second),
    false,
    "the lite connection from turn one hides tag_all_rooms — it has to be reopened"
  );
});

/**
 * A refused tool call must not be filed as a success.
 *
 * The 19.08.2026 journal has `color_elements` recorded twice with `ok: true`
 * and «MCP error -32602: Input validation error» as its summary — the panel
 * showed a tick, the turn ended as `outcome: ok`, and the architect got told
 * the rooms were coloured. Everything downstream (the panel tick, the journal,
 * the feedback report) reads this one flag.
 */
test("MCP validation refusals count as failures", () => {
  assert.equal(
    toolResultFailed(
      "MCP error -32602: Input validation error: Invalid arguments for tool color_elements"
    ),
    true
  );
});

test("the protocol error flag counts, whatever the text says", () => {
  assert.equal(
    toolResultFailed({ isError: true, content: [{ type: "text", text: "ok" }] }),
    true
  );
});

test("a refusal shaped by toolOutcome counts", () => {
  assert.equal(
    toolResultFailed({
      content: [
        {
          type: "text",
          text: 'set_elements_parameters не выполнен: Нечего записывать: передайте либо edits',
        },
      ],
    }),
    true
  );
});

test("a wrapped envelope is still inspected", () => {
  assert.equal(toolResultFailed({ result: { isError: true } }), true);
});

test("a successful call stays successful", () => {
  assert.equal(
    toolResultFailed({
      content: [
        {
          type: "text",
          text: '{"success":true,"message":"Updated 12 parameter(s)."}',
        },
      ],
    }),
    false
  );
});

test("a norm violation is a result, not a failure", () => {
  // The word the model uses to report findings must never trip the check —
  // a check that flags its own findings as broken tools is worse than none.
  assert.equal(
    toolResultFailed({
      content: [
        {
          type: "text",
          text: '{"success":true,"violations":[{"room":"Санузел","reason":"ошибка по ширине двери"}]}',
        },
      ],
    }),
    false
  );
});

test("nothing at all is not a failure", () => {
  assert.equal(toolResultFailed(undefined), false);
  assert.equal(toolResultFailed(null), false);
});
