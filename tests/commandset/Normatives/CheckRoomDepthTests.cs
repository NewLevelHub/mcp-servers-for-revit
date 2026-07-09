using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Nice3point.TUnit.Revit;
using Nice3point.TUnit.Revit.Executors;
using RevitMCPCommandSet.Services.Normatives;
using RevitMCPCommandSet.Utils;
using TUnit.Core;
using TUnit.Core.Executors;

namespace RevitMCPCommandSet.Tests.Normatives;

public class CheckRoomDepthTests : RevitApiTest
{
    private static Document _doc;
    private static Level _level;
    private static Room _room;

    [Before(HookType.Class)]
    [HookExecutor<RevitThreadExecutor>]
    public static void Setup()
    {
        _doc = Application.NewProjectDocument(UnitSystem.Imperial);

        using var tx = new Transaction(_doc, "Setup Check Room Depth Tests");
        tx.Start();

        _level = Level.Create(_doc, 0.0);
        _level.Name = "Depth Check Level";

        var floorPlanType = new FilteredElementCollector(_doc)
            .OfClass(typeof(ViewFamilyType))
            .Cast<ViewFamilyType>()
            .FirstOrDefault(vft => vft.ViewFamily == ViewFamily.FloorPlan);

        if (floorPlanType != null)
        {
            ViewPlan.Create(_doc, floorPlanType.Id, _level.Id);
        }

        // Rectangle 12ft x 8ft room: depth = 12ft ≈ 3657.6 mm, width = 8ft ≈ 2438.4 mm.
        var p1 = new XYZ(0, 0, 0);
        var p2 = new XYZ(12, 0, 0);
        var p3 = new XYZ(12, 8, 0);
        var p4 = new XYZ(0, 8, 0);

        Wall.Create(_doc, Line.CreateBound(p1, p2), _level.Id, false);
        Wall.Create(_doc, Line.CreateBound(p2, p3), _level.Id, false);
        Wall.Create(_doc, Line.CreateBound(p3, p4), _level.Id, false);
        Wall.Create(_doc, Line.CreateBound(p4, p1), _level.Id, false);

        _room = _doc.Create.NewRoom(_level, new UV(6, 4));
        _room?.get_Parameter(BuiltInParameter.ROOM_NAME)?.Set("Жилая комната Тест");

        tx.Commit();
    }

    [After(HookType.Class)]
    [HookExecutor<RevitThreadExecutor>]
    public static void Cleanup()
    {
        _doc?.Close(false);
    }

    [Test]
    public async Task RoomFootprint_RectangularRoom_DepthIsLargerSpan()
    {
        await Assert.That(_room).IsNotNull();

        var (widthMm, depthMm) = RoomFootprintCalculator.Calculate(_room!);

        await Assert.That(depthMm).IsGreaterThanOrEqualTo(widthMm);
        // 12ft = 3657.6 mm outer span; room boundary is inside walls, allow tolerance.
        await Assert.That(depthMm).IsGreaterThan(3000);
        await Assert.That(depthMm).IsLessThan(3700);
        await Assert.That(widthMm).IsGreaterThan(2000);
    }

    [Test]
    public async Task DepthCompliance_MinLimit_DetectsViolationAndCompliance()
    {
        var (_, depthMm) = RoomFootprintCalculator.Calculate(_room!);

        // Норма глубже фактической — нарушение.
        await Assert.That(CheckRoomDepthEventHandler.IsDepthCompliant(depthMm, 4000, null)).IsFalse();
        var deviation = CheckRoomDepthEventHandler.CalculateDeviationMm(depthMm, 4000, null);
        await Assert.That(deviation).IsGreaterThan(0);
        await Assert.That(deviation).IsEqualTo(4000 - depthMm);

        // Норма мельче фактической — соответствие.
        await Assert.That(CheckRoomDepthEventHandler.IsDepthCompliant(depthMm, 3000, null)).IsTrue();
        await Assert.That(CheckRoomDepthEventHandler.CalculateDeviationMm(depthMm, 3000, null)).IsEqualTo(0);
    }

    [Test]
    public async Task DepthCompliance_MaxLimit_DetectsExcessDepth()
    {
        var (_, depthMm) = RoomFootprintCalculator.Calculate(_room!);

        await Assert.That(CheckRoomDepthEventHandler.IsDepthCompliant(depthMm, null, 3000)).IsFalse();
        await Assert.That(CheckRoomDepthEventHandler.CalculateDeviationMm(depthMm, null, 3000))
            .IsEqualTo(depthMm - 3000);

        await Assert.That(CheckRoomDepthEventHandler.IsDepthCompliant(depthMm, null, 4000)).IsTrue();
    }

    [Test]
    public async Task DepthCompliance_MinAndMaxRange_Works()
    {
        var (_, depthMm) = RoomFootprintCalculator.Calculate(_room!);

        await Assert.That(CheckRoomDepthEventHandler.IsDepthCompliant(depthMm, 3000, 4000)).IsTrue();
        await Assert.That(CheckRoomDepthEventHandler.IsDepthCompliant(depthMm, 3800, 4000)).IsFalse();
        await Assert.That(CheckRoomDepthEventHandler.IsDepthCompliant(depthMm, 1000, 2000)).IsFalse();
    }
}
