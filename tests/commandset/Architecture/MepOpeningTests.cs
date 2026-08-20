using System.Collections.Generic;
using System.Linq;
using RevitMCPCommandSet.Utils;
using TUnit.Core;

using Rect = RevitMCPCommandSet.Utils.MepOpeningRules.OpeningRect;

namespace RevitMCPCommandSet.Tests.Architecture;

/// <summary>
/// The arithmetic of «задание на отверстия» (REV-168) — the part that decides an answer
/// without a running Revit: how big a hole a pipe needs and when two holes become one.
/// </summary>
/// <remarks>
/// The geometry that produces the rectangles — the boolean intersection and the
/// projection into the plane of the wall — only means anything against a real model
/// with a real ИОС link, and is covered by the live acceptance.
/// </remarks>
public class OpeningSizeTests
{
    [Test]
    public async Task Pipe_GetsTheClearanceOnEverySide()
    {
        // A 110 mm pipe measured square through a wall, 50 mm of clearance all round,
        // rounded up to the 50 mm step: 110 + 100 = 210 → 250.
        var size = MepOpeningRules.SizeForDrawing(new Rect(0, 110, 0, 110), 50, 50);

        await Assert.That(size.WidthMm).IsEqualTo(250);
        await Assert.That(size.HeightMm).IsEqualTo(250);
    }

    [Test]
    public async Task AngledRun_GetsTheWiderHoleItActuallyNeeds()
    {
        // Crossing at an angle, the footprint on the wall face is an ellipse, so the
        // measured rectangle is wider than the pipe. Measuring rather than assuming
        // round is the whole point.
        var size = MepOpeningRules.SizeForDrawing(new Rect(0, 220, 0, 110), 50, 50);

        await Assert.That(size.WidthMm).IsEqualTo(350);
        await Assert.That(size.HeightMm).IsEqualTo(250);
    }

    [Test]
    public async Task SizeStepZero_ReturnsTheMeasuredSize()
    {
        var size = MepOpeningRules.SizeForDrawing(new Rect(0, 110, 0, 110), 50, 0);

        await Assert.That(size.WidthMm).IsEqualTo(210);
    }

    [Test]
    public async Task TinyRun_StillGetsABuildableHole()
    {
        // A 10 mm conduit with no clearance would ask for a 10 mm hole. Nobody drills a
        // задание for that, and a hole that size is not a hole.
        var size = MepOpeningRules.SizeForDrawing(new Rect(0, 10, 0, 10), 0, 0);

        await Assert.That(size.WidthMm).IsEqualTo(MepOpeningRules.MinOpeningSizeMm);
    }

    [Test]
    [Arguments(137.0, 50.0, 150.0)]
    [Arguments(150.0, 50.0, 150.0)]
    [Arguments(151.0, 50.0, 200.0)]
    [Arguments(137.0, 0.0, 137.0)]
    public async Task Sizes_AreRoundedUpNeverDown(double value, double step, double expected)
    {
        await Assert.That(MepOpeningRules.RoundUpTo(value, step)).IsEqualTo(expected);
    }
}

public class OpeningClusterTests
{
    private static List<Rect> Cluster(double gapMm, params Rect[] rects)
    {
        return MepOpeningRules.Cluster(
            rects.ToList(),
            rect => rect,
            (_, _, union) => union,
            gapMm);
    }

    [Test]
    public async Task PipesSideBySide_BecomeOneOpening()
    {
        // Three pipes 100 mm apart: one hole, not three with fins between them.
        var merged = Cluster(200,
            new Rect(0, 110, 0, 110),
            new Rect(210, 320, 0, 110),
            new Rect(420, 530, 0, 110));

        await Assert.That(merged.Count).IsEqualTo(1);
        await Assert.That(merged[0].WidthMm).IsEqualTo(530);
    }

