using RevitMCPCommandSet.Models.DataExtraction;
using RevitMCPCommandSet.Services.DataExtraction;
using RevitMCPCommandSet.Utils;
using TUnit.Core;

namespace RevitMCPCommandSet.Tests.DataExtraction;

/// <summary>
/// The parts of get_linked_models that decide an answer without a running Revit
/// (REV-166): the раздел read off a link's file name, and how a link that cannot be
/// opened is reported.
/// </summary>
/// <remarks>
/// The rest — GetTotalTransform, element counts, traversal time — only means anything
/// against a real model with a real link, and is covered by the live acceptance in
/// docs/performance.md.
/// </remarks>
public class LinkDisciplineClassifierTests
{
    [Test]
    [Arguments("Корпус1_АР.rvt", "АР")]
    [Arguments("АР_Корпус_2.rvt", "АР")]
    [Arguments("ЖК Ромашка_АС_этап2.rvt", "АР")]
    [Arguments("Tower_ARCH_r23.rvt", "АР")]
    [Arguments("Корпус1_КР.rvt", "КР")]
    [Arguments("ЖК Ромашка_КЖ0.1.rvt", "КР")]
    [Arguments("Стилобат_КМ.rvt", "КР")]
    [Arguments("Площадка_ГП.rvt", "ГП")]
    public async Task Section_IsReadFromTheFileName(string fileName, string expected)
    {
        var discipline = LinkDisciplineClassifier.Classify(fileName);
        await Assert.That(discipline.Section).IsEqualTo(expected);
        await Assert.That(discipline.Subsection).IsEqualTo(string.Empty);
    }

    [Test]
    [Arguments("Корпус_ОВ.rvt", "ОВ")]
    [Arguments("20-01-2024_ИОС4.1_ОВ.rvt", "ОВ")]
    [Arguments("Стройка_ВК_2этап.rvt", "ВК")]
    [Arguments("Корпус1_ЭОМ.rvt", "ЭОМ")]
    [Arguments("Корпус1_СС.rvt", "СС")]
    [Arguments("Корпус1_АУПТ.rvt", "СС")]
    public async Task IosTrade_IsReadTogetherWithTheSection(string fileName, string expectedTrade)
    {
        var discipline = LinkDisciplineClassifier.Classify(fileName);
        await Assert.That(discipline.Section).IsEqualTo("ИОС");
        await Assert.That(discipline.Subsection).IsEqualTo(expectedTrade);
        await Assert.That(discipline.Display).IsEqualTo($"ИОС / {expectedTrade}");
    }

    [Test]
    public async Task BareIos_KeepsTheSectionWithoutInventingATrade()
    {
        var discipline = LinkDisciplineClassifier.Classify("Корпус1_ИОС.rvt");
        await Assert.That(discipline.Section).IsEqualTo("ИОС");
        await Assert.That(discipline.Subsection).IsEqualTo(string.Empty);
        await Assert.That(discipline.Display).IsEqualTo("ИОС");
    }

    [Test]
    public async Task Reading_NamesTheTokenItCameFrom()
    {
        // The report shows this token next to the section. Without it the architect
        // has to trust the guess; with it they can see it is right at a glance.
        var discipline = LinkDisciplineClassifier.Classify("ЖК Ромашка_КЖ0.1.rvt");
        await Assert.That(discipline.MatchedToken).IsEqualTo("КЖ");
    }

