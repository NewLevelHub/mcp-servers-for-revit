using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Nice3point.TUnit.Revit;
using Nice3point.TUnit.Revit.Executors;
using TUnit.Core;
using TUnit.Core.Executors;

namespace RevitMCPCommandSet.Tests.Architecture;

/// <summary>
/// Lightweight validation of StairsType / RailingType discovery and StairsEditScope create (REV-83).
/// Full ADSK template acceptance is manual on «Короткий блок».
/// </summary>
public class CreateStairRailingTests : RevitApiTest
{
    private static Document _doc;
    private static Level _baseLevel;
    private static Level _topLevel;

    [Before(HookType.Class)]
    [HookExecutor<RevitThreadExecutor>]
    public static void Setup()
    {
        _doc = Application.NewProjectDocument(UnitSystem.Imperial);

        using var tx = new Transaction(_doc, "Setup stair/railing test levels");
        tx.Start();
        _baseLevel = Level.Create(_doc, 0.0);
        _baseLevel.Name = "Stair Base";
        _topLevel = Level.Create(_doc, 3000.0 / 304.8);
        _topLevel.Name = "Stair Top";
        tx.Commit();
    }

    [After(HookType.Class)]
    [HookExecutor<RevitThreadExecutor>]
    public static void Cleanup()
    {
        _doc?.Close(false);
    }

    [Test]
    public async Task Document_HasStairsType()
    {
        var stairsType = new FilteredElementCollector(_doc)
            .OfClass(typeof(StairsType))
            .Cast<StairsType>()
            .FirstOrDefault();

        await Assert.That(stairsType).IsNotNull();
    }

    [Test]
    public async Task Document_HasRailingType()
    {
        var railingType = new FilteredElementCollector(_doc)
            .OfClass(typeof(RailingType))
            .Cast<RailingType>()
            .FirstOrDefault();

        await Assert.That(railingType).IsNotNull();
    }

    [Test]
    public async Task StairsEditScope_CreatesStraightRun_WithExplicitType()
    {
        var stairsType = new FilteredElementCollector(_doc)
            .OfClass(typeof(StairsType))
            .Cast<StairsType>()
            .FirstOrDefault();

        await Assert.That(stairsType).IsNotNull();
        if (stairsType == null) return;

        ElementId newStairsId = ElementId.InvalidElementId;
        var widthInternal = 1200.0 / 304.8;

        using (var scope = new StairsEditScope(_doc, "Test Create Stair"))
        {
            newStairsId = scope.Start(_baseLevel.Id, _topLevel.Id);

            using (var tx = new Transaction(_doc, "Add run"))
            {
                tx.Start();
                var stairs = _doc.GetElement(newStairsId) as Stairs;
                await Assert.That(stairs).IsNotNull();
                stairs!.ChangeTypeId(stairsType.Id);

                var p0 = new XYZ(0, 0, _baseLevel.Elevation);
                var p1 = new XYZ(4000.0 / 304.8, 0, _baseLevel.Elevation);
                var run = StairsRun.CreateStraightRun(
                    _doc,
                    newStairsId,
                    Line.CreateBound(p0, p1),
                    StairsRunJustification.Center);
                run.ActualRunWidth = widthInternal;
                tx.Commit();
            }

            scope.Commit(new WarningOnlyPreprocessor());
        }

        var created = _doc.GetElement(newStairsId) as Stairs;
        await Assert.That(created).IsNotNull();
        await Assert.That(created!.GetStairsRuns().Count).IsGreaterThan(0);

        using (var tx = new Transaction(_doc, "Delete test stair"))
        {
            tx.Start();
            _doc.Delete(newStairsId);
            tx.Commit();
        }
    }

    private sealed class WarningOnlyPreprocessor : IFailuresPreprocessor
    {
        public FailureProcessingResult PreprocessFailures(FailuresAccessor failuresAccessor)
        {
            foreach (var failure in failuresAccessor.GetFailureMessages())
            {
                if (failure.GetSeverity() == FailureSeverity.Warning)
                    failuresAccessor.DeleteWarning(failure);
            }

            return FailureProcessingResult.Continue;
        }
    }
}
