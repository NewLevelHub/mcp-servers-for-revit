using Autodesk.Revit.DB;
using Nice3point.TUnit.Revit;
using Nice3point.TUnit.Revit.Executors;
using RevitMCPCommandSet.Models.Views;
using RevitMCPCommandSet.Services.Views;
using RevitMCPCommandSet.Utils;
using TUnit.Core;
using TUnit.Core.Executors;

namespace RevitMCPCommandSet.Tests.Views;

public class AutoLayoutSheetTests : RevitApiTest
{
    private static Document _doc;
    private static Level _level;
    private static ViewPlan _floorPlan;
    private static ViewSchedule _roomSchedule;
    private static ViewSchedule _secondSchedule;
    private static FamilySymbol _titleBlock;

    [Before(HookType.Class)]
    [HookExecutor<RevitThreadExecutor>]
    public static void Setup()
    {
        _doc = Application.NewProjectDocument(UnitSystem.Imperial);

        _titleBlock = new FilteredElementCollector(_doc)
            .OfCategory(BuiltInCategory.OST_TitleBlocks)
            .OfClass(typeof(FamilySymbol))
            .Cast<FamilySymbol>()
            .FirstOrDefault();

        using var tx = new Transaction(_doc, "Setup Auto Layout Sheet Test");
        tx.Start();

        if (_titleBlock != null && !_titleBlock.IsActive)
        {
            _titleBlock.Activate();
            _doc.Regenerate();
        }

        _level = Level.Create(_doc, 0.0);
        _level.Name = "Auto Layout Level";

        CreateEnclosedRoom(_doc, _level, 10.0, new UV(5.0, 5.0), "Living");

        var floorPlanType = new FilteredElementCollector(_doc)
            .OfClass(typeof(ViewFamilyType))
            .Cast<ViewFamilyType>()
            .FirstOrDefault(vft => vft.ViewFamily == ViewFamily.FloorPlan);
        if (floorPlanType != null)
        {
            _floorPlan = ViewPlan.Create(_doc, floorPlanType.Id, _level.Id);
            _floorPlan.Name = "MCP Auto Layout Plan";
        }

        var roomsCategoryId = new ElementId(BuiltInCategory.OST_Rooms);

        _roomSchedule = ViewSchedule.CreateSchedule(_doc, roomsCategoryId);
        _roomSchedule.Name = "MCP Auto Layout Schedule";
        AddField(_roomSchedule, "Number");
        AddField(_roomSchedule, "Name");

        _secondSchedule = ViewSchedule.CreateSchedule(_doc, roomsCategoryId);
        _secondSchedule.Name = "MCP Auto Layout Schedule 2";
        AddField(_secondSchedule, "Name");
        AddField(_secondSchedule, "Area");

        tx.Commit();
    }

    [After(HookType.Class)]
    [HookExecutor<RevitThreadExecutor>]
    public static void Cleanup()
    {
        _doc?.Close(false);
    }

    [Test]
    [HookExecutor<RevitThreadExecutor>]
    public async Task AutoLayout_PlanAndSchedule_PlacedWithoutOverlapInsideUsableArea()
    {
        if (_titleBlock == null || _floorPlan == null)
        {
            await Assert.That(true).IsTrue();
            return;
        }

        var info = new AutoLayoutSheetInfo
        {
            SheetName = "MCP Автокомпоновка",
            SheetNumber = "АК-1",
            Items = new List<AutoLayoutItemInfo>
            {
                new AutoLayoutItemInfo { ViewId = _floorPlan.Id.GetValue() },
                new AutoLayoutItemInfo { ViewName = "MCP Auto Layout Schedule" }
            }
        };

        var result = AutoLayoutSheetEventHandler.Layout(_doc, info);

        await Assert.That(result.Success).IsTrue();
        await Assert.That(result.SheetCreated).IsTrue();
        await Assert.That(result.PlacedCount).IsEqualTo(2);
        await Assert.That(result.SkippedCount).IsEqualTo(0);

        var placed = result.Items.Where(item => item.Placed).ToList();
        await Assert.That(placed.Count).IsEqualTo(2);
        await Assert.That(placed.Any(item => item.PlacementType == "viewport")).IsTrue();
        await Assert.That(placed.Any(item => item.PlacementType == "schedule")).IsTrue();

        // No overlap between the two placed rectangles.
        await Assert.That(Overlaps(placed[0], placed[1])).IsFalse();

        // Both inside the usable area (margins + title block zone respected).
        foreach (var item in placed)
        {
            await Assert.That(item.X).IsGreaterThanOrEqualTo(info.MarginLeft - 0.01);
            await Assert.That(item.Y)
                .IsGreaterThanOrEqualTo(info.MarginBottom + info.TitleBlockReserveBottom - 0.01);
            await Assert.That(item.Width).IsGreaterThan(0);
            await Assert.That(item.Height).IsGreaterThan(0);
        }

        // Real elements exist on the sheet.
        var sheet = (ViewSheet)_doc.GetElement(result.SheetUniqueId);
        var viewportCount = new FilteredElementCollector(_doc, sheet.Id)
            .OfClass(typeof(Viewport))
            .GetElementCount();
        var scheduleCount = new FilteredElementCollector(_doc, sheet.Id)
            .OfClass(typeof(ScheduleSheetInstance))
            .GetElementCount();
        await Assert.That(viewportCount).IsEqualTo(1);
        await Assert.That(scheduleCount).IsEqualTo(1);
    }

