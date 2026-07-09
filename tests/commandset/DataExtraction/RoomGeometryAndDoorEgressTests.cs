using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Nice3point.TUnit.Revit;
using Nice3point.TUnit.Revit.Executors;
using RevitMCPCommandSet.Utils;
using TUnit.Core;
using TUnit.Core.Executors;

namespace RevitMCPCommandSet.Tests.DataExtraction;

public class RoomGeometryAndDoorEgressTests : RevitApiTest
{
    private static Document _doc;
    private static Level _level;
    private static Room _room;

    [Before(HookType.Class)]
    [HookExecutor<RevitThreadExecutor>]
    public static void Setup()
    {
        _doc = Application.NewProjectDocument(UnitSystem.Imperial);

        using var tx = new Transaction(_doc, "Setup Room Geometry Tests");
        tx.Start();

        _level = Level.Create(_doc, 0.0);
        _level.Name = "Geometry Test Level";

        var floorPlanType = new FilteredElementCollector(_doc)
            .OfClass(typeof(ViewFamilyType))
            .Cast<ViewFamilyType>()
            .FirstOrDefault(vft => vft.ViewFamily == ViewFamily.FloorPlan);

        if (floorPlanType != null)
        {
            ViewPlan.Create(_doc, floorPlanType.Id, _level.Id);
        }

        // Rectangle 12ft x 8ft room enclosure.
        var p1 = new XYZ(0, 0, 0);
        var p2 = new XYZ(12, 0, 0);
        var p3 = new XYZ(12, 8, 0);
        var p4 = new XYZ(0, 8, 0);

        Wall.Create(_doc, Line.CreateBound(p1, p2), _level.Id, false);
        Wall.Create(_doc, Line.CreateBound(p2, p3), _level.Id, false);
        Wall.Create(_doc, Line.CreateBound(p3, p4), _level.Id, false);
        Wall.Create(_doc, Line.CreateBound(p4, p1), _level.Id, false);

        _room = _doc.Create.NewRoom(_level, new UV(6, 4));
        _room?.get_Parameter(BuiltInParameter.ROOM_NAME)?.Set("Коридор Тест");

        tx.Commit();
    }

    [After(HookType.Class)]
    [HookExecutor<RevitThreadExecutor>]
    public static void Cleanup()
    {
        _doc?.Close(false);
    }

    [Test]
    public async Task RoomGeometryMetrics_RectangularRoom_WidthDepthAreaComputed()
    {
        await Assert.That(_room).IsNotNull();

        var options = new SpatialElementBoundaryOptions();
        var boundaries = _room!.GetBoundarySegments(options);
        await Assert.That(boundaries).IsNotNull();
        await Assert.That(boundaries.Count).IsGreaterThan(0);

        var points = new List<XYZ>();
        foreach (var loop in boundaries)
        {
            foreach (var segment in loop)
            {
                var curve = segment.GetCurve();
                points.Add(curve.GetEndPoint(0));
                points.Add(curve.GetEndPoint(1));
            }
        }

        var minX = points.Min(p => p.X);
        var maxX = points.Max(p => p.X);
        var minY = points.Min(p => p.Y);
        var maxY = points.Max(p => p.Y);

        var xSpanMm = RevitUnitConversion.ToMillimeters(maxX - minX);
        var ySpanMm = RevitUnitConversion.ToMillimeters(maxY - minY);
        var widthMm = Math.Min(xSpanMm, ySpanMm);
        var depthMm = Math.Max(xSpanMm, ySpanMm);
        var areaM2 = RevitUnitConversion.ToSquareMeters(_room.Area);

        await Assert.That(widthMm).IsGreaterThan(2000);
        await Assert.That(depthMm).IsGreaterThan(widthMm);
        await Assert.That(areaM2).IsGreaterThan(0);
    }

    [Test]
    public async Task DoorEgressInfo_DoorCollector_ExecutesWithoutErrors()
    {
        var doors = new FilteredElementCollector(_doc)
            .OfCategory(BuiltInCategory.OST_Doors)
            .WhereElementIsNotElementType()
            .OfType<FamilyInstance>()
            .ToList();

        await Assert.That(doors.Count).IsGreaterThanOrEqualTo(0);
    }
}