    [Test]
    [Arguments("Подземный паркинг.rvt")]
    [Arguments("Корпус 1 объединённый.rvt")]
    [Arguments("2024-08-19.rvt")]
    [Arguments("")]
    [Arguments(null)]
    public async Task UnclearName_SaysNothingRatherThanGuessing(string fileName)
    {
        var discipline = LinkDisciplineClassifier.Classify(fileName);
        await Assert.That(discipline.IsKnown).IsFalse();
        await Assert.That(discipline.Display).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task FolderName_DoesNotDecideTheSection()
    {
        // An АР model stored in the ОВ folder of the exchange is still an АР model.
        var discipline = LinkDisciplineClassifier.Classify(@"C:\Обмен\ОВ\Корпус_АР.rvt");
        await Assert.That(discipline.Section).IsEqualTo("АР");
    }

    [Test]
    public async Task RevitsOwnInstanceName_IsHandledInsteadOfThrowing()
    {
        // What Element.Name gives for a link instance. It carries ':' — a character
        // the .NET Framework path helpers reject outright.
        var discipline = LinkDisciplineClassifier.Classify("Корпус_КР.rvt : 1 : location <Не общедоступное>");
        await Assert.That(discipline.Section).IsEqualTo("КР");
    }
}

/// <summary>
/// A link that cannot be opened is a status, not a failure — REV-166 acceptance:
/// «выгруженная и битая связь попадают в отчёт со статусом и не роняют команду».
/// </summary>
public class LinkedModelsStatusTests
{
    [Test]
    [Arguments("Loaded", "Загружена")]
    [Arguments("Unloaded", "Выгружена")]
    [Arguments("NotFound", "Файл не найден")]
    [Arguments("Invalid", "Ссылка повреждена")]
    public async Task Status_IsWordedForTheArchitect(string status, string expected)
    {
        await Assert.That(GetLinkedModelsEventHandler.DescribeStatus(status)).IsEqualTo(expected);
    }

    [Test]
    public async Task UnknownStatus_IsPassedThroughInsteadOfBlanked()
    {
        // Revit adds LinkedFileStatus members between versions, and this add-in builds
        // for 2020 through 2026 off one source. An unknown value must still be visible.
        await Assert.That(GetLinkedModelsEventHandler.DescribeStatus("SomeFutureStatus"))
            .IsEqualTo("SomeFutureStatus");
    }

    [Test]
    [Arguments("Unloaded")]
    [Arguments("LocallyUnloaded")]
    public async Task UnloadedLink_CountsAsUnloadedAndNotAsBroken(string status)
    {
        await Assert.That(GetLinkedModelsEventHandler.IsUnloaded(status)).IsTrue();
        await Assert.That(GetLinkedModelsEventHandler.IsBroken(status)).IsFalse();
    }

    [Test]
    [Arguments("NotFound")]
    [Arguments("Invalid")]
    [Arguments("Error")]
    public async Task BrokenLink_CountsAsBroken(string status)
    {
        await Assert.That(GetLinkedModelsEventHandler.IsBroken(status)).IsTrue();
        await Assert.That(GetLinkedModelsEventHandler.IsUnloaded(status)).IsFalse();
    }

    [Test]
    public async Task LoadedLink_IsNeitherUnloadedNorBroken()
    {
        await Assert.That(GetLinkedModelsEventHandler.IsUnloaded("Loaded")).IsFalse();
        await Assert.That(GetLinkedModelsEventHandler.IsBroken("Loaded")).IsFalse();
    }

    [Test]
    public async Task Message_NamesTheProblemLinksAndTheTraversalCost()
    {
        var result = new GetLinkedModelsResult
        {
            TotalLinks = 5,
            LoadedCount = 3,
            UnloadedCount = 1,
            BrokenCount = 1,
            ElapsedMs = 412
        };

        var message = GetLinkedModelsEventHandler.BuildMessage(result);

        await Assert.That(message).Contains("Связей: 5");
        await Assert.That(message).Contains("выгружено 1");
        await Assert.That(message).Contains("не найдено 1");
        await Assert.That(message).Contains("412 мс");
    }

    [Test]
    public async Task Message_StaysQuietWhenEveryLinkIsFine()
    {
        var result = new GetLinkedModelsResult
        {
            TotalLinks = 2,
            LoadedCount = 2,
            ElapsedMs = 7
        };

        var message = GetLinkedModelsEventHandler.BuildMessage(result);

        await Assert.That(message.Contains("выгружено")).IsFalse();
        await Assert.That(message.Contains("не найдено")).IsFalse();
    }

    [Test]
    public async Task NoLinks_IsAnAnswerOfItsOwn()
    {
        var message = GetLinkedModelsEventHandler.BuildMessage(new GetLinkedModelsResult());
        await Assert.That(message).IsEqualTo("В открытой модели нет связанных файлов Revit.");
    }
}
