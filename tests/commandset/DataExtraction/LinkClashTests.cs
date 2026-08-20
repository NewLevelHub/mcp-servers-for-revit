using RevitMCPCommandSet.Models.DataExtraction;
using RevitMCPCommandSet.Utils;
using TUnit.Core;

namespace RevitMCPCommandSet.Tests.DataExtraction;

/// <summary>
/// The parts of check_link_clashes that decide an answer without a running Revit
/// (REV-167): what the tolerance lets through, and how a pile of overlaps reads back
/// as a sentence.
/// </summary>
/// <remarks>
/// The geometry — GetTotalTransform, the solid filter, the boolean intersection — only
/// means anything against a real model with a real link, and is covered by the live
/// acceptance in docs/performance.md.
/// </remarks>
public class ClashToleranceTests
{
    [Test]
    public async Task Touch_ThinnerThanTheTolerance_DoesNotReachTheReport()
    {
        // The acceptance case: a 1 mm contact is a modelling slip, not a clash.
        await Assert.That(ClashRules.IsReportable(1.0, ClashRules.DefaultToleranceMm)).IsFalse();
    }

    [Test]
    [Arguments(200.0)]
    [Arguments(5.0)]
    [Arguments(4999.0)]
    public async Task RealOverlap_IsReported(double depthMm)
    {
        await Assert.That(ClashRules.IsReportable(depthMm, ClashRules.DefaultToleranceMm)).IsTrue();
    }

    [Test]
    public async Task ZeroTolerance_KeepsEvenTheThinnestContact()
    {
        await Assert.That(ClashRules.IsReportable(0.1, 0)).IsTrue();
    }

    [Test]
    public async Task UnmeasuredOverlap_IsKeptRatherThanDropped()
    {
        // Revit refused the boolean on messy geometry, but its own filter found the
        // overlap. A clash nobody can size is still a clash — dropping it silently is
        // the one way this tool must never be wrong.
        await Assert.That(ClashRules.IsReportable(null, ClashRules.DefaultToleranceMm)).IsTrue();
        await Assert.That(ClashRules.IsReportable(null, 500)).IsTrue();
    }

    [Test]
    [Arguments(-5.0, ClashRules.DefaultToleranceMm)]
    [Arguments(double.NaN, ClashRules.DefaultToleranceMm)]
    [Arguments(0.0, 0.0)]
    [Arguments(20.0, 20.0)]
    [Arguments(9000.0, ClashRules.MaxToleranceMm)]
    public async Task Tolerance_IsClampedToSomethingUsable(double requested, double expected)
    {
        await Assert.That(ClashRules.NormaliseToleranceMm(requested)).IsEqualTo(expected);
    }
}

public class ClashSummaryTests
{
    private static LinkClashItem Clash(string host, string link, double? depthMm = 100)
    {
        return new LinkClashItem
        {
            HostCategory = host,
            LinkCategory = link,
            DepthMm = depthMm
        };
    }

    [Test]
    public async Task Pairs_AreFoldedBiggestGroupFirst()
    {
        var clashes = new List<LinkClashItem>
        {
            Clash("Стены", "Трубы"),
            Clash("Двери", "Балки"),
            Clash("Стены", "Трубы"),
            Clash("Стены", "Трубы"),
            Clash("Двери", "Балки"),
        };

        var summary = ClashRules.Summarise(clashes);

        await Assert.That(summary.Count).IsEqualTo(2);
        await Assert.That(summary[0].HostCategory).IsEqualTo("Стены");
        await Assert.That(summary[0].LinkCategory).IsEqualTo("Трубы");
        await Assert.That(summary[0].Count).IsEqualTo(3);
        await Assert.That(summary[1].Count).IsEqualTo(2);
    }

    [Test]
    public async Task Pair_CarriesTheDeepestOverlapOfTheGroup()
    {
        var clashes = new List<LinkClashItem>
        {
            Clash("Стены", "Балки", 40),
            Clash("Стены", "Балки", 260.44),
            Clash("Стены", "Балки", 120),
        };

        var summary = ClashRules.Summarise(clashes);

        await Assert.That(summary[0].MaxDepthMm).IsEqualTo(260.4);
    }

