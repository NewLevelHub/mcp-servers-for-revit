using RevitMCPCommandSet.Utils.Detailing;
using TUnit.Core;

namespace RevitMCPCommandSet.Tests.Detailing;

/// <summary>
///     Which way a section looks is the one thing that is easy to get backwards and impossible to
///     notice in a diff, so it is pinned down here rather than in Revit.
/// </summary>
public class SectionBoxBuilderTests
{
    private const double Tolerance = 1e-9;

    private static async Task AssertVector(double[] actual, double x, double y, double z)
    {
        await Assert.That(Math.Abs(actual[0] - x)).IsLessThan(Tolerance);
        await Assert.That(Math.Abs(actual[1] - y)).IsLessThan(Tolerance);
        await Assert.That(Math.Abs(actual[2] - z)).IsLessThan(Tolerance);
    }

    [Test]
    public async Task LineRunningEast_LooksNorth()
    {
        var frame = SectionBoxBuilder.FromLine(
            new[] { 0.0, 0.0, 0.0 },
            new[] { 10.0, 0.0, 0.0 },
            bottom: 0,
            top: 10,
            depth: 6);

        await AssertVector(frame.BasisX, 1, 0, 0);
        await AssertVector(frame.BasisY, 0, 0, 1);
        await AssertVector(frame.LookDirection, 0, 1, 0);
    }

    [Test]
    public async Task Flip_LooksTheOtherWayAndKeepsTheFrameRightHanded()
    {
        var frame = SectionBoxBuilder.FromLine(
            new[] { 0.0, 0.0, 0.0 },
            new[] { 10.0, 0.0, 0.0 },
            bottom: 0,
            top: 10,
            depth: 6,
            flip: true);

        await AssertVector(frame.BasisX, -1, 0, 0);
        await AssertVector(frame.LookDirection, 0, -1, 0);

        // BasisX × BasisY must still equal BasisZ, or Revit builds a mirrored view.
        var cross = new[]
        {
            frame.BasisX[1] * frame.BasisY[2] - frame.BasisX[2] * frame.BasisY[1],
            frame.BasisX[2] * frame.BasisY[0] - frame.BasisX[0] * frame.BasisY[2],
            frame.BasisX[0] * frame.BasisY[1] - frame.BasisX[1] * frame.BasisY[0]
        };

        await AssertVector(cross, frame.BasisZ[0], frame.BasisZ[1], frame.BasisZ[2]);
    }

    [Test]
    public async Task ExtentsAreCenteredOnTheLineAndClipInFrontOfTheViewer()
    {
        var frame = SectionBoxBuilder.FromLine(
            new[] { 2.0, 5.0, 0.0 },
            new[] { 12.0, 5.0, 0.0 },
            bottom: 1,
            top: 9,
            depth: 6);

        await AssertVector(frame.Origin, 7, 5, 5);
        await Assert.That(frame.MinU).IsEqualTo(-5);
        await Assert.That(frame.MaxU).IsEqualTo(5);
        await Assert.That(frame.MinV).IsEqualTo(-4);
        await Assert.That(frame.MaxV).IsEqualTo(4);

        // The viewer looks along -BasisZ, so what it sees lies at negative W.
        await Assert.That(frame.MinW).IsEqualTo(-6);
        await Assert.That(frame.MaxW).IsEqualTo(0);
    }

    [Test]
    public async Task FromBoundingBox_AlongX_SpansTheBoxWithPadding()
    {
        var frame = SectionBoxBuilder.FromBoundingBox(
            new[] { 0.0, 0.0, 0.0 },
            new[] { 20.0, 8.0, 10.0 },
            alongX: true,
            padding: 1,
            depth: 5);

        // 20 wide plus 1 padding at each end.
        await Assert.That(frame.MaxU - frame.MinU).IsEqualTo(22);
        await Assert.That(frame.MaxV - frame.MinV).IsEqualTo(12);
        await AssertVector(frame.Origin, 10, 4, 5);
        await AssertVector(frame.LookDirection, 0, 1, 0);
    }

    [Test]
    public async Task FromBoundingBox_AlongY_CutsAcrossTheOtherAxis()
    {
        var frame = SectionBoxBuilder.FromBoundingBox(
            new[] { 0.0, 0.0, 0.0 },
            new[] { 20.0, 8.0, 10.0 },
            alongX: false,
            padding: 1,
            depth: 5);

        await Assert.That(frame.MaxU - frame.MinU).IsEqualTo(10);
        await AssertVector(frame.BasisX, 0, 1, 0);
        await AssertVector(frame.LookDirection, -1, 0, 0);
    }

    [Test]
    public async Task FromBoundingBox_WithoutDepth_DerivesItFromTheBox()
    {
        var frame = SectionBoxBuilder.FromBoundingBox(
            new[] { 0.0, 0.0, 0.0 },
            new[] { 20.0, 8.0, 10.0 },
            alongX: true,
            padding: 1,
            depth: 0);

        await Assert.That(frame.MinW).IsEqualTo(-5);
    }

    [Test]
    public async Task DegenerateLine_Fails()
    {
        await Assert.That(() => SectionBoxBuilder.FromLine(
            new[] { 3.0, 3.0, 0.0 },
            new[] { 3.0, 3.0, 0.0 },
            0,
            10,
            5)).Throws<ArgumentException>();
    }

    [Test]
    public async Task TopBelowBottom_Fails()
    {
        await Assert.That(() => SectionBoxBuilder.FromLine(
            new[] { 0.0, 0.0, 0.0 },
            new[] { 10.0, 0.0, 0.0 },
            bottom: 10,
            top: 2,
            depth: 5)).Throws<ArgumentException>();
    }

    [Test]
    public async Task NonPositiveDepth_Fails()
    {
        await Assert.That(() => SectionBoxBuilder.FromLine(
            new[] { 0.0, 0.0, 0.0 },
            new[] { 10.0, 0.0, 0.0 },
            bottom: 0,
            top: 10,
            depth: 0)).Throws<ArgumentException>();
    }
}
