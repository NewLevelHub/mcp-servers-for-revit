using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Nice3point.TUnit.Revit;
using Nice3point.TUnit.Revit.Executors;
using RevitMCPCommandSet.Services.Normatives;
using RevitMCPCommandSet.Utils;
using TUnit.Core;
using TUnit.Core.Executors;

namespace RevitMCPCommandSet.Tests.Normatives;

public class CheckEvacuationWidthTests : RevitApiTest
{
    private static Document _doc;
    private static Level _level;
    private static Room _corridorRoom;

    [Before(HookType.Class)]
    [HookExecutor<RevitThreadExecutor>]
    public static void Setup()
    {
        _doc = Application.NewProjectDocument(UnitSystem.Imperial);

        using var tx = new Transaction(_doc, "Setup Check Evacuation Width Tests");
        tx.Start();

        _level = Level.Create(_doc, 0.0);
        _level.Name = "Evac Width Level";

        var floorPlanType = new FilteredElementCollector(_doc)
            .OfClass(typeof(ViewFamilyType))
            .Cast<ViewFamilyType>()
            .FirstOrDefault(vft => vft.ViewFamily == ViewFamily.FloorPlan);

        if (floorPlanType != null)
        {
            ViewPlan.Create(_doc, floorPlanType.Id, _level.Id);
        }

        // Rectangle 12ft x 4ft corridor: width = 4ft ≈ 1219 mm, depth = 12ft.
        var p1 = new XYZ(0, 0, 0);
        var p2 = new XYZ(12, 0, 0);
        var p3 = new XYZ(12, 4, 0);
        var p4 = new XYZ(0, 4, 0);

        Wall.Create(_doc, Line.CreateBound(p1, p2), _level.Id, false);
        Wall.Create(_doc, Line.CreateBound(p2, p3), _level.Id, false);
        Wall.Create(_doc, Line.CreateBound(p3, p4), _level.Id, false);
        Wall.Create(_doc, Line.CreateBound(p4, p1), _level.Id, false);

        _corridorRoom = _doc.Create.NewRoom(_level, new UV(6, 2));
        _corridorRoom?.get_Parameter(BuiltInParameter.ROOM_NAME)?.Set("Эвакуационный коридор Тест");

        tx.Commit();
    }

    [After(HookType.Class)]
    [HookExecutor<RevitThreadExecutor>]
    public static void Cleanup()
    {
        _doc?.Close(false);
    }

    [Test]
    public async Task CorridorClassifier_DetectsEvacuationCorridorName()
    {
        await Assert.That(CorridorClassifier.IsEvacuationCorridor("Эвакуационный коридор")).IsTrue();
        await Assert.That(CorridorClassifier.IsEvacuationCorridor("Коридор")).IsTrue();
        await Assert.That(CorridorClassifier.IsEvacuationCorridor("Тамбур")).IsTrue();
        await Assert.That(CorridorClassifier.IsEvacuationCorridor("Лифтовый холл")).IsTrue();
        await Assert.That(CorridorClassifier.IsEvacuationCorridor("Жилая комната")).IsFalse();
    }

    [Test]
    public async Task RoomFootprint_CorridorRoom_WidthIsSmallerSpan()
    {
        await Assert.That(_corridorRoom).IsNotNull();

        var (widthMm, depthMm) = RoomFootprintCalculator.Calculate(_corridorRoom!);

        await Assert.That(widthMm).IsLessThan(depthMm);
        await Assert.That(widthMm).IsGreaterThan(1100);
        await Assert.That(widthMm).IsLessThan(1300);
    }

    [Test]
    public async Task WidthCompliance_MinLimit_DetectsViolationAndCompliance()
    {
        var (widthMm, _) = RoomFootprintCalculator.Calculate(_corridorRoom!);

        await Assert.That(CheckEvacuationWidthEventHandler.IsWidthCompliant(widthMm, 1300)).IsFalse();
        await Assert.That(CheckEvacuationWidthEventHandler.CalculateDeviationMm(widthMm, 1300))
            .IsEqualTo(1300 - widthMm);

        await Assert.That(CheckEvacuationWidthEventHandler.IsWidthCompliant(widthMm, 1100)).IsTrue();
        await Assert.That(CheckEvacuationWidthEventHandler.CalculateDeviationMm(widthMm, 1100)).IsEqualTo(0);
    }
}
