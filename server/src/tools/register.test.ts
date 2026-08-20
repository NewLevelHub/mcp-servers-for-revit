import test from "node:test";
import assert from "node:assert/strict";
import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { InMemoryTransport } from "@modelcontextprotocol/sdk/inMemory.js";
import { captureTools } from "./register.js";
import type { ToolHandle } from "../utils/toolCatalog.js";

/**
 * The guarantees here are the ones a guessed argument name broke in the field,
 * so they are checked through a real client over a real transport rather than
 * against the schema object: what matters is what the model receives.
 */
async function connectedPair(register: (server: McpServer) => void) {
  const server = new McpServer({ name: "test", version: "0.0.0" });
  const sink = new Map<string, ToolHandle>();
  register(captureTools(server, sink));

  const client = new Client({ name: "test-client", version: "0.0.0" });
  const [clientTransport, serverTransport] =
    InMemoryTransport.createLinkedPair();
  await Promise.all([
    client.connect(clientTransport),
    server.connect(serverTransport),
  ]);

  return { client, sink };
}

const filterTool = (server: McpServer) => {
  server.tool(
    "get_current_view_elements",
    "Elements of the active view",
    {
      modelCategoryList: z.array(z.string()).optional(),
      limit: z.number().int().positive().optional().default(150),
    },
    async (args: { modelCategoryList?: string[]; limit?: number }) => ({
      content: [
        {
          type: "text" as const,
          text: JSON.stringify({
            filteredBy: args.modelCategoryList ?? null,
            limit: args.limit,
          }),
        },
      ],
    })
  );
};

/**
 * A validation failure comes back as an `isError` result, not as a thrown
 * JSON-RPC error — the SDK catches its own `McpError` and shapes it as tool
 * output. That is worth pinning down: it is why the panel drew a green tick
 * over the refused `color_elements` call on 19.08.2026.
 */
async function callAndCapture(
  client: Client,
  args: Record<string, unknown>
): Promise<{ isError: boolean; text: string }> {
  const result = (await client.callTool({
    name: "get_current_view_elements",
    arguments: args,
  })) as { isError?: boolean; content: { text: string }[] };

  return {
    isError: result.isError === true,
    text: result.content.map((c) => c.text).join("\n"),
  };
}

test("a guessed argument name is refused, not dropped", async () => {
  const { client } = await connectedPair(filterTool);

  // The exact call from 19.08.2026: the spelling is `modelCategoryList`, and
  // this used to come back as an unfiltered view with no hint of a problem.
  const { isError, text } = await callAndCapture(client, {
    categories: ["OST_Rooms"],
  });

  assert.equal(isError, true);
  assert.match(text, /categories/);
  assert.match(text, /modelCategoryList/);
});

test("the refusal names the tool's own parameters", async () => {
  const { client } = await connectedPair(filterTool);

  const { isError, text } = await callAndCapture(client, {
    limit: 10,
    nonsense: true,
  });

  assert.equal(isError, true);
  assert.match(text, /Неизвестный параметр/);
  assert.match(text, /modelCategoryList, limit/);
});

test("valid arguments still reach the handler, defaults included", async () => {
  const { client } = await connectedPair(filterTool);

  const result = (await client.callTool({
    name: "get_current_view_elements",
    arguments: { modelCategoryList: ["OST_Rooms"] },
  })) as { content: { text: string }[] };

  assert.deepEqual(JSON.parse(result.content[0].text), {
    filteredBy: ["OST_Rooms"],
    limit: 150,
  });
});

test("the published schema closes the parameter list", async () => {
  const { client } = await connectedPair(filterTool);

  const { tools } = await client.listTools();
  const listed = tools.find((t) => t.name === "get_current_view_elements");

  assert.ok(listed, "tool must be listed");
  // Both halves matter: the model reads the closed list before it calls, and
  // an empty `properties` here would mean the parameters vanished from the
  // catalogue — which is what a non-object schema wrapper would have caused.
  assert.equal(listed.inputSchema.additionalProperties, false);
  assert.deepEqual(Object.keys(listed.inputSchema.properties ?? {}), [
    "modelCategoryList",
    "limit",
  ]);
});

test("a tool without parameters is still callable with no arguments", async () => {
  const { client } = await connectedPair((server) => {
    server.tool("say_hello", "Greets", async () => ({
      content: [{ type: "text" as const, text: "привет" }],
    }));
  });

  const result = (await client.callTool({ name: "say_hello" })) as {
    content: { text: string }[];
  };
  assert.equal(result.content[0].text, "привет");
});

test("the lite profile still gets its handles", async () => {
  const { sink } = await connectedPair(filterTool);

  // Registration moved from `tool` to `registerTool`; the handle it returns is
  // what `hideUnlistedTools` disables, so losing it would silently break the
  // lite profile instead of erroring.
  const handle = sink.get("get_current_view_elements");
  assert.ok(handle, "handle must be captured");
  assert.equal(typeof handle.disable, "function");
});
