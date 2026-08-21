import assert from "node:assert/strict";
import { describe, it } from "node:test";
import {
  formatColorResult,
  type ColorSplashResponse,
} from "../tools/color_elements.js";

/** The payload the plugin returns when the scheme lands cleanly. */
const painted: ColorSplashResponse = {
  success: true,
  mode: "color_fill_scheme",
  schemeName: "MCP НК Номер 124712",
  parameterName: "Номер",
  totalElements: 4,
  coloredElements: 4,
  coloredGroups: 4,
  skippedValues: [],
  roomsWithoutArea: [],
  colorFillBlocked: false,
  message: "Применена цветовая схема вида (Цветовая область помещений).",
  results: [
    { parameterValue: "1", count: 1, color: { r: 255, g: 0, b: 0 } },
    { parameterValue: "2", count: 1, color: { r: 0, g: 0, b: 255 } },
    { parameterValue: "5", count: 1, color: { r: 0, g: 180, b: 0 } },
    { parameterValue: "6", count: 1, color: { r: 120, g: 90, b: 40 } },
  ],
};

describe("formatColorResult", () => {
  it("reports a clean run as a success and lists the groups", () => {
    const result = formatColorResult(painted, 3);

    assert.equal(result.isError, undefined);
    assert.match(result.content[0].text, /Colored 4 of 4 elements across 4 groups/);
    assert.match(result.content[0].text, /"1": 1 elements colored with RGB\(255, 0, 0\)/);
    assert.doesNotMatch(result.content[0].text, /ОДИН ЦВЕТ/);
  });

  it("flags a refusal as an error instead of a plain-text result", () => {
    // Watched on 20.08.2026 in an English Revit session: the model passed the
    // Russian category name, the plugin refused, and the dislike log still
    // recorded ok:true because nothing marked the result as failed.
    const result = formatColorResult(
      { success: false, message: "Category 'Помещения' not found" },
      3
    );

    assert.equal(result.isError, true);
    assert.match(result.content[0].text, /^color_elements не выполнен: Category 'Помещения' not found/);
  });

  it("treats a blocked colour fill as a failure, naming the rooms in the way", () => {
    // The scheme applies and every read-back passes, so the payload looks like
    // a success — while the architect is looking at flat pink.
    const result = formatColorResult(
      {
        ...painted,
        colorFillBlocked: true,
        roomsWithoutArea: ["3", "4", "7", "8", "9"],
      },
      3
    );

    assert.equal(result.isError, true);
    assert.match(result.content[0].text, /^color_elements не выполнен:/);
    assert.match(result.content[0].text, /5 помещ\. без площади: №3, №4, №7, №8, №9\./);
    assert.match(result.content[0].text, /не раскрашен/);
  });

  it("warns when a multi-colour palette collapses onto one group", () => {
    // «поставь цвета в помещения разные красный синий зеленый» grouped by «Имя»,
    // where all four rooms shared the name: one group, one colour, and the
    // answer claimed the palette alternated (20.08.2026 11:02).
    const result = formatColorResult(
      {
        ...painted,
        parameterName: "Имя",
        coloredGroups: 1,
        results: [{ parameterValue: "Помещение", count: 4, color: { r: 255, g: 0, b: 0 } }],
      },
      3
    );

    assert.equal(result.isError, undefined);
    assert.match(result.content[0].text, /^⚠ ОДИН ЦВЕТ НА ВСЕХ/);
    assert.match(result.content[0].text, /из 3 запрошенных цветов применён только первый/);
  });

  it("stays quiet about grouping when no palette was requested", () => {
    const result = formatColorResult(
      { ...painted, coloredGroups: 1, results: [painted.results![0]] },
      0
    );

    assert.doesNotMatch(result.content[0].text, /ОДИН ЦВЕТ/);
  });

  it("does not read a missing response as success", () => {
    const result = formatColorResult(undefined, 0);

    assert.equal(result.isError, true);
    assert.match(result.content[0].text, /не выполнен/);
  });
});
