import test from "node:test";
import assert from "node:assert/strict";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import {
  LITE_TOOLS,
  TOOL_GROUPS,
  TOOL_GROUP_NAMES,
  TOOL_GROUP_SUMMARIES,
  allGroupedTools,
  groupForTool,
  hideUnlistedTools,
  listedToolsFor,
  parseToolProfile,
  type ToolGroupName,
  type ToolHandle,
} from "./toolCatalog.js";

/** Registered but never listed in `default` — see DEFAULT_DENYLIST in register.ts. */
const DEFAULT_DENYLIST = new Set([
  "store_room_data",
  "store_project_data",
  "query_stored_data",
]);

function toolFileNames(): string[] {
  const here = path.dirname(fileURLToPath(import.meta.url));
  // Resolves against build/ at run time and src/ under ts-node; both hold the
  // same file set, which is the point of the check.
  const dir = path.resolve(here, "..", "tools");
  return fs
    .readdirSync(dir)
    .filter((f) => /\.(ts|js)$/.test(f))
    .filter((f) => !f.includes(".test."))
    .filter((f) => !/^(register|index)\.(ts|js)$/.test(f))
    .map((f) => f.replace(/\.(ts|js)$/, ""));
}

function fakeHandles(names: string[]): {
  handles: Map<string, ToolHandle>;
  listed: Map<string, boolean>;
} {
  const listed = new Map<string, boolean>();
  const handles = new Map<string, ToolHandle>();
  for (const name of names) {
    listed.set(name, true);
    handles.set(name, {
      enable: () => listed.set(name, true),
      disable: () => listed.set(name, false),
    });
  }
  return { handles, listed };
}

// --- the map itself ---------------------------------------------------------

test("every tool file is either lite or in exactly one group", () => {
  const grouped = allGroupedTools();
  const unassigned: string[] = [];
  const duplicated: string[] = [];

  for (const tool of toolFileNames()) {
    if (DEFAULT_DENYLIST.has(tool)) continue;

    const inLite = LITE_TOOLS.has(tool);
    const inGroup = grouped.has(tool);

    if (!inLite && !inGroup) unassigned.push(tool);
    if (inLite && inGroup) duplicated.push(tool);
  }

  assert.deepEqual(
    unassigned,
    [],
    "an unassigned tool is hidden by `lite` with no group that brings it back — " +
      "add it to LITE_TOOLS or a TOOL_GROUPS entry in utils/toolCatalog.ts"
  );
  assert.deepEqual(
    duplicated,
    [],
    "a lite tool must not also sit in a group — it is never hidden, so naming it in a group misleads"
  );
});

test("no tool is listed in two groups", () => {
  const seen = new Map<string, string>();
  for (const group of TOOL_GROUP_NAMES) {
    for (const tool of TOOL_GROUPS[group]) {
      const other = seen.get(tool);
      assert.equal(
        other,
        undefined,
        `${tool} is in both "${other}" and "${group}" — pick one`
      );
      seen.set(tool, group);
    }
  }
});

test("every group has a summary and every summary a group", () => {
  assert.deepEqual(
    TOOL_GROUP_NAMES.filter((g) => !TOOL_GROUP_SUMMARIES[g]),
    [],
    "a group without a summary cannot be explained to whoever picks the profile"
  );
  assert.deepEqual(
    Object.keys(TOOL_GROUP_SUMMARIES).filter(
      (g) => !TOOL_GROUP_NAMES.includes(g as ToolGroupName)
    ),
    []
  );
});

test("every group map entry points at a real tool file", () => {
  const files = new Set(toolFileNames());
  const missing = [...allGroupedTools()].filter((tool) => !files.has(tool));
  assert.deepEqual(missing, [], "group map names tools that do not exist");
});

test("groupForTool finds the group and skips lite tools", () => {
  assert.equal(groupForTool("check_fire_doors"), "norms");
  assert.equal(groupForTool("create_sheet"), "sheets");
  assert.equal(groupForTool("get_current_view_info"), undefined);
});

// --- profile parsing --------------------------------------------------------

test("a bare profile name parses with no groups", () => {
  assert.deepEqual(parseToolProfile("lite"), {
    base: "lite",
    groups: [],
    unknownGroups: [],
  });
  assert.deepEqual(parseToolProfile(undefined), {
    base: "default",
    groups: [],
    unknownGroups: [],
  });
});

test("groups come off either separator, and case does not matter", () => {
  const plus = parseToolProfile("lite+sheets+annotation");
  const comma = parseToolProfile("lite+sheets,annotation");
  const shouty = parseToolProfile("  LITE+Sheets, Annotation ");

  assert.deepEqual(plus.groups, ["sheets", "annotation"]);
  assert.deepEqual(comma.groups, plus.groups);
  assert.deepEqual(shouty.groups, plus.groups);
  assert.equal(shouty.base, "lite");
});

test("'all' expands to every group", () => {
  assert.deepEqual(parseToolProfile("lite+all").groups, [...TOOL_GROUP_NAMES]);
});

test("a repeated group is listed once", () => {
  assert.deepEqual(parseToolProfile("lite+norms,norms").groups, ["norms"]);
});

test("an unknown group is reported, not thrown — a typo must not kill the connection", () => {
  const parsed = parseToolProfile("lite+sheets,shets");
  assert.deepEqual(parsed.groups, ["sheets"]);
  assert.deepEqual(parsed.unknownGroups, ["shets"]);
  assert.equal(parsed.base, "lite");
});

// --- what ends up listed ----------------------------------------------------

test("listedToolsFor is the lite set plus the named groups", () => {
  const listed = listedToolsFor(["sheets"]);
  assert.ok(listed.has("get_current_view_info"), "lite tool missing");
  assert.ok(listed.has("create_sheet"), "requested group missing");
  assert.ok(!listed.has("check_fire_doors"), "unrequested group leaked in");
});

test("hiding leaves the lite set plus the requested group listed", () => {
  const { handles, listed } = fakeHandles([
    "get_current_view_info",
    "check_fire_doors",
    "create_sheet",
    "trace_walls_from_cad",
  ]);

  const hidden = hideUnlistedTools(handles, ["norms"]);

  assert.deepEqual(hidden.sort(), ["create_sheet", "trace_walls_from_cad"]);
  assert.equal(listed.get("get_current_view_info"), true);
  assert.equal(listed.get("check_fire_doors"), true);
  assert.equal(listed.get("create_sheet"), false);
});

test("no groups means the everyday set only", () => {
  const { handles, listed } = fakeHandles([
    "get_current_view_info",
    "check_fire_doors",
  ]);

  const hidden = hideUnlistedTools(handles, []);

  assert.deepEqual(hidden, ["check_fire_doors"]);
  assert.equal(listed.get("get_current_view_info"), true);
});

test("every group hides nothing — same listing as default", () => {
  const { handles } = fakeHandles([
    "get_current_view_info",
    "check_fire_doors",
    "create_sheet",
    "trace_walls_from_cad",
  ]);

  assert.deepEqual(hideUnlistedTools(handles, [...TOOL_GROUP_NAMES]), []);
});

test("a handle that refuses to hide is logged, not fatal", () => {
  const handles = new Map<string, ToolHandle>([
    [
      "check_fire_doors",
      {
        enable: () => {},
        disable: () => {
          throw new Error("SDK said no");
        },
      },
    ],
    ["create_sheet", { enable: () => {}, disable: () => {} }],
  ]);

  // The throwing tool is left listed rather than reported as hidden — a wrong
  // count here would understate what the model still sees.
  assert.deepEqual(hideUnlistedTools(handles, []), ["create_sheet"]);
});
