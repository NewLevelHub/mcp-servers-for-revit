using RevitMCPCommandSet.Services.AnnotationComponents;
using TUnit.Core;

namespace RevitMCPCommandSet.Tests.Annotation;

public class DimensionGridsOffsetTests
{
    [Test]
    public async Task ExteriorLine_Bottom_IsBelowEnvelopeMin()
    {
        var y = DimensionGridsEventHandler.ComputeExteriorLineCoordinate(
            envMinMm: -9700,
            envMaxMm: 11500,
            offsetMm: 1200,
            towardMin: true);

        await Assert.That(y).IsEqualTo(-10900);
    }

    [Test]
    public async Task ExteriorLine_Left_IsLeftOfEnvelopeMin()
    {
        var x = DimensionGridsEventHandler.ComputeExteriorLineCoordinate(
            envMinMm: -12300,
            envMaxMm: 12000,
            offsetMm: 1200,
            towardMin: true);

        await Assert.That(x).IsEqualTo(-13500);
    }

    [Test]
    public async Task ExteriorLine_Top_IsAboveEnvelopeMax()
    {
        var y = DimensionGridsEventHandler.ComputeExteriorLineCoordinate(
            envMinMm: -9700,
            envMaxMm: 11500,
            offsetMm: 2000,
            towardMin: false);

        await Assert.That(y).IsEqualTo(13500);
    }

    [Test]
    public async Task OverallTier_IsFurtherOutThanInterAxis()
    {
        const double envMin = -9700;
        const double first = 1200;
        const double gap = 800;

        var inter = DimensionGridsEventHandler.ComputeExteriorLineCoordinate(envMin, 0, first, true);
        var overall = DimensionGridsEventHandler.ComputeExteriorLineCoordinate(envMin, 0, first + gap, true);

        await Assert.That(overall).IsLessThan(inter);
        await Assert.That(inter - overall).IsEqualTo(gap);
    }

    [Test]
    public async Task OpeningTier_IsCloserThanInterAxis_WithDefaults()
    {
        const double envMin = -9700;
        const double first = 1200;
        const double gap = 800;

        var openingOffset = OpeningFacadeDimensionCollector.ComputeOpeningOffsetMm(first, gap);
        await Assert.That(openingOffset).IsEqualTo(400);

        var opening = DimensionGridsEventHandler.ComputeExteriorLineCoordinate(envMin, 0, openingOffset, true);
        var inter = DimensionGridsEventHandler.ComputeExteriorLineCoordinate(envMin, 0, first, true);
        var overall = DimensionGridsEventHandler.ComputeExteriorLineCoordinate(envMin, 0, first + gap, true);

        await Assert.That(opening).IsGreaterThan(inter);
        await Assert.That(inter).IsGreaterThan(overall);
        await Assert.That(opening - inter).IsEqualTo(first - openingOffset);
        await Assert.That(inter - overall).IsEqualTo(gap);
    }

    [Test]
    [Arguments(50, 700d, 1100d, 1500d)]
    [Arguments(100, 1400d, 2200d, 3000d)]
    [Arguments(200, 2800d, 4400d, 6000d)]
    public async Task AutoLadder_ScalesWithTheView(
        int scale, double opening, double interAxis, double overall)
    {
        var ladder = OpeningFacadeDimensionCollector.ComputeTierLadderMm(scale, 0, 0);

        await Assert.That(ladder.Opening).IsEqualTo(opening);
        await Assert.That(ladder.InterAxis).IsEqualTo(interAxis);
        await Assert.That(ladder.Overall).IsEqualTo(overall);
    }

    [Test]
    [Arguments(50)]
    [Arguments(100)]
    [Arguments(200)]
    public async Task AutoLadder_ReadsTheSameOnPaper(int scale)
    {
        var ladder = OpeningFacadeDimensionCollector.ComputeTierLadderMm(scale, 0, 0);

        // The whole point: on paper the ladder is 14 / 22 / 30 mm at every scale.
        // The old fixed 400/1200/2000 gave 4/12/20 mm at 1:100 and 2/6/10 at 1:200,
        // which is what put the inner chain on top of the window marks.
        await Assert.That(ladder.Opening / scale).IsEqualTo(14d);
        await Assert.That(ladder.InterAxis / scale).IsEqualTo(22d);
        await Assert.That(ladder.Overall / scale).IsEqualTo(30d);
    }

