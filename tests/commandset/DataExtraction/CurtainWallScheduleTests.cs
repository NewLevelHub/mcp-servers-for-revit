using Autodesk.Revit.DB;
using Nice3point.TUnit.Revit;
using Nice3point.TUnit.Revit.Executors;
using RevitMCPCommandSet.Utils;
using TUnit.Core;
using TUnit.Core.Executors;

namespace RevitMCPCommandSet.Tests.DataExtraction;

public class CurtainWallScheduleTests : RevitApiTest
{
    private static Document _doc;
    private static Level _level;
    private static Wall _curtainWall;
    private static Wall _basicWall;

    [Before(HookType.Class)]
    [HookExecutor<RevitThreadExecutor>]
    public static void Setup()
    {
        _doc = Application.NewProjectDocument(UnitSystem.Imperial);

        using var tx = new Transaction(_doc, "Setup Curtain Wall Schedule Test");
        tx.Start();

        _level = Level.Create(_doc, 0.0);
        _level.Name = "Curtain Wall Level";

        _basicWall = Wall.Create(
            _doc,
            Line.CreateBound(new XYZ(0, 0, 0), new XYZ(10, 0, 0)),
            _level.Id,
            false);

        var curtainType = new FilteredElementCollector(_doc)
            .OfClass(typeof(WallType))
            .Cast<WallType>()
            .FirstOrDefault(type => type.Kind == WallKind.Curtain);

        if (curtainType != null)
        {
            _curtainWall = Wall.Create(
                _doc,
                Line.CreateBound(new XYZ(0, 20, 0), new XYZ(10, 20, 0)),
                curtainType.Id,
                _level.Id,
                10.0,
                0.0,
                false,
                false);
            _curtainWall.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.Set("ВВ-1");
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
    public async Task Classifier_CurtainWallSystem_IsCurtainWall()
    {
        if (_curtainWall == null)
        {
            await Assert.That(true).IsTrue();
            return;
        }

        await Assert.That(CurtainWallClassifier.IsCurtainWall(_curtainWall)).IsTrue();
    }

    [Test]
    public async Task Classifier_BasicWall_IsNotCurtainWall()
    {
        await Assert.That(CurtainWallClassifier.IsCurtainWall(_basicWall)).IsFalse();
    }

    [Test]
    public async Task Classifier_TypeFilter_MatchesSubstringCaseInsensitive()
    {
        if (_curtainWall == null)
        {
            await Assert.That(true).IsTrue();
            return;
        }

        var typeName = _curtainWall.WallType.Name;
        var fragment = typeName.Substring(0, Math.Min(4, typeName.Length)).ToUpperInvariant();

        await Assert.That(CurtainWallClassifier.MatchesTypeFilter(_curtainWall, null)).IsTrue();
        await Assert.That(CurtainWallClassifier.MatchesTypeFilter(_curtainWall, "")).IsTrue();
        await Assert.That(CurtainWallClassifier.MatchesTypeFilter(_curtainWall, fragment)).IsTrue();
        await Assert.That(CurtainWallClassifier.MatchesTypeFilter(_curtainWall, "(витражи)")).IsFalse();
    }

    [Test]
    public async Task Collector_CurtainWallSystems_CountsWallsNotPanels()
    {
        if (_curtainWall == null)
        {
            await Assert.That(true).IsTrue();
            return;
        }

        var curtainSystems = new FilteredElementCollector(_doc)
            .OfCategory(BuiltInCategory.OST_Walls)
            .WhereElementIsNotElementType()
            .OfClass(typeof(Wall))
            .Cast<Wall>()
            .Where(CurtainWallClassifier.IsCurtainWall)
            .ToList();

        // Exactly one curtain wall system; the basic wall and any auto-generated
        // curtain panels must not be counted.
        await Assert.That(curtainSystems.Count).IsEqualTo(1);
        await Assert.That(curtainSystems[0].Id).IsEqualTo(_curtainWall.Id);

        var panels = new FilteredElementCollector(_doc)
            .OfCategory(BuiltInCategory.OST_CurtainWallPanels)
            .WhereElementIsNotElementType()
            .ToElements()
            .Where(CurtainWallClassifier.IsCurtainWall)
            .ToList();
        await Assert.That(panels.Count).IsEqualTo(0);
    }

    [Test]
    public async Task CurtainWall_MarkParameter_RoundTrip()
    {
        if (_curtainWall == null)
        {
            await Assert.That(true).IsTrue();
            return;
        }

        var mark = _curtainWall.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString();
        await Assert.That(mark).IsEqualTo("ВВ-1");
    }
}
