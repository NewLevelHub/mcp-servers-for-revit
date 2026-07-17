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