    [Test]
    public async Task AutoLadder_ClearsTheOldInnerChain_At1To100()
    {
        var ladder = OpeningFacadeDimensionCollector.ComputeTierLadderMm(100, 0, 0);
        var oldOpening = OpeningFacadeDimensionCollector.ComputeOpeningOffsetMm(1200, 800);

        await Assert.That(ladder.Opening - oldOpening).IsEqualTo(1000d);
    }

    [Test]
    public async Task PinnedFirstOffset_KeepsTheLadderAnchoredOnIt()
    {
        var ladder = OpeningFacadeDimensionCollector.ComputeTierLadderMm(100, 1200, 800);

        await Assert.That(ladder.InterAxis).IsEqualTo(1200d);
        await Assert.That(ladder.Opening).IsEqualTo(400d);
        await Assert.That(ladder.Overall).IsEqualTo(2000d);
    }

    [Test]
    public async Task PinnedFirstOffset_StillScalesTheGapWhenOnlyGapIsOmitted()
    {
        var ladder = OpeningFacadeDimensionCollector.ComputeTierLadderMm(200, 1200, 0);

        await Assert.That(ladder.InterAxis).IsEqualTo(1200d);
        await Assert.That(ladder.Overall - ladder.InterAxis).IsEqualTo(1600d);
    }

    [Test]
    public async Task OpeningOffset_ClampsToMinimum300()
    {
        // first < gap would go negative — clamp to 300
        var offset = OpeningFacadeDimensionCollector.ComputeOpeningOffsetMm(500, 800);
        await Assert.That(offset).IsEqualTo(300);
    }

    [Test]
    public async Task DedupAndSort_DropsNearDuplicates()
    {
        var points = new List<(string Item, double PositionMm)>
        {
            ("c", 2000),
            ("a", 0),
            ("a2", 30),
            ("b", 1000),
            ("b2", 1040),
        };

        var result = OpeningFacadeDimensionCollector.DedupAndSort(points, toleranceMm: 50);

        await Assert.That(result.Count).IsEqualTo(3);
        await Assert.That(result[0].Item).IsEqualTo("a");
        await Assert.That(result[1].Item).IsEqualTo("b");
        await Assert.That(result[2].Item).IsEqualTo("c");
    }

    [Test]
    public async Task DedupPreferExterior_KeepsOuterFaceInCluster()
    {
        // Inner face at 0 and outer at 30 (wall thickness) — keep outer (min) at start.
        var points = new List<(string Item, double PositionMm)>
        {
            ("inner-left", 0),
            ("outer-left", -200),
            ("jamb", 3000),
            ("inner-right", 12000),
            ("outer-right", 12200),
        };

        var result = OpeningFacadeDimensionCollector.DedupAndSortPreferExterior(
            points,
            OpeningFacadeDimensionCollector.FacadeSide.Bottom,
            toleranceMm: 250);

        await Assert.That(result.Count).IsEqualTo(3);
        await Assert.That(result[0].Item).IsEqualTo("outer-left");
        await Assert.That(result[1].Item).IsEqualTo("jamb");
        await Assert.That(result[2].Item).IsEqualTo("outer-right");
    }

    [Test]
    public async Task SideDefaults_MatchWorkingDrawing()
    {
        await Assert.That(DimensionGridsEventHandler.IsBottomSide(null)).IsTrue();
        await Assert.That(DimensionGridsEventHandler.IsBottomSide("bottom")).IsTrue();
        await Assert.That(DimensionGridsEventHandler.IsBottomSide("top")).IsFalse();
        await Assert.That(DimensionGridsEventHandler.IsLeftSide(null)).IsTrue();
        await Assert.That(DimensionGridsEventHandler.IsLeftSide("left")).IsTrue();
        await Assert.That(DimensionGridsEventHandler.IsLeftSide("right")).IsFalse();
    }
}
