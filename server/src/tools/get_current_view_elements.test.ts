/**
 * The filter that was not applied.
 *
 * On 19.08.2026 the model asked this tool for the rooms of a plan and got all
 * 58 elements of the view back, with no error and no warning, because Revit
 * parses category names as the `OST_*` enum and quietly returns everything when
 * none of them parse. The model has no way to see that: a long list is exactly
 * what a working filter would produce on a busy view.
 *
 * These tests run without Revit on purpose — every case here is decided before
 * the connection is opened, which is the whole point of deciding it here.
 */
import test from "node:test";
import assert from "node:assert/strict";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { InMemoryTransport } from "@modelcontextprotocol/sdk/inMemory.js";
import { captureTools } from "./register.js";
import { registerGetCurrentViewElementsTool } from "./get_current_view_elements.js";
import type { ToolHandle } from "../utils/toolCatalog.js";

async function client() {
  const server = new McpServer({ name: "test", version: "0.0.0" });
  registerGetCurrentViewElementsTool(
    captureTools(server, new Map<string, ToolHandle>())
  );

  const c = new Client({ name: "test-client", version: "0.0.0" });
  const [ct, st] = InMemoryTransport.createLinkedPair();
  await Promise.all([c.connect(ct), server.connect(st)]);
  return c;
}

async function call(args: Record<string, unknown>) {
  const c = await client();
  const result = (await c.callTool({
    name: "get_current_view_elements",
    arguments: args,
  })) as { isError?: boolean; content: { text: string }[] };

  return {
    isError: result.isError === true,
    text: result.content.map((entry) => entry.text).join("\n"),
  };
}

test("a category name Revit cannot parse is refused, not ignored", async () => {
  const { isError, text } = await call({ modelCategoryList: ["Комнаты!"] });

  assert.equal(isError, true);
  assert.match(text, /не распознана/);
  assert.match(text, /Комнаты!/);
});

test("the refusal lists the names this build does know", async () => {
  const { text } = await call({ modelCategoryList: ["Абракадабра"] });

  assert.match(text, /OST_Rooms/);
});

test("annotation categories are checked the same way", async () => {
  const { isError, text } = await call({
    annotationCategoryList: ["марки чего-нибудь"],
  });

  assert.equal(isError, true);
  assert.match(text, /не распознана/);
});

/**
 * The two cases below go on to open a connection to Revit and fail there, in
 * well under a second — a refused connection is instant, and that is the point:
 * what is asserted is that they got past the category check rather than being
 * turned away by it. They hold either way, so a machine that does have Revit
 * listening runs them just the same.
 */
test("a plain name is translated instead of refused", async () => {
  const { text } = await call({ modelCategoryList: ["помещения"] });

  assert.doesNotMatch(text, /не распознана/);
});

test("no category at all is a valid whole-view request", async () => {
  const { text } = await call({ limit: 5 });

  assert.doesNotMatch(text, /не распознана/);
});
