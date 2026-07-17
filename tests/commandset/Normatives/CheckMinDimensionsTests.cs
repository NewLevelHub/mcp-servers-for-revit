using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Nice3point.TUnit.Revit;
using Nice3point.TUnit.Revit.Executors;
using RevitMCPCommandSet.Services.Normatives;
using RevitMCPCommandSet.Utils;
using TUnit.Core;
using TUnit.Core.Executors;

namespace RevitMCPCommandSet.Tests.Normatives;

public class CheckMinDimensionsTests : RevitApiTest
{
    private static Document _doc;
    private static Level _level;
    private static Room _loggiaRoom;

    [Before(HookType.Class)]
    [HookExecutor<RevitThreadExecutor>]
    public static void Setup()
    {
        _doc = Application.NewProjectDocument(UnitSystem.Imperial);

        using var tx = new Transaction(_doc, "Setup Check Min Dimensions Tests");
        tx.Start();

        _level = Level.Create(_doc, 0.0);
        _level.Name = "Min Dim Level";

        var floorPlanType = new FilteredElementCollector(_doc)
            .OfClass(typeof(ViewFamilyType))
            .Cast<ViewFamilyType>()
            .FirstOrDefault(vft => vft.ViewFamily == ViewFamily.FloorPlan);

        if (floorPlanType != null)
        {
            ViewPlan.Create(_doc, floorPlanType.Id, _level.Id);
        }

        // Rectangle 4ft x 6ft loggia: width ≈ 1219 mm, depth ≈ 1829 mm.
        var p1 = new XYZ(0, 0, 0);
        var p2 = new XYZ(6, 0, 0);
        var p3 = new XYZ(6, 4, 0);
        var p4 = new XYZ(0, 4, 0);

        Wall.Create(_doc, Line.CreateBound(p1, p2), _level.Id, false);
        Wall.Create(_doc, Line.CreateBound(p2, p3), _level.Id, false);
        Wall.Create(_doc, Line.CreateBound(p3, p4), _level.Id, false);
        Wall.Create(_doc, Line.CreateBound(p4, p1), _level.Id, false);

        _loggiaRoom = _doc.Create.NewRoom(_level, new UV(3, 2));
        _loggiaRoom?.get_Parameter(BuiltInParameter.ROOM_NAME)?.Set("Лоджия Тест");

        tx.Commit();
    }

    [After(HookType.Class)]
    [HookExecutor<RevitThreadExecutor>]
    public static void Cleanup()
    {
        _doc?.Close(false);
    }

    [Test]
    public async Task BalconyLoggiaClassifier_DetectsLoggiaAndBalcony()
    {
        await Assert.That(BalconyLoggiaClassifier.IsBalconyOrLoggia("Лоджия")).IsTrue();
        await Assert.That(BalconyLoggiaClassifier.IsBalconyOrLoggia("Балкон")).IsTrue();
        await Assert.That(BalconyLoggiaClassifier.IsBalconyOrLoggia("Летнее помещение")).IsTrue();
        await Assert.That(BalconyLoggiaClassifier.IsBalconyOrLoggia("Жилая комната")).IsFalse();
        await Assert.That(BalconyLoggiaClassifier.Classify("Лоджия кухни")).IsEqualTo(
            BalconyLoggiaClassifier.OutdoorSpaceKind.Loggia);
    }

    [Test]
    public async Task FirePathOutdoor_IsВоздушнаяЗона_NotPrivateLoggia()
    {
        await Assert.That(BalconyLoggiaClassifier.IsFirePathOutdoor("Воздушная зона")).IsTrue();
        await Assert.That(BalconyLoggiaClassifier.IsFirePathOutdoor("Лоджия")).IsFalse();
        await Assert.That(BalconyLoggiaClassifier.IsFirePathOutdoor("Балкон")).IsFalse();
        await Assert.That(BalconyLoggiaClassifier.Classify("Воздушная зона")).IsEqualTo(
            BalconyLoggiaClassifier.OutdoorSpaceKind.FirePathOutdoor);
    }

    [Test]
    public async Task RoomFootprint_LoggiaRoom_HasExpectedSpans()
    {
        await Assert.That(_loggiaRoom).IsNotNull();

        var (widthMm, depthMm) = RoomFootprintCalculator.Calculate(_loggiaRoom!);

        await Assert.That(widthMm).IsLessThan(depthMm);
        await Assert.That(widthMm).IsGreaterThan(1100);
        await Assert.That(widthMm).IsLessThan(1300);
        await Assert.That(depthMm).IsGreaterThan(1700);
    }

    [Test]
    public async Task DimensionCompliance_DetectsWidthAndDepthViolations()
    {
        var (widthMm, depthMm) = RoomFootprintCalculator.Calculate(_loggiaRoom!);

        await Assert.That(CheckMinDimensionsEventHandler.IsCompliant(widthMm, 1400)).IsFalse();
        await Assert.That(CheckMinDimensionsEventHandler.CalculateDeviationMm(widthMm, 1400))
            .IsEqualTo(1400 - widthMm);

        await Assert.That(CheckMinDimensionsEventHandler.IsCompliant(depthMm, 1600)).IsTrue();
        await Assert.That(CheckMinDimensionsEventHandler.CalculateDeviationMm(depthMm, 1600)).IsEqualTo(0);
    }
}
