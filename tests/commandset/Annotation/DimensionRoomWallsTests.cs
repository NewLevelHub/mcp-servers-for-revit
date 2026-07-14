using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Nice3point.TUnit.Revit;
using Nice3point.TUnit.Revit.Executors;
using RevitMCPCommandSet.Services.AnnotationComponents;
using TUnit.Core;
using TUnit.Core.Executors;

namespace RevitMCPCommandSet.Tests.Annotation;

public class DimensionRoomWallsTests : RevitApiTest
{
    private static Document _doc;
    private static Level _level;
    private static ViewPlan _floorPlan;
    private static Room _room;

    [Before(HookType.Class)]
    [HookExecutor<RevitThreadExecutor>]
    public static void Setup()
    {
        _doc = Application.NewProjectDocument(UnitSystem.Imperial);

        using var tx = new Transaction(_doc, "Setup Dimension Room Walls Test");
        tx.Start();

        _level = Level.Create(_doc, 0.0);
        _level.Name = "Dimension Test Level";

        var floorPlanType = new FilteredElementCollector(_doc)
            .OfClass(typeof(ViewFamilyType))
            .Cast<ViewFamilyType>()
            .FirstOrDefault(vft => vft.ViewFamily == ViewFamily.FloorPlan);

        if (floorPlanType != null)
            _floorPlan = ViewPlan.Create(_doc, floorPlanType.Id, _level.Id);

        CreateEnclosure(_doc, _level.Id, 0, 0, 10);
        _room = _doc.Create.NewRoom(_level, new UV(5.0, 5.0));

        tx.Commit();
    }

    [After(HookType.Class)]
    [HookExecutor<RevitThreadExecutor>]
    public static void Cleanup()
    {
        _doc?.Close(false);
    }

    [Test]
    public async Task DimensionRoomWalls_EnclosedRoom_HasWallBoundarySegments()
    {
        await Assert.That(_room).IsNotNull();
        await Assert.That(_room.Area).IsGreaterThan(0);

        var options = new SpatialElementBoundaryOptions
        {
            SpatialElementBoundaryLocation = SpatialElementBoundaryLocation.Finish
        };

        var loops = _room.GetBoundarySegments(options);
        await Assert.That(loops).IsNotNull();
        await Assert.That(loops.Count).IsGreaterThan(0);

        var wallSegmentCount = loops
            .SelectMany(loop => loop.Cast<BoundarySegment>())
            .Count(segment => _doc.GetElement(segment.ElementId) is Wall);

        await Assert.That(wallSegmentCount).IsGreaterThanOrEqualTo(4);
    }

    [Test]
    public async Task DimensionRoomWalls_FloorPlanView_ExistsForRoomLevel()
    {
        await Assert.That(_floorPlan).IsNotNull();
        await Assert.That(_floorPlan.GenLevel.Id).IsEqualTo(_level.Id);
    }

    [Test]
    public async Task DimensionRoomWalls_ProjectContainsLinearDimensionType()
    {
        var dimensionType = new FilteredElementCollector(_doc)
            .OfClass(typeof(DimensionType))
            .Cast<DimensionType>()
            .FirstOrDefault(type => type.StyleType == DimensionStyleType.Linear);

        await Assert.That(dimensionType).IsNotNull();
    }

    [Test]
    public async Task ChainLineCoordinate_InteriorDefault_LiesInsideRoomBounds()
    {
        // 10×10 ft room at origin, center (5,5), offset ~1 ft.
        var widthChainY = DimensionRoomWallsEventHandler.ComputeChainLineCoordinate(
            forXChain: true, interior: true, 0, 10, 0, 10, 5, 5, offsetFeet: 1.0);
        var depthChainX = DimensionRoomWallsEventHandler.ComputeChainLineCoordinate(
            forXChain: false, interior: true, 0, 10, 0, 10, 5, 5, offsetFeet: 1.0);

        await Assert.That(widthChainY).IsGreaterThan(0);
        await Assert.That(widthChainY).IsLessThan(10);
        await Assert.That(depthChainX).IsGreaterThan(0);
        await Assert.That(depthChainX).IsLessThan(10);

        // Even an oversized offset stays clamped inside the room.
        var clampedY = DimensionRoomWallsEventHandler.ComputeChainLineCoordinate(
            forXChain: true, interior: true, 0, 10, 0, 10, 5, 5, offsetFeet: 100.0);
        await Assert.That(clampedY).IsGreaterThan(0);
        await Assert.That(clampedY).IsLessThan(10);
    }

    [Test]
    public async Task ChainLineCoordinate_ExteriorRequested_LiesOutsideRoomBounds()
    {
        var widthChainY = DimensionRoomWallsEventHandler.ComputeChainLineCoordinate(
            forXChain: true, interior: false, 0, 10, 0, 10, 5, 5, offsetFeet: 1.0);
        var depthChainX = DimensionRoomWallsEventHandler.ComputeChainLineCoordinate(
            forXChain: false, interior: false, 0, 10, 0, 10, 5, 5, offsetFeet: 1.0);

        await Assert.That(widthChainY < 0 || widthChainY > 10).IsTrue();
        await Assert.That(depthChainX < 0 || depthChainX > 10).IsTrue();
    }

    [Test]
    public async Task IsInteriorPlacement_DefaultsToInteriorUnlessExplicitlyExterior()
    {
        await Assert.That(DimensionRoomWallsEventHandler.IsInteriorPlacement(null)).IsTrue();
        await Assert.That(DimensionRoomWallsEventHandler.IsInteriorPlacement("")).IsTrue();
        await Assert.That(DimensionRoomWallsEventHandler.IsInteriorPlacement("interior")).IsTrue();
        await Assert.That(DimensionRoomWallsEventHandler.IsInteriorPlacement("что-то ещё")).IsTrue();
        await Assert.That(DimensionRoomWallsEventHandler.IsInteriorPlacement("exterior")).IsFalse();
        await Assert.That(DimensionRoomWallsEventHandler.IsInteriorPlacement(" Exterior ")).IsFalse();
        await Assert.That(DimensionRoomWallsEventHandler.IsInteriorPlacement("outside")).IsFalse();
    }

    private static void CreateEnclosure(Document doc, ElementId levelId, double x, double y, double size)
    {
        var p1 = new XYZ(x, y, 0);
        var p2 = new XYZ(x + size, y, 0);
        var p3 = new XYZ(x + size, y + size, 0);
        var p4 = new XYZ(x, y + size, 0);

        Wall.Create(doc, Line.CreateBound(p1, p2), levelId, false);
        Wall.Create(doc, Line.CreateBound(p2, p3), levelId, false);
        Wall.Create(doc, Line.CreateBound(p3, p4), levelId, false);
        Wall.Create(doc, Line.CreateBound(p4, p1), levelId, false);
    }
}
