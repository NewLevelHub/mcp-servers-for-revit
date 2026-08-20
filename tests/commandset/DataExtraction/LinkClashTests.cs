using System.Collections.Generic;
using System.Linq;
using RevitMCPCommandSet.Models.Common;
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

/// <summary>
/// Folding the layers of one wall into one collision (REV-167). The numbers here are
/// the ones the live run produced on «Короткий блок»: one beam through one wall met
/// бетон 500, two layers of минвата, штукатурка and отделка as five separate elements.
/// </summary>
public class ClashLayerFoldingTests
{
    private static LinkClashItem Hit(long hostId, double depthMm, double x, double y = 0, long beam = 900)
    {
        return new LinkClashItem
        {
            HostElementId = hostId,
            HostCategory = "Стены",
            HostType = $"слой {hostId}",
            LinkElementId = beam,
            LinkInstanceId = 1,
            LinkCategory = "Каркас несущий",
            DepthMm = depthMm,
            PointMm = new JZPoint(x, y, 5000)
        };
    }

    [Test]
    public async Task LayersOfOneWall_BecomeOneRow()
    {
        var merged = ClashRules.MergeStackedHits(new List<LinkClashItem>
        {
            Hit(1, 150, -11056),
            Hit(2, 50, -11300),
            Hit(3, 50, -11350),
            Hit(4, 20, -11380),
            Hit(5, 15, -10800),
        });

        await Assert.That(merged.Count).IsEqualTo(1);
        await Assert.That(merged[0].AlsoHits!.Count).IsEqualTo(4);
    }

    [Test]
    public async Task DeepestLayer_IsTheRowThatRepresentsTheCollision()
    {
        // The structural core is what the argument with the смежник is about; the
        // 15 mm of finish is a consequence, not the subject.
        var merged = ClashRules.MergeStackedHits(new List<LinkClashItem>
        {
            Hit(5, 15, -10800),
            Hit(1, 150, -11056),
            Hit(2, 50, -11300),
        });

        await Assert.That(merged[0].HostElementId).IsEqualTo(1L);
        await Assert.That(merged[0].DepthMm).IsEqualTo(150);
    }

    [Test]
    public async Task FoldedLayers_KeepTheirOwnIdsAndDepths()
    {
        // Whoever has to move the finish needs its id, not the id of the core.
        var merged = ClashRules.MergeStackedHits(new List<LinkClashItem>
        {
            Hit(1, 150, -11056),
            Hit(7, 15, -11100),
        });

        var layer = merged[0].AlsoHits!.Single();
        await Assert.That(layer.HostElementId).IsEqualTo(7L);
        await Assert.That(layer.DepthMm).IsEqualTo(15);
        await Assert.That(layer.HostType).IsEqualTo("слой 7");
    }

    [Test]
    public async Task SameBeamBitingTwoDifferentWalls_StaysTwoRows()
    {
        // Beam 274329 of the live run: 1 mm against one wall at one end, 149 mm
        // against another six metres away. Folding those together would hide one.
        var merged = ClashRules.MergeStackedHits(new List<LinkClashItem>
        {
            Hit(1, 149, -10731, 1353),
            Hit(2, 100, -10731, -2800),
        });

        await Assert.That(merged.Count).IsEqualTo(2);
    }

    [Test]
    public async Task DifferentLinkElements_AreNeverFolded()
    {
        // Two beams through the same wall are two collisions, however close they are.
        var merged = ClashRules.MergeStackedHits(new List<LinkClashItem>
        {
            Hit(1, 150, -11056, 0, beam: 901),
            Hit(1, 150, -11056, 0, beam: 902),
        });

        await Assert.That(merged.Count).IsEqualTo(2);
    }

    [Test]
    public async Task UnmeasuredOverlap_DoesNotBecomeTheHeadOfACluster()
    {
        // An overlap Revit could not size is worth reporting, but naming the collision
        // by it would hide the 150 mm one it is folded together with.
        var unmeasured = Hit(1, 0, -11056);
        unmeasured.DepthMm = null;

        var merged = ClashRules.MergeStackedHits(new List<LinkClashItem>
        {
            unmeasured,
            Hit(2, 150, -11100),
        });

        await Assert.That(merged.Count).IsEqualTo(1);
        await Assert.That(merged[0].HostElementId).IsEqualTo(2L);
        await Assert.That(merged[0].AlsoHits!.Single().HostElementId).IsEqualTo(1L);
    }

    [Test]
    public async Task SingleClash_GetsNoAlsoHitsAtAll()
    {
        var merged = ClashRules.MergeStackedHits(new List<LinkClashItem> { Hit(1, 150, -11056) });

        await Assert.That(merged.Count).IsEqualTo(1);
        await Assert.That(merged[0].AlsoHits).IsNull();
    }

    [Test]
    public async Task EmptyInput_IsNotAFailure()
    {
        await Assert.That(ClashRules.MergeStackedHits(null).Count).IsEqualTo(0);
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
    public async Task FoldedLayers_AreAccountedForInTheMessage()
    {
        // Otherwise the count reads as a miss: the model shows eighteen overlaps and
        // the report says four.
        var result = Result(1, Clash("Стены", "Балки"), Clash("Стены", "Балки"));
        result.RawClashCount = 18;

        var message = ClashRules.BuildMessage(result);

        await Assert.That(message).Contains("18");
        await Assert.That(message).Contains("alsoHits");
    }

    [Test]
    public async Task NothingFolded_SaysNothingAboutLayers()
    {
        var result = Result(1, Clash("Стены", "Балки"));
        result.RawClashCount = 1;

        await Assert.That(ClashRules.BuildMessage(result)).DoesNotContain("alsoHits");
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
