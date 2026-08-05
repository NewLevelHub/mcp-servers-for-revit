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
