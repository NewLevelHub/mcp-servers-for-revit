import assert from "node:assert/strict";
import { describe, it } from "node:test";
import {
  classifyResidentialRoom,
  isResidentialRoomForHeight,
} from "./roomPurpose.js";

describe("classifyResidentialRoom", () => {
  it("classifies kitchen / bedroom / living / bathroom", () => {
    assert.equal(classifyResidentialRoom("Кухня", ""), "kitchen");
    assert.equal(classifyResidentialRoom("Спальня", ""), "bedroom");
    assert.equal(classifyResidentialRoom("Жилая", ""), "living_room");
    assert.equal(classifyResidentialRoom("Санузел", ""), "bathroom");
  });

  it("excludes corridors and tambours", () => {
    assert.equal(classifyResidentialRoom("Коридор", ""), "excluded");
    assert.equal(classifyResidentialRoom("Тамбур", ""), "excluded");
  });

  it("does not treat «Воздушная зона» as bathroom (душ ⊂ воздуш)", () => {
    assert.equal(classifyResidentialRoom("Воздушная зона", ""), "unknown");
  });

  it("classifies ванная / душевая", () => {
    assert.equal(classifyResidentialRoom("Ванная", ""), "bathroom");
    assert.equal(classifyResidentialRoom("Душевая", ""), "bathroom");
  });

  it("isResidentialRoomForHeight only for classified residential rooms", () => {
    assert.equal(isResidentialRoomForHeight("kitchen"), true);
    assert.equal(isResidentialRoomForHeight("excluded"), false);
  });
});
