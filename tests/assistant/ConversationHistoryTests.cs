using System.Linq;
using Newtonsoft.Json.Linq;
using revit_mcp_plugin.Core.Assistant;
using Xunit;

namespace revit_mcp_plugin.Tests.Assistant;

public class ConversationHistoryTests
{
    private static ConversationHistory NewHistory(int maxPrevious = 12)
    {
        var h = new ConversationHistory
        {
            MaxPreviousUserTurns = maxPrevious,
            MaxHistoryChars = ConversationHistory.DefaultMaxHistoryChars
        };
        h.EnsureSystemPrompt("system");
        return h;
    }

    private static void AddUser(ConversationHistory h, string text) =>
        h.Add(new JObject { ["role"] = "user", ["content"] = text });

    private static void AddAssistantTools(ConversationHistory h, string callId, string toolName)
    {
        h.Add(new JObject
        {
            ["role"] = "assistant",
            ["content"] = "",
            ["tool_calls"] = new JArray
            {
                new JObject
                {
                    ["id"] = callId,
                    ["type"] = "function",
                    ["function"] = new JObject
                    {
                        ["name"] = toolName,
                        ["arguments"] = "{}"
                    }
                }
            }
        });
    }

    private static void AddTool(ConversationHistory h, string callId, string content) =>
        h.Add(new JObject
        {
            ["role"] = "tool",
            ["tool_call_id"] = callId,
            ["content"] = content
        });

    [Fact]
    public void Trim_keeps_latest_user_when_over_turn_budget()
    {
        var h = NewHistory(maxPrevious: 4); // allow 5 user turns total
        for (var i = 1; i <= 7; i++)
        {
            AddUser(h, $"turn-{i} keyphrase-ADSK_Основной_2мм");
            h.Add(new JObject { ["role"] = "assistant", ["content"] = $"ok-{i}" });
        }

        var dropped = h.TrimIfNeeded();
        Assert.True(dropped);

        var users = h.SnapshotMessages().Where(m => ConversationHistory.IsRole(m, "user")).ToList();
        Assert.True(users.Count <= 5);
        Assert.Contains("turn-7", users.Last()["content"]!.ToString());
        Assert.DoesNotContain(users, m => m["content"]!.ToString().Contains("turn-1"));
    }

    [Fact]
    public void Trim_summarizes_dropped_turn_preserving_keyphrase()
    {
        var h = NewHistory(maxPrevious: 2); // allow 3 user turns
        AddUser(h, "[КОНТЕКСТ] doc\n\n[Запрос]\nтип размера ADSK_Основной_2 мм, только квартиры");
        h.Add(new JObject { ["role"] = "assistant", ["content"] = "принял" });
        AddUser(h, "теперь больше отступ");
        h.Add(new JObject { ["role"] = "assistant", ["content"] = "ok" });
        AddUser(h, "только слева");
        h.Add(new JObject { ["role"] = "assistant", ["content"] = "ok" });
        AddUser(h, "верни как было");

        Assert.True(h.TrimIfNeeded());
        var summaries = h.SnapshotSummaries();
        Assert.NotEmpty(summaries);
        Assert.Contains(summaries, s => s.Contains("ADSK_Основной_2 мм") || s.Contains("только квартиры"));

        var api = h.CloneForApi();
        var apiText = string.Join("\n", api.Select(t => t["content"]?.ToString() ?? ""));
        Assert.Contains("СВОДКА", apiText);
    }

    [Fact]
    public void Sanitize_removes_orphan_tool_without_matching_call()
    {
        var h = NewHistory();
        AddUser(h, "hi");
        AddTool(h, "orphan-1", "{\"ok\":true}");
        h.SanitizeToolPairs();

        Assert.DoesNotContain(h.SnapshotMessages(), m => ConversationHistory.IsRole(m, "tool"));
    }

    [Fact]
    public void Sanitize_removes_incomplete_assistant_tool_calls()
    {
        var h = NewHistory();
        AddUser(h, "hi");
        AddAssistantTools(h, "c1", "create_room");
        // missing tool result for c1
        h.SanitizeToolPairs();

        Assert.DoesNotContain(h.SnapshotMessages(), m => ConversationHistory.IsRole(m, "assistant"));
    }

