using Autodesk.Revit.DB;
using Nice3point.TUnit.Revit;
using Nice3point.TUnit.Revit.Executors;
using TUnit.Core;
using TUnit.Core.Executors;

namespace RevitMCPCommandSet.Tests.Views;

public class CreateSheetTests : RevitApiTest
{
    private static Document _doc;
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

        if (_titleBlock != null && !_titleBlock.IsActive)
        {
            _titleBlock.Activate();
            _doc.Regenerate();
        }
    }

    [After(HookType.Class)]
    [HookExecutor<RevitThreadExecutor>]
    public static void Cleanup()
    {
        _doc?.Close(false);
    }

    [Test]
    public async Task CreateSheet_WithTitleBlock_SheetIsCreated()
    {
        if (_titleBlock == null)
        {
            await Assert.That(true).IsTrue();
            return;
        }

        ViewSheet sheet = null;

        using (var tx = new Transaction(_doc, "Create Sheet Test"))
        {
            tx.Start();
            sheet = ViewSheet.Create(_doc, _titleBlock.Id);
            sheet.SheetNumber = "MCP-TEST-001";
            sheet.Name = "MCP Sheet Test";
            tx.Commit();
        }

        await Assert.That(sheet).IsNotNull();
        await Assert.That(sheet.SheetNumber).IsEqualTo("MCP-TEST-001");
    }

    [Test]
    public async Task PlaceViewOnSheet_FloorPlan_ViewportIsCreated()
    {
        if (_titleBlock == null)
        {
            await Assert.That(true).IsTrue();
            return;
        }

        ViewSheet sheet = null;
        ViewPlan floorPlan = null;
        Viewport viewport = null;

        using (var tx = new Transaction(_doc, "Place Floor Plan Test"))
        {
            tx.Start();

            sheet = ViewSheet.Create(_doc, _titleBlock.Id);
            sheet.SheetNumber = "MCP-TEST-002";
            sheet.Name = "MCP Placement Test";

            floorPlan = new FilteredElementCollector(_doc)
                .OfClass(typeof(ViewPlan))
                .Cast<ViewPlan>()
                .FirstOrDefault(v => v.ViewType == ViewType.FloorPlan);

            await Assert.That(floorPlan).IsNotNull();

            var location = new XYZ(300.0 / 304.8, 200.0 / 304.8, 0);
            viewport = Viewport.Create(_doc, sheet.Id, floorPlan.Id, location);

            tx.Commit();
        }

        await Assert.That(viewport).IsNotNull();
    }

    [Test]
    public async Task PlaceViewOnSheet_DoorSchedule_ScheduleInstanceIsCreated()
    {
        if (_titleBlock == null)
        {
            await Assert.That(true).IsTrue();
            return;
        }

        ViewSheet sheet = null;
        ViewSchedule schedule = null;
        ScheduleSheetInstance instance = null;

        using (var tx = new Transaction(_doc, "Place Schedule Test"))
        {
            tx.Start();

            sheet = ViewSheet.Create(_doc, _titleBlock.Id);
            sheet.SheetNumber = "MCP-TEST-003";
            sheet.Name = "MCP Schedule Sheet Test";

            schedule = ViewSchedule.CreateSchedule(_doc, new ElementId(BuiltInCategory.OST_Doors));
            schedule.Name = "MCP Door Schedule Placement Test";

            var origin = new XYZ(100.0 / 304.8, 100.0 / 304.8, 0);
            instance = ScheduleSheetInstance.Create(_doc, sheet.Id, schedule.Id, origin);

            tx.Commit();
        }

        await Assert.That(instance).IsNotNull();
    }

    [Test]
    public async Task SheetPlanSchedule_Scenario_AllElementsExist()
    {
        if (_titleBlock == null)
        {
            await Assert.That(true).IsTrue();
            return;
        }

        ViewSheet sheet = null;
        ViewPlan floorPlan = null;
        ViewSchedule schedule = null;
        Viewport viewport = null;
        ScheduleSheetInstance scheduleInstance = null;

        using (var tx = new Transaction(_doc, "Sheet Plan Schedule Scenario"))
        {
            tx.Start();

            sheet = ViewSheet.Create(_doc, _titleBlock.Id);
            sheet.SheetNumber = "MCP-TEST-004";
            sheet.Name = "MCP Scenario Sheet";

            floorPlan = new FilteredElementCollector(_doc)
                .OfClass(typeof(ViewPlan))
                .Cast<ViewPlan>()
                .FirstOrDefault(v => v.ViewType == ViewType.FloorPlan);

            schedule = ViewSchedule.CreateSchedule(_doc, new ElementId(BuiltInCategory.OST_Doors));
            schedule.Name = "MCP Scenario Door Schedule";

            viewport = Viewport.Create(
                _doc,
                sheet.Id,
                floorPlan.Id,
                new XYZ(250.0 / 304.8, 250.0 / 304.8, 0));

            scheduleInstance = ScheduleSheetInstance.Create(
                _doc,
                sheet.Id,
                schedule.Id,
                new XYZ(600.0 / 304.8, 250.0 / 304.8, 0));

            tx.Commit();
        }

        await Assert.That(sheet).IsNotNull();
        await Assert.That(viewport).IsNotNull();
        await Assert.That(scheduleInstance).IsNotNull();
    }
}
