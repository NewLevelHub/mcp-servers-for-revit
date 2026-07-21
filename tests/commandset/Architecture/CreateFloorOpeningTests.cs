using Autodesk.Revit.DB;
using Nice3point.TUnit.Revit;
using Nice3point.TUnit.Revit.Executors;
using TUnit.Core;
using TUnit.Core.Executors;

namespace RevitMCPCommandSet.Tests.Architecture;

/// <summary>
/// REV-85: Floor opening + shaft via Document.Create.NewOpening.
/// Full acceptance is manual (stair shaft on «Короткий блок»).
/// </summary>
public class CreateFloorOpeningTests : RevitApiTest
{
    private static Document _doc;
    private static Level _baseLevel;
    private static Level _topLevel;
    private static Floor _floor;

    [Before(HookType.Class)]
    [HookExecutor<RevitThreadExecutor>]
    public static void Setup()
    {
        _doc = Application.NewProjectDocument(UnitSystem.Imperial);

        using var tx = new Transaction(_doc, "Setup floor opening test");
        tx.Start();
        _baseLevel = Level.Create(_doc, 0.0);
        _baseLevel.Name = "Opening Base";
        _topLevel = Level.Create(_doc, 3000.0 / 304.8);
        _topLevel.Name = "Opening Top";

        var floorType = new FilteredElementCollector(_doc)
            .OfClass(typeof(FloorType))
            .Cast<FloorType>()
            .FirstOrDefault();

        if (floorType != null)
        {
            var loop = new CurveLoop();
            loop.Append(Line.CreateBound(new XYZ(0, 0, 0), new XYZ(30, 0, 0)));
            loop.Append(Line.CreateBound(new XYZ(30, 0, 0), new XYZ(30, 30, 0)));
            loop.Append(Line.CreateBound(new XYZ(30, 30, 0), new XYZ(0, 30, 0)));
            loop.Append(Line.CreateBound(new XYZ(0, 30, 0), new XYZ(0, 0, 0)));
            _floor = Floor.Create(_doc, new List<CurveLoop> { loop }, floorType.Id, _baseLevel.Id);
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
    public async Task NewOpening_CutsFloor_RectangularProfile()
    {
        await Assert.That(_floor).IsNotNull();
        if (_floor == null) return;

        // 2 m × 2.5 m opening near center (mm → ft via /304.8)
        var mm = 1.0 / 304.8;
        var x0 = 5000 * mm;
        var y0 = 5000 * mm;
        var w = 2000 * mm;
        var d = 2500 * mm;

        Opening opening;
        using (var tx = new Transaction(_doc, "Test floor opening"))
        {
            tx.Start();
            var profile = new CurveArray();
            profile.Append(Line.CreateBound(new XYZ(x0, y0, 0), new XYZ(x0 + w, y0, 0)));
            profile.Append(Line.CreateBound(new XYZ(x0 + w, y0, 0), new XYZ(x0 + w, y0 + d, 0)));
            profile.Append(Line.CreateBound(new XYZ(x0 + w, y0 + d, 0), new XYZ(x0, y0 + d, 0)));
            profile.Append(Line.CreateBound(new XYZ(x0, y0 + d, 0), new XYZ(x0, y0, 0)));
            opening = _doc.Create.NewOpening(_floor, profile, true);
            tx.Commit();
        }

        await Assert.That(opening).IsNotNull();
        await Assert.That(opening.Id).IsNotEqualTo(ElementId.InvalidElementId);
    }

    [Test]
    public async Task NewOpening_CreatesShaft_BetweenLevels()
    {
        var mm = 1.0 / 304.8;
        var x0 = 10000 * mm;
        var y0 = 10000 * mm;
        var w = 2500 * mm;
        var d = 2500 * mm;

        Opening shaft;
        using (var tx = new Transaction(_doc, "Test shaft opening"))
        {
            tx.Start();
            var profile = new CurveArray();
            profile.Append(Line.CreateBound(new XYZ(x0, y0, 0), new XYZ(x0 + w, y0, 0)));
            profile.Append(Line.CreateBound(new XYZ(x0 + w, y0, 0), new XYZ(x0 + w, y0 + d, 0)));
            profile.Append(Line.CreateBound(new XYZ(x0 + w, y0 + d, 0), new XYZ(x0, y0 + d, 0)));
            profile.Append(Line.CreateBound(new XYZ(x0, y0 + d, 0), new XYZ(x0, y0, 0)));
            shaft = _doc.Create.NewOpening(_baseLevel, _topLevel, profile);
            tx.Commit();
        }

        await Assert.That(shaft).IsNotNull();
        await Assert.That(shaft.Id).IsNotEqualTo(ElementId.InvalidElementId);
    }
}
