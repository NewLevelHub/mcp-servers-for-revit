import assert from "node:assert/strict";
import { describe, it } from "node:test";
import { resolve } from "node:path";
import { extractRulesFromPdfFile } from "./extractRulesFromPdf.js";

describe("extractRulesFromPdfFile", () => {
  it("reads text PDF and returns extraction result with language hint", async () => {
    const pdfPath = resolve(
      process.cwd(),
      "..",
      "normatives",
      "SP_RK_3.02-101-2012_27.04.2021.pdf"
    );

    const result = await extractRulesFromPdfFile({
      pdfPath,
      document: "СП РК 3.02-101-2012",
      maxPages: 8,
    });

    assert.equal(result.metadata.mode, "embedded-text");
    assert.ok(Array.isArray(result.rules));
    assert.ok(
      result.warnings.some((warning) =>
        warning.toLowerCase().includes("detected language:")
      )
    );
  });
});