    [Test]
    public async Task Pair_WithNoMeasurableDepth_StillCounts()
    {
        var summary = ClashRules.Summarise(new List<LinkClashItem>
        {
            Clash("Перекрытия", "Воздуховоды", null),
            Clash("Перекрытия", "Воздуховоды", null),
        });

        await Assert.That(summary[0].Count).IsEqualTo(2);
        await Assert.That(summary[0].MaxDepthMm).IsNull();
    }

    [Test]
    public async Task Summary_IsCappedSoTheHeaderStaysReadable()
    {
        var clashes = new List<LinkClashItem>();
        for (var i = 0; i < 20; i++)
            clashes.Add(Clash($"Категория {i}", "Трубы"));

        await Assert.That(ClashRules.Summarise(clashes).Count).IsEqualTo(ClashRules.SummaryPairLimit);
        await Assert.That(ClashRules.Summarise(clashes, 3).Count).IsEqualTo(3);
    }

    [Test]
    public async Task EmptyInput_IsNotAFailure()
    {
        await Assert.That(ClashRules.Summarise(null).Count).IsEqualTo(0);
        await Assert.That(ClashRules.Summarise(new List<LinkClashItem>()).Count).IsEqualTo(0);
    }
}

public class ClashMessageTests
{
    private static CheckLinkClashesResult Result(int scannedLinks, params LinkClashItem[] clashes)
    {
        var result = new CheckLinkClashesResult
        {
            ToleranceMm = ClashRules.DefaultToleranceMm,
            Clashes = clashes.ToList(),
            TotalClashes = clashes.Length,
            HostElementsScanned = 120
        };

        for (var i = 0; i < scannedLinks; i++)
            result.Links.Add(new LinkClashScanInfo { LinkName = $"Корпус_КР{i}.rvt", Scanned = true });

        result.ByCategoryPair = ClashRules.Summarise(result.Clashes);
        return result;
    }

    private static LinkClashItem Clash(string host, string link) =>
        new LinkClashItem { HostCategory = host, LinkCategory = link, DepthMm = 100 };

    [Test]
    public async Task NoLoadedLinks_SaysSoInsteadOfSayingItIsClean()
    {
        // «Пересечений не найдено» when nothing was even read is the dangerous answer.
        var result = new CheckLinkClashesResult();
        result.Links.Add(new LinkClashScanInfo { LinkName = "Корпус_КР.rvt", Scanned = false });

        var message = ClashRules.BuildMessage(result);

        await Assert.That(message).Contains("ни одной загруженной связи");
    }

    [Test]
    public async Task CleanRun_SaysWhatWasActuallyChecked()
    {
        var result = Result(2);

        var message = ClashRules.BuildMessage(result);

        await Assert.That(message).Contains("Пересечений не найдено");
        await Assert.That(message).Contains("120");
        await Assert.That(message).Contains("5");
    }

    [Test]
    public async Task CleanRun_MentionsWhatTheToleranceSwallowed()
    {
        var result = Result(1);
        result.IgnoredBelowTolerance = 7;

        await Assert.That(ClashRules.BuildMessage(result)).Contains("7");
    }

    [Test]
    public async Task Message_NamesTheWorstCategoryPairs()
    {
        var result = Result(
            2,
            Clash("Двери", "Балки"),
            Clash("Двери", "Балки"),
            Clash("Стены", "Трубы"));

        var message = ClashRules.BuildMessage(result);

        await Assert.That(message).Contains("Пересечений: 3");
        await Assert.That(message).Contains("Двери ↔ Балки — 2");
        await Assert.That(message).Contains("Стены ↔ Трубы — 1");
    }

    [Test]
    public async Task TruncatedRun_SaysTheAnswerIsPartial()
    {
        var result = Result(1, Clash("Стены", "Трубы"));
        result.Truncated = true;

        await Assert.That(ClashRules.BuildMessage(result)).Contains("часть модели");
    }

    [Test]
    [Arguments(1, "связи")]
    [Arguments(2, "связях")]
    [Arguments(5, "связях")]
    [Arguments(11, "связях")]
    [Arguments(21, "связи")]
    public async Task LinkCount_ReadsAsRussianRatherThanAsALogLine(int count, string expected)
    {
        await Assert.That(ClashRules.LinkWord(count)).IsEqualTo(expected);
    }

    [Test]
    public async Task NullResult_IsNotAFailure()
    {
        await Assert.That(ClashRules.BuildMessage(null)).IsEqualTo(string.Empty);
    }
}
