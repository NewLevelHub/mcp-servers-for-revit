import assert from "node:assert/strict";
import { describe, it } from "node:test";
import { resolve } from "node:path";
import {
  assessPdfTextQuality,
  extractRulesFromPdfFile,
  MIN_EMBEDDED_TEXT_CHARS,
} from "./extractRulesFromPdf.js";

describe("assessPdfTextQuality", () => {
  it("flags empty text as likely scanned with explicit OCR warning", () => {
    const quality = assessPdfTextQuality("", 12);
    assert.equal(quality.likelyScanned, true);
    assert.equal(quality.charCount, 0);
    assert.ok(
      quality.warnings.some((w) => /no extractable text|OCR/i.test(w))
    );
  });

  it("flags sparse text as likely scanned", () => {
    const quality = assessPdfTextQuality("abc", 10);
    assert.equal(quality.likelyScanned, true);
    assert.ok(quality.warnings.some((w) => /sparse|scan/i.test(w)));
  });

  it("accepts dense text PDFs", () => {
    const text = "а".repeat(MIN_EMBEDDED_TEXT_CHARS * 3);
    const quality = assessPdfTextQuality(text, 2);
    assert.equal(quality.likelyScanned, false);
    assert.equal(quality.warnings.length, 0);
  });
});

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
    assert.ok(!result.warnings.some((w) => /no extractable text/i.test(w)));
  });
});
