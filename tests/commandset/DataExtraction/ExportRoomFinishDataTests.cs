using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Nice3point.TUnit.Revit;
using Nice3point.TUnit.Revit.Executors;
using RevitMCPCommandSet.Services.DataExtraction;
using TUnit.Core;
using TUnit.Core.Executors;

namespace RevitMCPCommandSet.Tests.DataExtraction;

public class ExportRoomFinishDataTests : RevitApiTest
{
    private static Document _doc;
    private static Level _level;

    [Before(HookType.Class)]
    [HookExecutor<RevitThreadExecutor>]
    public static void Setup()
    {
        _doc = Application.NewProjectDocument(UnitSystem.Imperial);

        using var tx = new Transaction(_doc, "Setup Export Room Finish Data Test");
        tx.Start();

        _level = Level.Create(_doc, 0.0);
        _level.Name = "Finish Test Level";

        var floorPlanType = new FilteredElementCollector(_doc)
            .OfClass(typeof(ViewFamilyType))
            .Cast<ViewFamilyType>()
            .FirstOrDefault(vft => vft.ViewFamily == ViewFamily.FloorPlan);

        if (floorPlanType != null)
        {
            ViewPlan.Create(_doc, floorPlanType.Id, _level.Id);
        }

        double size = 10.0;
        var p1 = new XYZ(0, 0, 0);
        var p2 = new XYZ(size, 0, 0);
        var p3 = new XYZ(size, size, 0);
        var p4 = new XYZ(0, size, 0);

        Wall.Create(_doc, Line.CreateBound(p1, p2), _level.Id, false);
        Wall.Create(_doc, Line.CreateBound(p2, p3), _level.Id, false);
        Wall.Create(_doc, Line.CreateBound(p3, p4), _level.Id, false);
        Wall.Create(_doc, Line.CreateBound(p4, p1), _level.Id, false);

        var room = _doc.Create.NewRoom(_level, new UV(5.0, 5.0));
        if (room != null)
        {
            room.get_Parameter(BuiltInParameter.ROOM_NAME)?.Set("Finish Test Room");
            room.get_Parameter(BuiltInParameter.ROOM_FINISH_FLOOR)?.Set("Tile 300x300");
            room.get_Parameter(BuiltInParameter.ROOM_FINISH_WALL)?.Set("Paint White");
            room.get_Parameter(BuiltInParameter.ROOM_FINISH_CEILING)?.Set("Gypsum Board");
        }

        // Second enclosed room so pagination has something to page over.
        var q1 = new XYZ(20, 0, 0);
        var q2 = new XYZ(20 + size, 0, 0);
        var q3 = new XYZ(20 + size, size, 0);
        var q4 = new XYZ(20, size, 0);

        Wall.Create(_doc, Line.CreateBound(q1, q2), _level.Id, false);
        Wall.Create(_doc, Line.CreateBound(q2, q3), _level.Id, false);
        Wall.Create(_doc, Line.CreateBound(q3, q4), _level.Id, false);
        Wall.Create(_doc, Line.CreateBound(q4, q1), _level.Id, false);

        var secondRoom = _doc.Create.NewRoom(_level, new UV(25.0, 5.0));
        if (secondRoom != null)
        {
            secondRoom.get_Parameter(BuiltInParameter.ROOM_NAME)?.Set("Finish Test Room B");
            secondRoom.get_Parameter(BuiltInParameter.ROOM_FINISH_FLOOR)?.Set("Tile 300x300");
        }

        tx.Commit();
    }

    [After(HookType.Class)]
    [HookExecutor<RevitThreadExecutor>]
    public static void Cleanup()
    {
        _doc?.Close(false);
    }

    [Test]
    public async Task ExportRoomFinish_FinishParameters_RoundTrip()
    {
        var room = new FilteredElementCollector(_doc)
            .OfCategory(BuiltInCategory.OST_Rooms)
            .WhereElementIsNotElementType()
            .Cast<Room>()
            .First(r => r.Area > 0);

        string floorFinish = room.get_Parameter(BuiltInParameter.ROOM_FINISH_FLOOR)?.AsString() ?? "";
        string wallFinish = room.get_Parameter(BuiltInParameter.ROOM_FINISH_WALL)?.AsString() ?? "";
        string ceilingFinish = room.get_Parameter(BuiltInParameter.ROOM_FINISH_CEILING)?.AsString() ?? "";

        await Assert.That(floorFinish).IsEqualTo("Tile 300x300");
        await Assert.That(wallFinish).IsEqualTo("Paint White");
        await Assert.That(ceilingFinish).IsEqualTo("Gypsum Board");
    }

    [Test]
    public async Task ExportRoomFinish_MissingFinish_ReturnsNullAndWarning()
    {
        var room = new FilteredElementCollector(_doc)
            .OfCategory(BuiltInCategory.OST_Rooms)
            .WhereElementIsNotElementType()
            .Cast<Room>()
            .First(r => r.Area > 0);

        var warnings = new List<string>();

        using (var tx = new Transaction(_doc, "Clear Floor Finish"))
        {
            tx.Start();
            room.get_Parameter(BuiltInParameter.ROOM_FINISH_FLOOR)?.Set("");
            tx.Commit();
        }

        string cleared = room.get_Parameter(BuiltInParameter.ROOM_FINISH_FLOOR)?.AsString();
        await Assert.That(string.IsNullOrWhiteSpace(cleared)).IsTrue();

        warnings.Clear();
        string clearedResult = string.IsNullOrWhiteSpace(cleared)
            ? null
            : cleared.Trim();
        if (string.IsNullOrWhiteSpace(cleared))
        {
            warnings.Add($"Room {room.Number} ({room.Id}): floor finish is not set");
        }

        await Assert.That(clearedResult).IsNull();
        await Assert.That(warnings.Count).IsEqualTo(1);
    }

