import assert from "node:assert/strict";
import { describe, it } from "node:test";
import {
  classifyResidentialRoom,
  isLivingRoomForDepth,
  isLivingScopeAlias,
  isResidentialRoomForHeight,
} from "./roomPurpose.js";

describe("classifyResidentialRoom", () => {
  it("classifies kitchen / bedroom / living / bathroom", () => {
    assert.equal(classifyResidentialRoom("Кухня", ""), "kitchen");
    assert.equal(classifyResidentialRoom("Спальня", ""), "bedroom");
    assert.equal(classifyResidentialRoom("Жилая", ""), "living_room");
    assert.equal(classifyResidentialRoom("Санузел", ""), "bathroom");
  });

  it("excludes corridors, tambours, stairs, PON", () => {
    assert.equal(classifyResidentialRoom("Коридор", ""), "excluded");
    assert.equal(classifyResidentialRoom("Тамбур", ""), "excluded");
    assert.equal(classifyResidentialRoom("Лестничная клетка", ""), "excluded");
    assert.equal(classifyResidentialRoom("ПОН", ""), "excluded");
  });

  it("does not treat «Воздушная зона» as bathroom (душ ⊂ воздуш)", () => {
    assert.equal(classifyResidentialRoom("Воздушная зона", ""), "unknown");
  });

  it("classifies ванная / душевая", () => {
    assert.equal(classifyResidentialRoom("Ванная", ""), "bathroom");
    assert.equal(classifyResidentialRoom("Душевая", ""), "bathroom");
  });

  it("classifies гостиная / детская as living", () => {
    assert.equal(classifyResidentialRoom("Гостиная", ""), "living_room");
    assert.equal(classifyResidentialRoom("Детская", ""), "living_room");
  });

  it("isResidentialRoomForHeight only for classified residential rooms", () => {
    assert.equal(isResidentialRoomForHeight("kitchen"), true);
    assert.equal(isResidentialRoomForHeight("excluded"), false);
  });
});

describe("isLivingRoomForDepth (REV-50)", () => {
  it("includes bedroom and living aliases without the word «жилая»", () => {
    assert.equal(isLivingRoomForDepth("Спальня"), true);
    assert.equal(isLivingRoomForDepth("Гостиная"), true);
    assert.equal(isLivingRoomForDepth("Детская"), true);
    assert.equal(isLivingRoomForDepth("Кабинет"), true);
  });

  it("excludes stairs, corridor, PON, kitchen, loggia", () => {
    assert.equal(isLivingRoomForDepth("Лестница"), false);
    assert.equal(isLivingRoomForDepth("Коридор"), false);
    assert.equal(isLivingRoomForDepth("ПОН"), false);
    assert.equal(isLivingRoomForDepth("Кухня"), false);
    assert.equal(isLivingRoomForDepth("Лоджия"), false);
  });

  it("treats «жилая» filter as living-scope alias", () => {
    assert.equal(isLivingScopeAlias("жилая"), true);
    assert.equal(isLivingScopeAlias("living"), true);
    assert.equal(isLivingScopeAlias("спальня"), false);
  });
});
