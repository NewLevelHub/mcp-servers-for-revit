#!/usr/bin/env node
// Создаёт тикеты в Linear из tasks/*.json через GraphQL API.
//
//   node scripts/linear-push.mjs --file tasks/tutor-onboarding-tickets.json --dry-run
//   node scripts/linear-push.mjs --file tasks/tutor-onboarding-tickets.json
//
// Ключ берётся из LINEAR_API_KEY или из файла %USERPROFILE%\.linear-api-key.
// Повторный запуск не дублирует: созданные id пишутся рядом в *.linear-state.json.

import { readFileSync, writeFileSync, existsSync } from "node:fs";
import { homedir } from "node:os";
import { join } from "node:path";

const API = "https://api.linear.app/graphql";

function arg(name, fallback = null) {
  const i = process.argv.indexOf(`--${name}`);
  if (i === -1) return fallback;
  const next = process.argv[i + 1];
  return next && !next.startsWith("--") ? next : true;
}

const dryRun = arg("dry-run", false) !== false;
const file = arg("file", "tasks/tutor-onboarding-tickets.json");
const statePath = file.replace(/\.json$/, ".linear-state.json");

function readKey() {
  if (process.env.LINEAR_API_KEY) return process.env.LINEAR_API_KEY.trim();
  const keyFile = join(homedir(), ".linear-api-key");
  if (existsSync(keyFile)) return readFileSync(keyFile, "utf8").trim();
  return null;
}

const key = readKey();
if (!key && !dryRun) {
  console.error(
    "Нет ключа. Положи его в %USERPROFILE%\\.linear-api-key или задай LINEAR_API_KEY.\n" +
      "Проверить план без ключа: node scripts/linear-push.mjs --dry-run"
  );
  process.exit(1);
}

async function gql(query, variables = {}) {
  const res = await fetch(API, {
    method: "POST",
    headers: { "Content-Type": "application/json", Authorization: key },
    body: JSON.stringify({ query, variables }),
  });
  const json = await res.json();
  if (json.errors) throw new Error(JSON.stringify(json.errors, null, 2));
  return json.data;
}

const doc = JSON.parse(readFileSync(file, "utf8"));
const teamKey = arg("team", doc.meta?.team ?? "REV");
const priorityMap = doc.meta?.priorityMap ?? { P0: 2, P1: 3, P2: 4 };
const state = existsSync(statePath) ? JSON.parse(readFileSync(statePath, "utf8")) : {};

function body(issue) {
  const days = issue.estimateDays ? `**Оценка:** ${issue.estimateDays} дн.\n\n` : "";
  const phase = issue.phase ? `**Этап:** ${issue.phase}\n\n` : "";
  return phase + days + (issue.description ?? "");
}

if (dryRun) {
  console.log(`ПЛАН (--dry-run), команда ${teamKey}, файл ${file}\n`);
  for (const i of doc.issues) {
    const blocked = i.blockedBy?.length ? ` ← ждёт: ${i.blockedBy.join(", ")}` : "";
    const done = state[i.slug] ? ` [уже создан: ${state[i.slug].identifier}]` : "";
    console.log(
      `  ${i.epic ? "EPIC " : "     "}${(i.phase ?? "").padEnd(14)} ${i.priority}  ${i.title}${blocked}${done}`
    );
  }
  const total = doc.issues.reduce((s, i) => s + (i.estimateDays ?? 0), 0);
  console.log(`\n  Всего задач: ${doc.issues.length}, суммарная оценка: ${total} дн.`);
  process.exit(0);
}

const team = (
  await gql(`query($key:String!){ teams(filter:{key:{eq:$key}}){ nodes{ id key name } } }`, {
    key: teamKey,
  })
).teams.nodes[0];
if (!team) throw new Error(`Команда с ключом ${teamKey} не найдена — проверь права ключа.`);
console.log(`Команда: ${team.name} (${team.key})`);

const labelNodes = (
  // first:250 — без него Linear отдаёт 50 меток, и всё, что не влезло, молча теряется
  await gql(`query($id:String!){ team(id:$id){ labels(first:250){ nodes{ id name } } } }`, {
    id: team.id,
  })
).team.labels.nodes;
const labelId = new Map(labelNodes.map((l) => [l.name.toLowerCase(), l.id]));

const CREATE = `mutation($input:IssueCreateInput!){ issueCreate(input:$input){ success issue{ id identifier url } } }`;
const RELATE = `mutation($input:IssueRelationCreateInput!){ issueRelationCreate(input:$input){ success } }`;

// Эпик первым — дети цепляются к нему через parentId.
const ordered = [...doc.issues].sort((a, b) => (b.epic ? 1 : 0) - (a.epic ? 1 : 0));

for (const issue of ordered) {
  if (state[issue.slug]) {
    console.log(`  = ${state[issue.slug].identifier}  ${issue.title} (пропуск, уже создан)`);
    continue;
  }
  const missing = (issue.labels ?? []).filter((n) => !labelId.has(n.toLowerCase()));
  if (missing.length) console.warn(`    ! нет меток в команде, пропускаю: ${missing.join(", ")}`);

  const input = {
    teamId: team.id,
    title: issue.title,
    description: body(issue),
    priority: priorityMap[issue.priority] ?? 0,
    labelIds: (issue.labels ?? []).map((n) => labelId.get(n.toLowerCase())).filter(Boolean),
  };
  const parent = issue.parent && state[issue.parent];
  if (parent) input.parentId = parent.id;

  const created = (await gql(CREATE, { input })).issueCreate.issue;
  state[issue.slug] = { id: created.id, identifier: created.identifier, url: created.url };
  writeFileSync(statePath, JSON.stringify(state, null, 2));
  console.log(`  + ${created.identifier}  ${issue.title}`);
}

for (const issue of doc.issues) {
  for (const blocker of issue.blockedBy ?? []) {
    if (!state[blocker] || !state[issue.slug]) continue;
    // issueId блокирует relatedIssueId
    await gql(RELATE, {
      input: { issueId: state[blocker].id, relatedIssueId: state[issue.slug].id, type: "blocks" },
    });
    console.log(`  ↳ ${state[blocker].identifier} блокирует ${state[issue.slug].identifier}`);
  }
}

console.log(`\nГотово. Карта slug → тикет: ${statePath}`);