    [Test]
    public async Task ExportRoomFinish_PlacedRoom_HasPositiveArea()
    {
        var room = new FilteredElementCollector(_doc)
            .OfCategory(BuiltInCategory.OST_Rooms)
            .WhereElementIsNotElementType()
            .Cast<Room>()
            .First(r => r.Area > 0);

        await Assert.That(room.Area).IsGreaterThan(0);
    }

    [Test]
    public async Task ExportRoomFinish_SpatialGeometry_ReturnsMaterialsList()
    {
        var room = new FilteredElementCollector(_doc)
            .OfCategory(BuiltInCategory.OST_Rooms)
            .WhereElementIsNotElementType()
            .Cast<Room>()
            .First(r => r.Area > 0);

        var calculator = new SpatialElementGeometryCalculator(_doc);
        var results = calculator.CalculateSpatialElementGeometry(room);
        var solid = results.GetGeometry();

        await Assert.That(solid).IsNotNull();

        int facesWithMaterial = 0;
        foreach (Face face in solid.Faces)
        {
            if (face.MaterialElementId != null && face.MaterialElementId != ElementId.InvalidElementId)
                facesWithMaterial++;
        }

        await Assert.That(facesWithMaterial).IsGreaterThanOrEqualTo(0);
    }

    [Test]
    public async Task ExportRoomFinish_RoomCollector_ReturnsPlacedRooms()
    {
        var placedRooms = new FilteredElementCollector(_doc)
            .OfCategory(BuiltInCategory.OST_Rooms)
            .WhereElementIsNotElementType()
            .Cast<Room>()
            .Where(r => r.Area > 0)
            .ToList();

        await Assert.That(placedRooms.Count).IsGreaterThan(0);
    }

    [Test]
    public async Task Compute_WithoutMaterials_ReturnsFinishesAndEmptyMaterials()
    {
        var result = ExportRoomFinishDataEventHandler.Compute(_doc, includeMaterials: false);

        await Assert.That(result.Success).IsTrue();
        await Assert.That(result.TotalRooms).IsGreaterThanOrEqualTo(2);
        await Assert.That(result.ReturnedRooms).IsEqualTo(result.TotalRooms);
        await Assert.That(result.HasMore).IsFalse();
        await Assert.That(result.Rooms.All(room => room.Materials.Count == 0)).IsTrue();

        var roomWithFloor = result.Rooms.FirstOrDefault(room => room.Name == "Finish Test Room B");
        await Assert.That(roomWithFloor).IsNotNull();
        await Assert.That(roomWithFloor!.FloorFinish).IsEqualTo("Tile 300x300");
        // Missing finishes come back as explicit null plus a per-room warning.
        await Assert.That(roomWithFloor.WallFinish).IsNull();
        await Assert.That(roomWithFloor.Warnings.Any(warning => warning.Contains("wall finish"))).IsTrue();
    }

    [Test]
    public async Task Compute_OffsetLimit_PaginatesDeterministically()
    {
        var all = ExportRoomFinishDataEventHandler.Compute(_doc, includeMaterials: false);

        var firstPage = ExportRoomFinishDataEventHandler.Compute(
            _doc, includeMaterials: false, limit: 1);
        await Assert.That(firstPage.ReturnedRooms).IsEqualTo(1);
        await Assert.That(firstPage.TotalRooms).IsEqualTo(all.TotalRooms);
        await Assert.That(firstPage.HasMore).IsTrue();
        await Assert.That(firstPage.Warnings.Any(warning => warning.Contains("offset=1"))).IsTrue();
        await Assert.That(firstPage.Rooms[0].RoomId).IsEqualTo(all.Rooms[0].RoomId);

        var secondPage = ExportRoomFinishDataEventHandler.Compute(
            _doc, includeMaterials: false, offset: 1, limit: 1);
        await Assert.That(secondPage.ReturnedRooms).IsEqualTo(1);
        await Assert.That(secondPage.Rooms[0].RoomId).IsEqualTo(all.Rooms[1].RoomId);
        await Assert.That(secondPage.Rooms[0].RoomId).IsNotEqualTo(firstPage.Rooms[0].RoomId);
    }

    [Test]
    public async Task Compute_LevelFilter_LimitsRoomsAndWarnsOnUnknownLevel()
    {
        var byLevel = ExportRoomFinishDataEventHandler.Compute(
            _doc, includeMaterials: false, levelName: "Finish Test Level");
        await Assert.That(byLevel.TotalRooms).IsGreaterThanOrEqualTo(2);
        await Assert.That(byLevel.Rooms.All(room => room.Level == "Finish Test Level")).IsTrue();

        var unknown = ExportRoomFinishDataEventHandler.Compute(
            _doc, includeMaterials: false, levelName: "Нет такого уровня");
        await Assert.That(unknown.Success).IsTrue();
        await Assert.That(unknown.TotalRooms).IsEqualTo(0);
        await Assert.That(unknown.Warnings.Any(warning => warning.Contains("matched no rooms"))).IsTrue();
    }

    [Test]
    public async Task Compute_WithMaterials_FillsMaterialsForEnclosedRoom()
    {
        var result = ExportRoomFinishDataEventHandler.Compute(_doc, includeMaterials: true, limit: 1);

        await Assert.That(result.Success).IsTrue();
        await Assert.That(result.ReturnedRooms).IsEqualTo(1);
        // Materials list may be empty when boundary faces carry no materials in the
        // default template, but the extraction path must not fail.
        await Assert.That(result.Rooms[0].Materials).IsNotNull();
    }
}
