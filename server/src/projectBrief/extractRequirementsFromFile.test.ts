import assert from "node:assert/strict";
import { describe, it } from "node:test";
import { extractRequirementsFromFile } from "./extractRequirementsFromFile.js";

describe("extractRequirementsFromFile", () => {
  it("refuses an unsupported extension rather than guessing a parser", async () => {
    await assert.rejects(
      () => extractRequirementsFromFile({ filePath: "brief.txt" }),
      /Unsupported file type ".txt"/
    );
  });

  it("is case-insensitive on the extension", async () => {
    await assert.rejects(
      () => extractRequirementsFromFile({ filePath: "brief.RTF" }),
      /Unsupported file type ".rtf"/
    );
  });
});
