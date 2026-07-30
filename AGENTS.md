# Инструкции для AI-агента

Проект **mcp-servers-for-revit**: связь Cursor ↔ Revit через MCP.

## Перед задачей по Revit

1. **Обязательно прочитай и выполни** [.cursor/rules/revit-mcp.mdc](.cursor/rules/revit-mcp.mdc).
2. Руководство для пользователей: [docs/user-guide-revit-mcp.md](docs/user-guide-revit-mcp.md).

## Режим по умолчанию (проектировщик)

- Только **MCP-tools** `mcp-server-for-revit`.
- **Не** использовать `send_code_to_revit` без явного согласия пользователя.
- **Не** редактировать `server/`, `plugin/`, `commandset/` при работе с моделью.
- В конце ответа — список использованных MCP-функций.

## Режим разработчика

Только если пользователь явно просит изменить код MCP, плагина или command set.

При добавлении MCP/Revit команд см. [docs/tool-registry.md](docs/tool-registry.md) и `npm run check:tool-registry` в `server/`.

### Golden set ассистента (REV-111)

Правка `plugin/Core/Assistant/AssistantSystemPrompt.cs` или `ToolCatalog.cs` **не принимается** без прогона golden set:

```powershell
cd tests/assistant
.\run-golden.ps1
# опционально с живой моделью (stub tools, без Revit):
# $env:ASSISTANT_API_KEY="…"; .\run-golden.ps1 -Live
```

Кейсы: `tests/assistant/Golden/*.json` (≥20, 7 групп). Baseline и цели метрик — `tests/assistant/Golden/baseline.json`.

### Live-приёмка тикета ассистента (обязательно предлагать)

После тикета по промпту, каталогу, роутингу или чипам — **агент сам предлагает** smoke в Revit и делает **MCP replay**: те же tools, что должен вызвать ассистент, сверка с ответом чата.

Правило: [.cursor/rules/assistant-ticket-verification.mdc](.cursor/rules/assistant-ticket-verification.mdc).

Кратко: `get_current_view_info` → целевой tool → таблица «эталон MCP vs ответ чата». Расхождение при зелёном golden → отдельный тикет (пример: REV-118 smoke → REV-132).
