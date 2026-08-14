import assert from "node:assert/strict";
import { describe, it } from "node:test";
import { normalizeCategoryNames } from "./revitCategories.js";

describe("normalizeCategoryNames", () => {
  it("passes OST_ names through untouched", () => {
    const { categories, unresolved } = normalizeCategoryNames(["OST_Doors", "OST_Walls"]);
    assert.deepEqual(categories, ["OST_Doors", "OST_Walls"]);
    assert.deepEqual(unresolved, []);
  });

  it("maps plain English and Russian names onto the enum", () => {
    assert.deepEqual(normalizeCategoryNames(["doors"]).categories, ["OST_Doors"]);
    assert.deepEqual(normalizeCategoryNames(["Двери"]).categories, ["OST_Doors"]);
    assert.deepEqual(normalizeCategoryNames(["стены", "окна"]).categories, [
      "OST_Walls",
      "OST_Windows",
    ]);
  });

  it("accepts a bare string as well as a list", () => {
    assert.deepEqual(normalizeCategoryNames("мебель").categories, ["OST_Furniture"]);
  });

  it("reports names it could not map instead of dropping them", () => {
    const { categories, unresolved } = normalizeCategoryNames(["doors", "щеглы"]);
    assert.deepEqual(categories, ["OST_Doors"]);
    assert.deepEqual(unresolved, ["щеглы"]);
  });

  it("trims and de-duplicates", () => {
    const { categories } = normalizeCategoryNames([" doors ", "OST_Doors", "двери"]);
    assert.deepEqual(categories, ["OST_Doors"]);
  });

  it("returns nothing for empty input so the filter stays off", () => {
    assert.deepEqual(normalizeCategoryNames([]).categories, []);
    assert.deepEqual(normalizeCategoryNames(undefined).categories, []);
  });
});