    [Test]
    public async Task RunsFarApart_StayTheirOwnOpenings()
    {
        var merged = Cluster(200,
            new Rect(0, 110, 0, 110),
            new Rect(2000, 2110, 0, 110));

        await Assert.That(merged.Count).IsEqualTo(2);
    }

    [Test]
    public async Task MergingTwo_PullsInTheThirdThatOnlyTouchesTheResult()
    {
        // The third is 300 mm from the first — too far on its own — but only 100 from
        // the second. Folding has to keep going until nothing changes.
        var merged = Cluster(150,
            new Rect(0, 100, 0, 100),
            new Rect(200, 300, 0, 100),
            new Rect(400, 500, 0, 100));

        await Assert.That(merged.Count).IsEqualTo(1);
    }

    [Test]
    public async Task ZeroGap_KeepsOneOpeningPerRun()
    {
        var merged = Cluster(0,
            new Rect(0, 110, 0, 110),
            new Rect(150, 260, 0, 110));

        await Assert.That(merged.Count).IsEqualTo(2);
    }

    [Test]
    public async Task TouchingRuns_AreOneOpeningEvenAtZeroGap()
    {
        var merged = Cluster(0,
            new Rect(0, 110, 0, 110),
            new Rect(110, 220, 0, 110));

        await Assert.That(merged.Count).IsEqualTo(1);
    }

    [Test]
    public async Task DiagonalNeighbours_AreJudgedOnBothAxes()
    {
        // 150 mm apart along the wall and 150 mm up it is 212 mm away, not 150 — a
        // per-axis test would merge holes that are further apart than they look.
        await Assert.That(Cluster(200, new Rect(0, 100, 0, 100), new Rect(250, 350, 250, 350)).Count)
            .IsEqualTo(2);
        await Assert.That(Cluster(250, new Rect(0, 100, 0, 100), new Rect(250, 350, 250, 350)).Count)
            .IsEqualTo(1);
    }

    [Test]
    public async Task OneRun_IsLeftAlone()
    {
        await Assert.That(Cluster(200, new Rect(0, 110, 0, 110)).Count).IsEqualTo(1);
    }
}

public class OpeningMarkTests
{
    [Test]
    public async Task Mark_PutsTheLevelFirst()
    {
        // That is how a монтажник looks for it on site.
        await Assert.That(MepOpeningRules.BuildMark("2 этаж", 3)).IsEqualTo("ОТВ-2эт-03");
    }

    [Test]
    public async Task Mark_WithoutALevel_IsStillUnique()
    {
        await Assert.That(MepOpeningRules.BuildMark(null, 7)).IsEqualTo("ОТВ-07");
    }

    [Test]
    [Arguments("2 этаж", "2эт")]
    [Arguments("Уровень 1", "Уровень1")]
    [Arguments("-1 этаж", "-1эт")]
    public async Task LevelName_IsCompactedToFitATag(string levelName, string expected)
    {
        await Assert.That(MepOpeningRules.Compact(levelName)).IsEqualTo(expected);
    }
}

public class OpeningRectTests
{
    [Test]
    public async Task Gap_IsZeroForOverlappingRectangles()
    {
        await Assert.That(new Rect(0, 100, 0, 100).GapTo(new Rect(50, 150, 50, 150))).IsEqualTo(0);
    }

    [Test]
    public async Task Union_CoversBoth()
    {
        var union = new Rect(0, 100, 0, 100).Union(new Rect(200, 300, -50, 50));

        await Assert.That(union.WidthMm).IsEqualTo(300);
        await Assert.That(union.HeightMm).IsEqualTo(150);
        await Assert.That(union.CentreU).IsEqualTo(150);
    }

    [Test]
    public async Task Rectangle_NormalisesReversedInput()
    {
        var rect = new Rect(100, 0, 100, 0);

        await Assert.That(rect.WidthMm).IsEqualTo(100);
        await Assert.That(rect.MinU).IsEqualTo(0);
    }
}
