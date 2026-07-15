using RevitMCPCommandSet.Services.Normatives;
using TUnit.Core;

namespace RevitMCPCommandSet.Tests.Normatives;

/// <summary>
/// Pure unit tests for schedule-note matching (REV-47). No Revit document required.
/// </summary>
public class FireDoorScheduleNoteReaderTests
{
    [Test]
    public async Task LooksLikeFireDoorText_DetectsEi30AndRussianKeywords()
    {
        await Assert.That(
                FireDoorScheduleNoteReader.LooksLikeFireDoorText(
                    "Дверь остекленная, металлическая, противопожарная EI 30, с порогом."))
            .IsTrue();
        await Assert.That(FireDoorScheduleNoteReader.LooksLikeFireDoorText("EI-30")).IsTrue();
        await Assert.That(FireDoorScheduleNoteReader.LooksLikeFireDoorText("Обычная дверь")).IsFalse();
        await Assert.That(FireDoorScheduleNoteReader.LooksLikeFireDoorText("<варианты>")).IsFalse();
    }

    [Test]
    public async Task FindNoteForDoor_IgnoresPlaceholderAndPrefersFireNote()
    {
        var rows = new List<FireDoorScheduleNoteReader.ScheduleNoteRow>
        {
            new()
            {
                Name = "Дверный блок ДОмп 21-12 л",
                Note = "<варианты>",
                NormalizedName = FireDoorScheduleNoteReader.NormalizeForMatch("Дверной блок ДОмп 21-12 л"),
            },
            new()
            {
                Name = "Дверной блок ДОмп 21-12 л",
                Note = "Дверь остекленная, металлическая, противопожарная EI 30, с порогом.",
                NormalizedName = FireDoorScheduleNoteReader.NormalizeForMatch("Дверной блок ДОмп 21-12 л"),
            },
        };

        var note = FireDoorScheduleNoteReader.FindNoteForDoor(
            rows,
            "АС_Дверь_Двупольная_Стальная1",
            "(дверь)ДОмп_Л_2100-1200");

        await Assert.That(note).Contains("противопожарная EI 30");
    }

    [Test]
    public async Task ClassifyMarkSource_ReturnsExpectedLabels()
    {
        await Assert.That(FireDoorScheduleNoteReader.ClassifyMarkSource(true, false)).IsEqualTo("parameter");
        await Assert.That(FireDoorScheduleNoteReader.ClassifyMarkSource(false, true)).IsEqualTo("schedule_note");
        await Assert.That(FireDoorScheduleNoteReader.ClassifyMarkSource(true, true)).IsEqualTo("both");
        await Assert.That(FireDoorScheduleNoteReader.ClassifyMarkSource(false, false)).IsEqualTo("none");
    }

    [Test]
    public async Task NormalizeForMatch_MapsDoorSizeCoding()
    {
        var normalized = FireDoorScheduleNoteReader.NormalizeForMatch("(дверь)ДОмп_Л_2100-1200");
        await Assert.That(normalized).Contains("домп");
        await Assert.That(normalized).Contains("21");
        await Assert.That(normalized).Contains("12");
    }

    [Test]
    public async Task FindNoteForDoor_MatchesDompTypeToScheduleRow()
    {
        var rows = new List<FireDoorScheduleNoteReader.ScheduleNoteRow>
        {
            new()
            {
                ScheduleName = "Спецификация элементов заполнения дверных проемов",
                Name = "Дверной блок ДОмп 21-12 л",
                Note = "Дверь остекленная, металлическая, противопожарная EI 30, с порогом.",
                NormalizedName = FireDoorScheduleNoteReader.NormalizeForMatch("Дверной блок ДОмп 21-12 л"),
            },
        };

        var note = FireDoorScheduleNoteReader.FindNoteForDoor(
            rows,
            "АС_Дверь_Двупольная_Стальная1",
            "(дверь)ДОмп_Л_2100-1200");

        await Assert.That(note).Contains("противопожарная EI 30");
    }

    [Test]
    public async Task FindNoteForDoor_DoesNotMatchUnrelatedType()
    {
        var rows = new List<FireDoorScheduleNoteReader.ScheduleNoteRow>
        {
            new()
            {
                Name = "Дверной блок ДОмп 21-12 л",
                Note = "противопожарная EI 30",
                NormalizedName = FireDoorScheduleNoteReader.NormalizeForMatch("Дверной блок ДОмп 21-12 л"),
            },
        };

        var note = FireDoorScheduleNoteReader.FindNoteForDoor(
            rows,
            "Окно",
            "(окно)ОС_1500-1500");

        await Assert.That(note).IsEmpty();
    }

    [Test]
    public async Task ExtractFireRatingSnippet_PullsEiToken()
    {
        var snippet = CheckFireDoorsEventHandler.ExtractFireRatingSnippet(
            "Дверь метал., противопожарная EI 30, с порогом.");
        await Assert.That(snippet).IsEqualTo("EI30");
    }
}
