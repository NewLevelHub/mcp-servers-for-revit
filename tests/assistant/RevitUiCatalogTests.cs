using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using revit_mcp_plugin.Core.Assistant;
using Xunit;

namespace revit_mcp_plugin.Tests.Assistant;

/// <summary>
/// REV-151: ассистент называет место кнопки по ленте живого Revit, а не по памяти.
/// Главное, что здесь проверяется — он честно молчит, когда не нашёл.
/// </summary>
public class RevitUiCatalogTests : IDisposable
{
    private readonly string _dir;

    public RevitUiCatalogTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ui-catalog-tests-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(_dir);
        RevitUiCatalog.OverrideDirectory = _dir;
        RevitUiCatalog.ResetCache();
    }

    public void Dispose()
    {
        RevitUiCatalog.OverrideDirectory = null;
        RevitUiCatalog.ResetCache();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // Временная папка — не повод валить прогон.
        }
    }

    /// <summary>Кусок настоящей ленты Revit 2023 RU в том виде, в каком её пишет сканер.</summary>
    private void WriteCatalog()
    {
        var doc = new JObject
        {
            ["schema"] = "revit-ribbon/1",
            ["revitVersion"] = "2023",
            ["language"] = "Russian",
            ["tabs"] = new JArray
            {
                new JObject
                {
                    ["title"] = "Архитектура",
                    ["panels"] = new JArray
                    {
                        new JObject
                        {
                            ["title"] = "Построение",
                            ["items"] = new JArray
                            {
                                Button("ID_OBJECTS_WALL", "Стена: архитектурная", "WA",
                                    "Создаёт стену на активном уровне."),
                                Button("ID_OBJECTS_DOOR", "Дверь", "DR",
                                    "Вставляет дверь в существующую стену."),
                                Button("ID_OBJECTS_WINDOW", "Окно", "WN", null),
                            },
                        },
                        new JObject
                        {
                            ["title"] = "Помещение и площадь",
                            ["items"] = new JArray
                            {
                                Button("ID_OBJECTS_ROOM", "Помещение", "RM",
                                    "Создаёт помещение внутри замкнутого контура стен."),
                                Button("ID_OBJECTS_ROOM_TAG", "Марка помещения", null, null),
                            },
                        },
                    },
                },
                new JObject
                {
                    ["title"] = "Аннотация",
                    ["panels"] = new JArray
                    {
                        new JObject
                        {
                            ["title"] = "Размер",
                            ["items"] = new JArray
                            {
                                Button("ID_OBJECTS_DIMENSION", "Параллельный размер", "DI", null),
                            },
                        },
                    },
                },
            },
        };

        File.WriteAllText(Path.Combine(_dir, "ribbon-2023-russian.json"), doc.ToString());
        RevitUiCatalog.ResetCache();
    }

    private static JObject Button(string id, string text, string keyTip, string hint)
    {
        var node = new JObject
        {
            ["id"] = id,
            ["text"] = text,
            ["type"] = "RibbonButton",
        };
        if (keyTip != null)
            node["keyTip"] = keyTip;
        if (hint != null)
            node["tooltip"] = new JObject { ["content"] = hint };
        return node;
    }

    [Fact]
    public void Without_a_catalog_the_assistant_is_told_to_admit_it()
    {
        // Ни при каких условиях нельзя молча дать модели придумать вкладку.
        var result = RevitUiCatalog.Query("стена");
        Assert.True(result["catalogMissing"]?.Value<bool>());
        Assert.Equal(0, result["found"]?.Value<int>());
    }

    [Fact]
    public void Finds_the_wall_button_with_its_real_tab_and_panel()
    {
        WriteCatalog();
        var first = RevitUiCatalog.Query("где сделать стену")["commands"]?.First();

        Assert.Equal("Стена: архитектурная", first?["button"]?.ToString());
        Assert.Equal("Архитектура", first?["tab"]?.ToString());
        Assert.Equal("Построение", first?["panel"]?.ToString());
        Assert.Equal("WA", first?["hotkey"]?.ToString());
    }

    [Theory]
    [InlineData("комната", "Помещение")]
    [InlineData("перегородка", "Стена: архитектурная")]
    [InlineData("wall", "Стена: архитектурная")]
    [InlineData("марка комнаты", "Марка помещения")]
    [InlineData("размеры", "Параллельный размер")]
    public void Understands_how_a_beginner_words_it(string query, string expectedButton)
    {
        // Новичок говорит «комната», а кнопка называется «Помещение».
        WriteCatalog();
        var first = RevitUiCatalog.Query(query)["commands"]?.First();
        Assert.Equal(expectedButton, first?["button"]?.ToString());
    }

    [Fact]
    public void Nothing_found_returns_zero_instead_of_a_guess()
    {
        WriteCatalog();
        var result = RevitUiCatalog.Query("рендер панорамы облака");

        Assert.Equal(0, result["found"]?.Value<int>());
        Assert.Null(result["commands"]);
        Assert.Contains("не выдумывай", result["message"]?.ToString() ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Query_tool_returns_a_json_rpc_result()
    {
        WriteCatalog();
        var raw = RevitUiCatalog.ExecuteQueryTool("{\"query\":\"дверь\"}");
        var jo = JObject.Parse(raw);

        Assert.Null(jo["error"]);
        Assert.Equal("Дверь", jo["result"]?["commands"]?.First()?["button"]?.ToString());
        Assert.Equal("2023", jo["result"]?["revitVersion"]?.ToString());
    }

    [Fact]
    public void Tool_is_available_to_the_tutor_and_in_normal_mode()
    {
        Assert.True(ToolCatalog.IsToolAllowed("query_revit_ui", new[] { ToolCatalog.Profiles.Learning }));
        Assert.True(ToolCatalog.IsToolAllowed("query_revit_ui", Array.Empty<string>()));
    }


    [Fact]
    public void Wall_answer_carries_the_human_explanation()
    {
        // REV-152: каталог говорит ГДЕ, пояснение говорит ЗАЧЕМ и на чём спотыкаются.
        WriteCatalog();
        var first = RevitUiCatalog.Query("стена")["commands"]?.First();

        Assert.False(string.IsNullOrWhiteSpace(first?["why"]?.ToString()));
        Assert.Contains("уровень", first?["commonMistake"]?.ToString() ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Door_explains_that_it_needs_a_wall_first()
    {
        WriteCatalog();
        var first = RevitUiCatalog.Query("дверь")["commands"]?.First();

        Assert.Contains("стен", first?["before"]?.ToString() ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Buttons_without_a_written_explanation_simply_have_none()
    {
        // Пустого или выдуманного пояснения быть не должно — лучше ничего.
        WriteCatalog();
        var window = RevitUiCatalog.Query("окно")["commands"]?.First();
        Assert.NotNull(window);

        foreach (var hint in RevitUiHints.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(hint.Why), "пояснение без «зачем»");
            Assert.True(hint.Ids.Length > 0 || hint.Captions.Length > 0, "пояснение не к чему привязать");
        }
    }

    [Fact]
    public void Explanations_are_matched_by_id_even_if_the_caption_is_translated()
    {
        // Id переживает смену языка интерфейса, подпись — нет.
        var byId = RevitUiHints.For("ID_OBJECTS_ROOM", "Room");
        Assert.NotNull(byId);
        Assert.Contains("площад", byId.Why, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Tutor_playbook_sends_the_model_to_the_catalog_before_naming_a_tab()
    {
        Assert.Contains("query_revit_ui", AssistantPlaybooks.Tutor);
    }
}
