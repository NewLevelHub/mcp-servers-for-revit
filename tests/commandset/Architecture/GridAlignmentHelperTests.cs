using RevitMCPCommandSet.Services;
using TUnit.Core;

namespace RevitMCPCommandSet.Tests.Architecture;

/// <summary>
/// Pure unit tests for wall-centerline clustering (no Revit document required).
/// </summary>
public class GridAlignmentHelperTests
{
    [Test]
    public async Task ClusterPositions_MergesNearbyCenterlines_WeightedByLength()
    {
        var samples = new List<(double posMm, double lengthMm)>
        {
            (-11056.0, 18000),
            (-11050.0, 5000),   // within 50 mm of first → merge
            (-7456.0, 18000),
            (-3856.0, 12000),
        };

        var clustered = GridAlignmentHelper.ClusterPositions(samples, 50);

        await Assert.That(clustered.Count).IsEqualTo(3);
        await Assert.That(Math.Abs(clustered[0] - (-11054.7))).IsLessThan(2.0);
        await Assert.That(Math.Abs(clustered[1] - (-7456.0))).IsLessThan(0.1);
        await Assert.That(Math.Abs(clustered[2] - (-3856.0))).IsLessThan(0.1);
    }

    [Test]
    public async Task ClusterPositions_KeepsDistinctAxes()
    {
        var samples = new List<(double posMm, double lengthMm)>
        {
            (0, 10000),
            (3600, 10000),
            (7200, 10000),
        };

        var clustered = GridAlignmentHelper.ClusterPositions(samples, 50);

        await Assert.That(clustered).IsEquivalentTo(new List<double> { 0, 3600, 7200 });
    }

    [Test]
    public async Task ClusterPositions_Empty_ReturnsEmpty()
    {
        var clustered = GridAlignmentHelper.ClusterPositions(
            Array.Empty<(double, double)>(),
            50);

        await Assert.That(clustered.Count).IsEqualTo(0);
    }
}
