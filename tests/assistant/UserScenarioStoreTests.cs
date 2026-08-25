using System;
using System.IO;
using System.Linq;
using revit_mcp_plugin.Core.Assistant;
using Xunit;

namespace revit_mcp_plugin.Tests.Assistant;

/// <summary>
/// REV-178: a chip a user saves from chat — persists (survives a Revit restart), can be
/// renamed and deleted, and never shadows a built-in Pilot preset.
/// </summary>
public class UserScenarioStoreTests : IDisposable
{
    private readonly string _dir;

    public UserScenarioStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "user-scenarios-tests-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(_dir);
        UserScenarioStore.OverrideDirectory = _dir;
    }

    public void Dispose()
    {
        UserScenarioStore.OverrideDirectory = null;
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // Temp dir — not worth failing the run over.
        }
    }

    [Fact]
    public void Save_persists_to_disk_and_LoadAll_reads_it_back()
    {
        var saved = UserScenarioStore.Save("Мой сценарий", "Поставь 4 перегородки 100мм", "Типовой этаж");

        Assert.True(saved.IsUserCreated);
        Assert.Null(saved.Profiles);
        Assert.True(Directory.GetFiles(_dir, "*.json").Length == 1);

        var loaded = UserScenarioStore.LoadAll();
        Assert.Single(loaded);
        Assert.Equal("Мой сценарий", loaded[0].Label);
        Assert.Equal("Поставь 4 перегородки 100мм", loaded[0].Prompt);
        Assert.Equal("Типовой этаж", loaded[0].Hint);
        Assert.True(loaded[0].IsUserCreated);
    }

    [Fact]
    public void Save_requires_a_label_and_a_prompt()
    {
        Assert.Throws<ArgumentException>(() => UserScenarioStore.Save("", "prompt", null));
        Assert.Throws<ArgumentException>(() => UserScenarioStore.Save("name", "", null));
    }

    [Fact]
    public void NewId_never_collides_with_a_Pilot_id()
    {
        for (var i = 0; i < 20; i++)
        {
            var id = UserScenarioStore.NewId();
            Assert.DoesNotContain(ScenarioPresets.Pilot, p => p.Id == id);
        }
    }

    [Fact]
    public void Rename_changes_the_label_and_survives_reload()
    {
        var saved = UserScenarioStore.Save("Старое имя", "prompt text", null);

        var renamed = UserScenarioStore.Rename(saved.Id, "Новое имя");

        Assert.True(renamed);
        var loaded = UserScenarioStore.LoadAll().Single();
        Assert.Equal("Новое имя", loaded.Label);
        Assert.Equal("prompt text", loaded.Prompt); // everything else untouched
    }

    [Fact]
    public void Rename_of_a_missing_id_returns_false_instead_of_throwing()
    {
        Assert.False(UserScenarioStore.Rename("does-not-exist", "New Name"));
    }

    [Fact]
    public void Delete_removes_the_file_and_it_stops_appearing_in_LoadAll()
    {
        var saved = UserScenarioStore.Save("To delete", "prompt", null);
        Assert.Single(UserScenarioStore.LoadAll());

        var deleted = UserScenarioStore.Delete(saved.Id);

        Assert.True(deleted);
        Assert.Empty(UserScenarioStore.LoadAll());
    }

    [Fact]
    public void Delete_of_a_missing_id_returns_false_instead_of_throwing()
    {
        Assert.False(UserScenarioStore.Delete("does-not-exist"));
    }

    [Fact]
    public void A_saved_file_that_happens_to_carry_a_Pilot_id_is_never_loaded_as_a_user_scenario()
    {
        // Defensive: even a hand-edited or pre-guard file must not shadow a built-in chip.
        var pilotId = ScenarioPresets.Pilot.First().Id;
        var path = Path.Combine(_dir, pilotId + ".json");
        File.WriteAllText(path, $$"""{"Id":"{{pilotId}}","Label":"Hijack","Prompt":"x"}""");

        var loaded = UserScenarioStore.LoadAll();

        Assert.DoesNotContain(loaded, p => p.Id == pilotId);
    }

    [Fact]
    public void A_corrupt_file_does_not_take_down_the_whole_list()
    {
        UserScenarioStore.Save("Good one", "prompt", null);
        File.WriteAllText(Path.Combine(_dir, "corrupt.json"), "{not valid json");

        var loaded = UserScenarioStore.LoadAll();

        Assert.Single(loaded);
        Assert.Equal("Good one", loaded[0].Label);
    }

    [Fact]
    public void LoadAll_on_a_directory_that_does_not_exist_yet_returns_an_empty_list()
    {
        UserScenarioStore.OverrideDirectory = Path.Combine(_dir, "does-not-exist-yet");

        var loaded = UserScenarioStore.LoadAll();

        Assert.Empty(loaded);
    }
}
