import assert from "node:assert/strict";
import { describe, it } from "node:test";
import { normalizeToolResult, wrapToolHandler } from "./toolOutcome.js";

function textResult(payload: unknown) {
  return {
    content: [
      { type: "text", text: typeof payload === "string" ? payload : JSON.stringify(payload) },
    ],
  };
}

describe("normalizeToolResult", () => {
  it("flags a refused Revit AIResult and states the reason first", () => {
    const result = normalizeToolResult(
      "set_element_parameter",
      textResult({ Success: false, Message: "Parameter 'Марка' not found", Response: null })
    );

    assert.equal(result.isError, true);
    assert.match(result.content![0].text!, /^set_element_parameter не выполнен: Parameter 'Марка' not found/);
    // The original payload must survive for the model to inspect.
    assert.match(result.content![0].text!, /"Success":false/);
  });

  it("accepts lowercase success and the CAD ok flag", () => {
    assert.equal(
      normalizeToolResult("t", textResult({ success: false, message: "нет активного вида" })).isError,
      true
    );
    assert.equal(
      normalizeToolResult("t", textResult({ ok: false, summary: "CAD не найден на виде" })).isError,
      true
    );
  });

  it("finds a refusal nested under result/response wrappers", () => {
    const result = normalizeToolResult(
      "annotate_norm_findings",
      textResult({ annotatedCount: 3, result: { Success: false, Message: "view is a sheet" } })
    );

    assert.equal(result.isError, true);
    assert.match(result.content![0].text!, /view is a sheet/);
  });

  it("leaves successful calls untouched", () => {
    const payload = { Success: true, Message: "created 4 walls", Response: [1, 2, 3, 4] };
    const result = normalizeToolResult("create_walls", textResult(payload));

    assert.equal(result.isError, undefined);
    assert.equal(result.content![0].text, JSON.stringify(payload));
  });

  it("does not treat a norm violation as a tool failure", () => {
    // A check that finds a violation ran correctly — passed:false is data, not a refusal.
    const payload = { passed: false, violations: [{ rule: "СП 1.13130", elementId: 42 }] };
    const result = normalizeToolResult("check_door_width", textResult(payload));

    assert.equal(result.isError, undefined);
  });

  it("leaves empty results and non-JSON text alone", () => {
    assert.equal(normalizeToolResult("t", textResult({ Success: true, Response: [] })).isError, undefined);
    assert.equal(normalizeToolResult("t", textResult("Готово, размеров нет")).isError, undefined);
  });

  it("keeps a result that already reported an error", () => {
    const already = { ...textResult({ Success: false }), isError: true as const };
    assert.equal(normalizeToolResult("t", already), already);
  });
});

describe("wrapToolHandler", () => {
  it("turns a thrown error into an MCP error instead of a plain result", async () => {
    const handler = wrapToolHandler("create_dimensions", async () => {
      throw new Error("Revit не отвечает");
    });

    const result = (await handler()) as { isError?: boolean; content: { text: string }[] };
    assert.equal(result.isError, true);
    assert.match(result.content[0].text, /create_dimensions не выполнен: Revit не отвечает/);
  });

  it("normalises a refusal returned by the handler", async () => {
    const handler = wrapToolHandler("create_dimensions", async () =>
      textResult({ Success: false, Message: "created 0 of 3" })
    );

    const result = (await handler()) as { isError?: boolean };
    assert.equal(result.isError, true);
  });
});