    [Fact]
    public void Trim_never_leaves_tool_call_without_result()
    {
        var h = NewHistory(maxPrevious: 1); // allow 2 user turns
        AddUser(h, "first");
        AddAssistantTools(h, "c1", "create_line_based_element");
        AddTool(h, "c1", "{\"ok\":true,\"summary\":\"12 стен\",\"count\":12}");
        AddUser(h, "second");
        AddAssistantTools(h, "c2", "create_room");
        AddTool(h, "c2", "{\"ok\":true,\"count\":3}");
        AddUser(h, "third");

        h.TrimIfNeeded();
        h.SanitizeToolPairs();

        var msgs = h.SnapshotMessages();
        for (var i = 0; i < msgs.Count; i++)
        {
            if (!ConversationHistory.IsRole(msgs[i], "assistant"))
                continue;
            var calls = msgs[i]["tool_calls"] as JArray;
            if (calls == null || calls.Count == 0) continue;

            var needed = calls.Select(c => c["id"]!.ToString()).ToHashSet();
            var j = i + 1;
            while (j < msgs.Count && ConversationHistory.IsRole(msgs[j], "tool"))
            {
                needed.Remove(msgs[j]["tool_call_id"]!.ToString());
                j++;
            }

            Assert.Empty(needed);
        }
    }

    [Fact]
    public void Char_budget_compacts_oversized_tool_without_breaking_json_pairs()
    {
        var h = new ConversationHistory
        {
            MaxPreviousUserTurns = 12,
            MaxHistoryChars = 2000
        };
        h.EnsureSystemPrompt("system");
        AddUser(h, "only");
        AddAssistantTools(h, "c1", "export_room_data");
        AddTool(h, "c1", "{\"ok\":true,\"summary\":\"" + new string('x', 3000) + "\"}");

        // Only one user turn — cannot drop; must compact tool payload.
        var dropped = h.TrimIfNeeded();
        Assert.False(dropped);

        var tool = h.SnapshotMessages().First(m => ConversationHistory.IsRole(m, "tool"));
        var content = tool["content"]!.ToString();
        Assert.True(content.Length <= 400 || content.Contains("truncated"));
        Assert.NotNull(JObject.Parse(content));
    }

    [Fact]
    public void Budget_meter_reports_user_turns()
    {
        var h = NewHistory(maxPrevious: 12);
        AddUser(h, "a");
        AddUser(h, "b");
        var b = h.GetBudget();
        Assert.Equal(2, b.UserTurns);
        Assert.Equal(13, b.MaxUserTurnsInclusive);
        Assert.Equal("Контекст: 2/13", b.MeterLabel);
    }
}

public class SessionCreationJournalTests
{
    [Fact]
    public void ExtractIds_from_AIResult_Response_int_array()
    {
        var json = new JObject
        {
            ["Success"] = true,
            ["Response"] = new JArray(512, 513, 514)
        }.ToString();

        var ids = SessionCreationJournal.ExtractIds(json);
        Assert.Equal(new long[] { 512, 513, 514 }, ids);
    }

    [Fact]
    public void ExtractIds_from_objects_with_ElementId()
    {
        var json = new JObject
        {
            ["Success"] = true,
            ["Response"] = new JArray
            {
                new JObject { ["ElementId"] = 1001, ["Name"] = "Кухня" },
                new JObject { ["Id"] = 1002 }
            }
        }.ToString();

        var ids = SessionCreationJournal.ExtractIds(json);
        Assert.Contains(1001L, ids);
        Assert.Contains(1002L, ids);
    }

    [Fact]
    public void TryRecord_tracks_create_tools_and_formats_prompt()
    {
        var journal = new SessionCreationJournal();
        Assert.True(journal.TryRecord("create_line_based_element",
            new JObject { ["Success"] = true, ["Response"] = new JArray(10, 11, 12) }.ToString()));
        Assert.False(journal.TryRecord("get_current_view_info",
            new JObject { ["Success"] = true }.ToString()));

        var text = journal.FormatForPrompt();
        Assert.Contains("[ЖУРНАЛ]", text);
        Assert.Contains("create_line_based_element", text);
        Assert.Contains("10", text);
    }

    [Fact]
    public void Clear_empties_journal()
    {
        var journal = new SessionCreationJournal();
        journal.TryRecord("create_room",
            new JObject { ["Response"] = new JArray(new JObject { ["Id"] = 55 }) }.ToString());
        Assert.Equal(1, journal.EntryCount);
        journal.Clear();
        Assert.Equal(0, journal.EntryCount);
        Assert.Equal("", journal.FormatForPrompt());
    }
}
