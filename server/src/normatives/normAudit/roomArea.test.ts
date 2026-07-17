import assert from "node:assert/strict";
import { describe, it } from "node:test";
import { classifyRoomAreas } from "./roomArea.js";
import type { RoomAreaLimit } from "./resolveRoomAreaLimits.js";

const livingLimit: RoomAreaLimit = {
  category: "living_room",
  minAreaM2: 9,
  source: {
    document: "СП РК 3.02-101-2012",
    clause: "п. 5.1.2",
    quote: "Площадь жилого помещения не менее 9 м².",
  },
  rule: { id: "living-9" } as never,
};

describe("classifyRoomAreas (golden)", () => {
  it("flags narrow living room, keeps ok kitchen", () => {
    const result = classifyRoomAreas(
      [
        { id: 1, name: "Жилая", areaM2: 8.2 },
        { id: 2, name: "Кухня", areaM2: 10 },
        { id: 3, name: "Коридор", areaM2: 5 },
      ],
      { limits: [livingLimit] }
    );

    assert.equal(result.violations.length, 1);
    assert.equal(result.violations[0].id, 1);
    assert.equal(result.compliant.length, 0);
    assert.equal(result.skippedNoLimit, 1);
    assert.equal(result.skippedUnknown, 1);
  });
});
