using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Nice3point.TUnit.Revit;
using Nice3point.TUnit.Revit.Executors;
using RevitMCPCommandSet.Models.Normatives;
using RevitMCPCommandSet.Services.Normatives;
using RevitMCPCommandSet.Utils;
using TUnit.Core;
using TUnit.Core.Executors;

namespace RevitMCPCommandSet.Tests.Normatives;

public class ApplyNormResultTests : RevitApiTest
{
    private static Document _doc;
    private static Level _level;
    private static Room _room;

    [Before(HookType.Class)]
    [HookExecutor<RevitThreadExecutor>]
    public static void Setup()
    {
        _doc = Application.NewProjectDocument(UnitSystem.Imperial);

        using var tx = new Transaction(_doc, "Setup Apply Norm Result Tests");
        tx.Start();

        _level = Level.Create(_doc, 0.0);
        _level.Name = "Norm Result Level";

        var floorPlanType = new FilteredElementCollector(_doc)
            .OfClass(typeof(ViewFamilyType))
            .Cast<ViewFamilyType>()
            .FirstOrDefault(vft => vft.ViewFamily == ViewFamily.FloorPlan);

        if (floorPlanType != null)
        {
            ViewPlan.Create(_doc, floorPlanType.Id, _level.Id);
        }

        var p1 = new XYZ(0, 0, 0);
        var p2 = new XYZ(12, 0, 0);
        var p3 = new XYZ(12, 8, 0);
        var p4 = new XYZ(0, 8, 0);

        Wall.Create(_doc, Line.CreateBound(p1, p2), _level.Id, false);
        Wall.Create(_doc, Line.CreateBound(p2, p3), _level.Id, false);
        Wall.Create(_doc, Line.CreateBound(p3, p4), _level.Id, false);
        Wall.Create(_doc, Line.CreateBound(p4, p1), _level.Id, false);

        _room = _doc.Create.NewRoom(_level, new UV(6, 4));
        _room?.get_Parameter(BuiltInParameter.ROOM_NAME)?.Set("Комната НК Тест");

        tx.Commit();
    }

    [After(HookType.Class)]
    [HookExecutor<RevitThreadExecutor>]
    public static void Cleanup()
    {
        _doc?.Close(false);
    }

    [Test]
    public async Task ComposeValue_DefaultTemplate_IncludesDocumentClauseAndNote()
    {
        var value = ApplyNormResultEventHandler.ComposeValue(
            "", "СП РК 3.02-101", "п. 5.2.4", "глубина 2100 мм < 2400 мм");

        await Assert.That(value).Contains("СП РК 3.02-101");
        await Assert.That(value).Contains("п. 5.2.4");
        await Assert.That(value).Contains("глубина 2100 мм < 2400 мм");
    }

    [Test]
    public async Task ComposeValue_CustomTemplate_ReplacesPlaceholders()
    {
        var value = ApplyNormResultEventHandler.ComposeValue(
            "НК {clause}: {note}", "СП РК 3.02-101", "5.2.4", "нарушение");

        await Assert.That(value).IsEqualTo("НК 5.2.4: нарушение");
    }

    [Test]
    public async Task ShouldSkipWrite_ProtectsExistingValues()
    {
        // Пустое старое значение — писать можно.
        await Assert.That(ApplyNormResultEventHandler.ShouldSkipWrite("", "новое", false)).IsFalse();
        // То же самое значение — идемпотентно, можно.
        await Assert.That(ApplyNormResultEventHandler.ShouldSkipWrite("новое", "новое", false)).IsFalse();
        // Чужое значение без overwrite — пропуск.
        await Assert.That(ApplyNormResultEventHandler.ShouldSkipWrite("старое", "новое", false)).IsTrue();
        // Чужое значение с overwrite — писать можно.
        await Assert.That(ApplyNormResultEventHandler.ShouldSkipWrite("старое", "новое", true)).IsFalse();
    }

    [Test]
    [HookExecutor<RevitThreadExecutor>]
    public async Task PlanParameterChange_EmptyComments_PlansWrite()
    {
        var change = ApplyNormResultEventHandler.PlanParameterChange(
            _room!, "Comments", "Нарушение СП РК 3.02-101 п. 5.2.4", false,
            ApplyNormResultEventHandler.ActionSetParameter);

        await Assert.That(change.Status).IsEqualTo(NormResultChangeStatus.Planned);
        await Assert.That(change.OldValue).IsEqualTo(string.Empty);
        await Assert.That(change.NewValue).Contains("5.2.4");
        await Assert.That(change.ElementId).IsEqualTo(_room!.Id.GetValue());
    }

    [Test]
    [HookExecutor<RevitThreadExecutor>]
    public async Task PlanParameterChange_ExistingValue_SkippedWithoutOverwrite()
    {
        using (var tx = new Transaction(_doc, "Set existing comment"))
        {
            tx.Start();
            _room!.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.Set("ручная заметка");
            tx.Commit();
        }

        var skipped = ApplyNormResultEventHandler.PlanParameterChange(
            _room!, "Comments", "Нарушение", false,
            ApplyNormResultEventHandler.ActionSetParameter);
        await Assert.That(skipped.Status).IsEqualTo(NormResultChangeStatus.Skipped);
        await Assert.That(skipped.OldValue).IsEqualTo("ручная заметка");
        await Assert.That(skipped.SkipReason).Contains("overwrite");

        var forced = ApplyNormResultEventHandler.PlanParameterChange(
            _room!, "Comments", "Нарушение", true,
            ApplyNormResultEventHandler.ActionSetParameter);
        await Assert.That(forced.Status).IsEqualTo(NormResultChangeStatus.Planned);

        using (var tx = new Transaction(_doc, "Clear comment"))
        {
            tx.Start();
            _room!.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.Set(string.Empty);
            tx.Commit();
        }
    }

    [Test]
    [HookExecutor<RevitThreadExecutor>]
    public async Task PlanParameterChange_UnknownParameter_SkippedWithReason()
    {
        var change = ApplyNormResultEventHandler.PlanParameterChange(
            _room!, "НесуществующийПараметр", "значение", false,
            ApplyNormResultEventHandler.ActionSetParameter);

        await Assert.That(change.Status).IsEqualTo(NormResultChangeStatus.Skipped);
        await Assert.That(change.SkipReason).Contains("НесуществующийПараметр");
    }

    [Test]
    [HookExecutor<RevitThreadExecutor>]
    public async Task PlanParameterChange_CommentsAlias_ResolvesBuiltInParameter()
    {
        // Английский алиас должен находить встроенный параметр независимо от локализации.
        var change = ApplyNormResultEventHandler.PlanParameterChange(
            _room!, "Comments", "тест", false,
            ApplyNormResultEventHandler.ActionSetParameter);

        await Assert.That(change.Status).IsNotEqualTo(NormResultChangeStatus.Skipped);

        var builtInName = _room!.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)
            ?.Definition.Name ?? string.Empty;
        await Assert.That(change.ParameterName).IsEqualTo(builtInName);
    }
}
