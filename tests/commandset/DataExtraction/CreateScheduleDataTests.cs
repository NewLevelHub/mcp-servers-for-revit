using Autodesk.Revit.DB;
using Nice3point.TUnit.Revit;
using Nice3point.TUnit.Revit.Executors;
using RevitMCPCommandSet.Utils;
using TUnit.Core;
using TUnit.Core.Executors;

namespace RevitMCPCommandSet.Tests.DataExtraction;

public class CreateScheduleDataTests : RevitApiTest
{
    private static Document _doc;
    private static Level _level;

    [Before(HookType.Class)]
    [HookExecutor<RevitThreadExecutor>]
    public static void Setup()
    {
        _doc = Application.NewProjectDocument(UnitSystem.Imperial);

        using var tx = new Transaction(_doc, "Setup Schedule Data Test");
        tx.Start();

        _level = Level.Create(_doc, 0.0);
        _level.Name = "Schedule Test Level";

        var floorType = new FilteredElementCollector(_doc)
            .OfClass(typeof(FloorType))
            .Cast<FloorType>()
            .First();

        var loop = new CurveLoop();
        loop.Append(Line.CreateBound(new XYZ(0, 0, 0), new XYZ(10, 0, 0)));
        loop.Append(Line.CreateBound(new XYZ(10, 0, 0), new XYZ(10, 10, 0)));
        loop.Append(Line.CreateBound(new XYZ(10, 10, 0), new XYZ(0, 10, 0)));
        loop.Append(Line.CreateBound(new XYZ(0, 10, 0), new XYZ(0, 0, 0)));
        Floor.Create(_doc, new List<CurveLoop> { loop }, floorType.Id, _level.Id);

        tx.Commit();
    }

    [After(HookType.Class)]
    [HookExecutor<RevitThreadExecutor>]
    public static void Cleanup()
    {
        _doc?.Close(false);
    }

    [Test]
    public async Task FloorSchedule_CountMatchesCollector()
    {
        int expected = new FilteredElementCollector(_doc)
            .OfCategory(BuiltInCategory.OST_Floors)
            .WhereElementIsNotElementType()
            .Cast<Floor>()
            .Count(FloorFinishClassifier.IsFloorFinish);

        await Assert.That(expected).IsEqualTo(1);
    }

    [Test]
    public async Task FloorFinishFilter_ExcludesSlabAndInsulationTypeNames()
    {
        await Assert.That(FloorFinishClassifier.IsFloorFinish("(полы)квартира_8_t=80")).IsTrue();
        await Assert.That(FloorFinishClassifier.IsFloorFinish("(плита_перекрытия)железобетон_t=200")).IsFalse();
        await Assert.That(FloorFinishClassifier.IsFloorFinish("(потолок_утеплитель)лоджия")).IsFalse();
    }

    [Test]
    public async Task DoorSchedule_EmptyProject_ReturnsZero()
    {
        int doorCount = new FilteredElementCollector(_doc)
            .OfCategory(BuiltInCategory.OST_Doors)
            .WhereElementIsNotElementType()
            .GetElementCount();

        await Assert.That(doorCount).IsEqualTo(0);
    }

    [Test]
    public async Task DoorAccessoryFilter_ExcludesSlopeFamilyNames()
    {
        // Mirrors create_door_schedule / validate_schedule filter (REV-41).
        var names = new[]
        {
            "(откос)двери_внутренний",
            "Дверь 1",
            "(Откос)Двери_наружный",
            "ДВ_01"
        };

        var schedulable = names
            .Where(name => !OpeningFillClassifier.IsDoorAccessory(name))
            .ToList();

        await Assert.That(schedulable.Count).IsEqualTo(2);
        await Assert.That(schedulable.Contains("Дверь 1")).IsTrue();
        await Assert.That(schedulable.Contains("ДВ_01")).IsTrue();
    }

    [Test]
    public async Task WindowSchedule_EmptyProject_ReturnsZero()
    {
        int windowCount = new FilteredElementCollector(_doc)
            .OfCategory(BuiltInCategory.OST_Windows)
            .WhereElementIsNotElementType()
            .GetElementCount();

        await Assert.That(windowCount).IsEqualTo(0);
    }

    [Test]
    public async Task FloorSchedule_GroupCount_IsOneForSingleFloorTypeAndLevel()
    {
        var floors = new FilteredElementCollector(_doc)
            .OfCategory(BuiltInCategory.OST_Floors)
            .WhereElementIsNotElementType()
            .Cast<Floor>()
            .ToList();

        var groups = floors
            .GroupBy(f => new
            {
                TypeId = f.GetTypeId(),
                Level = (_doc.GetElement(f.LevelId) as Level)?.Name ?? "No Level"
            })
            .Count();

        await Assert.That(groups).IsEqualTo(1);
        await Assert.That(floors.Count).IsEqualTo(1);
    }
}
