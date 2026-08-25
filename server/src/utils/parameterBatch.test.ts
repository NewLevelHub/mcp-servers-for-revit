import assert from "node:assert/strict";
import { describe, it } from "node:test";
import { chunk, readElementFields, PARAM_BATCH_SIZE } from "./parameterBatch.js";

function fakeClient(handler: (command: string, params: any) => any) {
  return { sendCommand: async (command: string, params: unknown) => handler(command, params) };
}

describe("chunk", () => {
  it("splits into batch-sized groups", () => {
    const groups = chunk([1, 2, 3, 4, 5], 2);
    assert.deepEqual(groups, [[1, 2], [3, 4], [5]]);
  });

  it("a list under the size is one group", () => {
    assert.deepEqual(chunk([1, 2], 100), [[1, 2]]);
  });

  it("an empty list produces no groups", () => {
    assert.deepEqual(chunk([], 100), []);
  });
});

describe("readElementFields", () => {
  it(
    "keys a resolved alias under the requested name too — a caller who asked for " +
      "\"Comments\" must find it at fields.Comments even though Revit's own name " +
      "for that parameter is «Комментарии» (REV-181 live bug)",
    async () => {
      const client = fakeClient(() => ({
        success: true,
        message: "ok",
        results: [
          {
            success: true,
            message: "ok",
            elementId: 1,
            elementName: "Wall",
            category: "Стены",
            parameters: [{ name: "Комментарии", displayValue: "hello", hasValue: true }],
          },
        ],
      }));

      const { elements } = await readElementFields(client, [1], ["Comments"]);
      assert.equal(elements[0].fields["Comments"], "hello");
      assert.equal(elements[0].fields["Комментарии"], "hello");
    }
  );

  it("a parameter with no value is not added under either key", async () => {
    const client = fakeClient(() => ({
      success: true,
      message: "ok",
      results: [
        {
          success: true,
          message: "ok",
          elementId: 1,
          parameters: [{ name: "Комментарии", displayValue: "", hasValue: false }],
        },
      ],
    }));

    const { elements } = await readElementFields(client, [1], ["Comments"]);
    assert.equal("Comments" in elements[0].fields, false);
  });

  it(
    "falls back to Revit's own name only when the returned count doesn't match the " +
      "request (one alias didn't resolve on this element) — still correct, just not aliased",
    async () => {
      const client = fakeClient(() => ({
        success: true,
        message: "ok",
        results: [
          {
            success: true,
            message: "ok",
            elementId: 1,
            // Asked for 2 names, only 1 resolved on this element.
            parameters: [{ name: "Марка", displayValue: "М-1", hasValue: true }],
          },
        ],
      }));

      const { elements } = await readElementFields(client, [1], ["Mark", "NoSuchParam"]);
      assert.equal(elements[0].fields["Марка"], "М-1");
      assert.equal("Mark" in elements[0].fields, false);
    }
  );

  it("an element the Revit call marked unsuccessful is reported as a read error, not a silent gap", async () => {
    const client = fakeClient(() => ({
      success: false,
      message: "partial",
      results: [{ success: false, message: "Element with id 99 was not found.", elementId: 99 }],
    }));

    const { elements, errors } = await readElementFields(client, [99], ["Mark"]);
    assert.equal(elements.length, 0);
    assert.deepEqual(errors, [{ elementId: 99, message: "Element with id 99 was not found." }]);
  });

  it(`batches ids over ${PARAM_BATCH_SIZE} into more than one call`, async () => {
    let calls = 0;
    const client = fakeClient((_cmd, params) => {
      calls++;
      return {
        success: true,
        message: "ok",
        results: (params.elementIds as number[]).map((elementId) => ({
          success: true,
          message: "ok",
          elementId,
          parameters: [],
        })),
      };
    });

    const ids = Array.from({ length: PARAM_BATCH_SIZE + 10 }, (_, i) => i + 1);
    const { elements } = await readElementFields(client, ids, ["Mark"]);
    assert.equal(calls, 2);
    assert.equal(elements.length, ids.length);
  });
});