    [Test]
    [HookExecutor<RevitThreadExecutor>]
    public async Task AutoLayout_ExistingElementsOnSheet_AreAvoided()
    {
        if (_titleBlock == null)
        {
            await Assert.That(true).IsTrue();
            return;
        }

        var first = AutoLayoutSheetEventHandler.Layout(_doc, new AutoLayoutSheetInfo
        {
            SheetName = "MCP Автокомпоновка (обход)",
            Items = new List<AutoLayoutItemInfo>
            {
                new AutoLayoutItemInfo { ViewName = "MCP Auto Layout Schedule" }
            }
        });
        await Assert.That(first.Success).IsTrue();
        await Assert.That(first.PlacedCount).IsEqualTo(1);

        var second = AutoLayoutSheetEventHandler.Layout(_doc, new AutoLayoutSheetInfo
        {
            SheetUniqueId = first.SheetUniqueId,
            Items = new List<AutoLayoutItemInfo>
            {
                new AutoLayoutItemInfo { ViewName = "MCP Auto Layout Schedule 2" }
            }
        });
        await Assert.That(second.Success).IsTrue();
        await Assert.That(second.SheetCreated).IsFalse();
        await Assert.That(second.PlacedCount).IsEqualTo(1);

        var existing = first.Items.First(item => item.Placed);
        var added = second.Items.First(item => item.Placed);
        await Assert.That(Overlaps(existing, added)).IsFalse();
    }

    [Test]
    [HookExecutor<RevitThreadExecutor>]
    public async Task AutoLayout_MissingView_ReportedAsSkippedNotFailed()
    {
        if (_titleBlock == null)
        {
            await Assert.That(true).IsTrue();
            return;
        }

        var result = AutoLayoutSheetEventHandler.Layout(_doc, new AutoLayoutSheetInfo
        {
            SheetName = "MCP Автокомпоновка (пропуск)",
            Items = new List<AutoLayoutItemInfo>
            {
                new AutoLayoutItemInfo { ViewName = "Вид, которого нет" },
                new AutoLayoutItemInfo { ViewName = "MCP Auto Layout Schedule 2" }
            }
        });

        await Assert.That(result.Success).IsTrue();
        await Assert.That(result.PartialSuccess).IsTrue();
        await Assert.That(result.AllPlaced).IsFalse();
        await Assert.That(result.SkippedCount).IsEqualTo(1);

        var skipped = result.Items.First(item => !item.Placed);
        await Assert.That(skipped.Warning).Contains("was not found");
    }

    private static bool Overlaps(AutoLayoutPlacedItem a, AutoLayoutPlacedItem b)
    {
        return a.X < b.X + b.Width &&
               b.X < a.X + a.Width &&
               a.Y < b.Y + b.Height &&
               b.Y < a.Y + a.Height;
    }

    private static void AddField(ViewSchedule schedule, string parameterName)
    {
        var definition = schedule.Definition;
        foreach (var schedulableField in definition.GetSchedulableFields())
        {
            if (schedulableField.GetName(schedule.Document)
                .Equals(parameterName, StringComparison.OrdinalIgnoreCase))
            {
                definition.AddField(schedulableField);
                return;
            }
        }
    }

    private static void CreateEnclosedRoom(
        Document doc,
        Level level,
        double sizeFeet,
        UV location,
        string roomName)
    {
        double z = level.Elevation;
        var p1 = new XYZ(location.U, location.V, z);
        var p2 = new XYZ(location.U + sizeFeet, location.V, z);
        var p3 = new XYZ(location.U + sizeFeet, location.V + sizeFeet, z);
        var p4 = new XYZ(location.U, location.V + sizeFeet, z);

        Wall.Create(doc, Line.CreateBound(p1, p2), level.Id, false);
        Wall.Create(doc, Line.CreateBound(p2, p3), level.Id, false);
        Wall.Create(doc, Line.CreateBound(p3, p4), level.Id, false);
        Wall.Create(doc, Line.CreateBound(p4, p1), level.Id, false);

        var room = doc.Create.NewRoom(level, new UV(location.U + sizeFeet / 2.0, location.V + sizeFeet / 2.0));
        room?.get_Parameter(BuiltInParameter.ROOM_NAME)?.Set(roomName);
    }
}
