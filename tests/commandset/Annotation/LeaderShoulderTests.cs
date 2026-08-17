using RevitMCPCommandSet.Services;
using TUnit.Core;

namespace RevitMCPCommandSet.Tests.Annotation;

/// <summary>
/// Norm callouts sit in a column outside the plan and used to reach their element
/// with one straight line cutting across walls and rooms. The leader now turns:
/// a horizontal landing at the text, then a single diagonal to the element.
/// </summary>
public class LeaderShoulderTests
{
    // 6 mm on paper at 1:100 → 600 mm model → ~1.9685 ft.
    private const double ShoulderFt = 600.0 / 304.8;

    [Test]
    public async Task Right_column_landing_runs_left_toward_the_plan()
    {
        var elbow = CreateTextNotesEventHandler.ComputeLeaderElbowX(
            startXFt: 100,
            endXFt: 10,
            side: "right",
            shoulderFt: ShoulderFt);

        await Assert.That(elbow).IsNotNull();
        await Assert.That(elbow!.Value).IsEqualTo(100 - ShoulderFt).Within(1e-9);
    }

    [Test]
    public async Task Left_column_landing_runs_right_toward_the_plan()
    {
        var elbow = CreateTextNotesEventHandler.ComputeLeaderElbowX(
            startXFt: -100,
            endXFt: 10,
            side: "left",
            shoulderFt: ShoulderFt);

        await Assert.That(elbow).IsNotNull();
        await Assert.That(elbow!.Value).IsEqualTo(-100 + ShoulderFt).Within(1e-9);
    }

    [Test]
    public async Task Near_placement_without_a_column_aims_the_landing_at_the_target()
    {
        var toTheLeft = CreateTextNotesEventHandler.ComputeLeaderElbowX(
            startXFt: 0,
            endXFt: -50,
            side: "near",
            shoulderFt: ShoulderFt);

        await Assert.That(toTheLeft).IsNotNull();
        await Assert.That(toTheLeft!.Value).IsEqualTo(-ShoulderFt).Within(1e-9);

        var toTheRight = CreateTextNotesEventHandler.ComputeLeaderElbowX(
            startXFt: 0,
            endXFt: 50,
            side: "near",
            shoulderFt: ShoulderFt);

        await Assert.That(toTheRight).IsNotNull();
        await Assert.That(toTheRight!.Value).IsEqualTo(ShoulderFt).Within(1e-9);
    }

    [Test]
    public async Task Target_nearer_than_the_landing_gets_a_straight_line()
    {
        // Elbow would overshoot the element and the leader would double back.
        var elbow = CreateTextNotesEventHandler.ComputeLeaderElbowX(
            startXFt: 100,
            endXFt: 99,
            side: "right",
            shoulderFt: ShoulderFt);

        await Assert.That(elbow).IsNull();
    }

    [Test]
    public async Task Zero_shoulder_falls_back_to_a_straight_line()
    {
        var elbow = CreateTextNotesEventHandler.ComputeLeaderElbowX(
            startXFt: 100,
            endXFt: 10,
            side: "right",
            shoulderFt: 0);

        await Assert.That(elbow).IsNull();
    }
}
