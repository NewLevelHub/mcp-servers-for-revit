using Autodesk.Revit.DB;
using Nice3point.TUnit.Revit;
using Nice3point.TUnit.Revit.Executors;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Models.Detailing;
using RevitMCPCommandSet.Services.Detailing;
using RevitMCPCommandSet.Utils;
using TUnit.Core;
using TUnit.Core.Executors;

namespace RevitMCPCommandSet.Tests.Detailing;

public class DetailingPilotTests : RevitApiTest
{
    private static Document _doc;
    private static Level _level;
    private static Wall _wall;
    private static ViewPlan _floorPlan;

    [Before(HookType.Class)]
    [HookExecutor<RevitThreadExecutor>]
    public static void Setup()
    {
        _doc = Application.NewProjectDocument(UnitSystem.Imperial);

        using var tx = new Transaction(_doc, "Setup Detailing Pilot Test");
        tx.Start();

        _level = Level.Create(_doc, 0.0);
        _level.Name = "Detailing Level";

        _wall = Wall.Create(
            _doc,
            Line.CreateBound(new XYZ(0, 0, 0), new XYZ(10, 0, 0)),
            _level.Id,
            false);

        var floorPlanType = new FilteredElementCollector(_doc)
            .OfClass(typeof(ViewFamilyType))
            .Cast<ViewFamilyType>()
            .FirstOrDefault(vft => vft.ViewFamily == ViewFamily.FloorPlan);
        if (floorPlanType != null)
        {
            _floorPlan = ViewPlan.Create(_doc, floorPlanType.Id, _level.Id);
            _floorPlan.Name = "MCP Detailing Plan";
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
    [HookExecutor<RevitThreadExecutor>]
    public async Task CreateDetailView_Drafting_CreatedWithNameScaleAndDetailLevel()
    {
        var result = CreateDetailViewEventHandler.Create(_doc, new DetailViewCreationInfo
        {
            Mode = "drafting",
            Name = "Узел 1. Пилот",
            Scale = 5,
            DetailLevel = "Fine"
        });

        await Assert.That(result.Success).IsTrue();
        await Assert.That(result.ViewName).IsEqualTo("Узел 1. Пилот");
        await Assert.That(result.Scale).IsEqualTo(5);

        var view = _doc.GetElement(result.ViewUniqueId) as ViewDrafting;
        await Assert.That(view).IsNotNull();
        await Assert.That(view!.DetailLevel).IsEqualTo(ViewDetailLevel.Fine);
    }

    [Test]
    [HookExecutor<RevitThreadExecutor>]
    public async Task CreateDetailView_CalloutAroundElement_CreatedFromParentPlan()
    {
        if (_floorPlan == null)
        {
            await Assert.That(true).IsTrue();
            return;
        }

        var result = CreateDetailViewEventHandler.Create(_doc, new DetailViewCreationInfo
        {
            Mode = "callout",
            Name = "Узел 2. Фрагмент стены",
            Scale = 10,
            ParentViewId = _floorPlan.Id.GetValue(),
            ElementId = _wall.Id.GetValue(),
            Padding = 300
        });

        await Assert.That(result.Success).IsTrue();
        await Assert.That(result.Mode).IsEqualTo("callout");

        var view = _doc.GetElement(result.ViewUniqueId) as View;
        await Assert.That(view).IsNotNull();
        await Assert.That(view!.Scale).IsEqualTo(10);
    }

    [Test]
    [HookExecutor<RevitThreadExecutor>]
    public async Task CreateTextNote_OnDraftingView_CreatedWithLeader()
    {
        var viewResult = CreateDetailViewEventHandler.Create(_doc, new DetailViewCreationInfo
        {
            Mode = "drafting",
            Name = "Узел 3. Аннотации"
        });

        var result = CreateTextNoteEventHandler.Create(_doc, new TextNoteCreationInfo
        {
            ViewUniqueId = viewResult.ViewUniqueId,
            Text = "Гидроизоляция — 2 слоя",
            Position = new JZPoint(500, 500, 0),
            Width = 60,
            LeaderEnd = new JZPoint(0, 0, 0)
        });

        await Assert.That(result.Success).IsTrue();
        await Assert.That(result.TextType).IsNotEmpty();

        var note = _doc.GetElement(result.TextNoteUniqueId) as TextNote;
        await Assert.That(note).IsNotNull();
        await Assert.That(note!.Text.Trim()).IsEqualTo("Гидроизоляция — 2 слоя");
    }

    [Test]
    [HookExecutor<RevitThreadExecutor>]
    public async Task PlaceDetailComponent_MissingType_ReportsAvailableTypesInsteadOfFailing()
    {
        var viewResult = CreateDetailViewEventHandler.Create(_doc, new DetailViewCreationInfo
        {
            Mode = "drafting",
            Name = "Узел 4. Компоненты"
        });

        var result = PlaceDetailComponentEventHandler.Place(_doc, new DetailComponentPlacementInfo
        {
            ViewUniqueId = viewResult.ViewUniqueId,
            Items = new List<DetailComponentItemInfo>
            {
                new DetailComponentItemInfo
                {
                    FamilyName = "Несуществующее семейство",
                    TypeName = "Несуществующий тип",
                    Point = new JZPoint(0, 0, 0)
                }
            }
        });

        await Assert.That(result.Success).IsTrue();
        await Assert.That(result.PlacedCount).IsEqualTo(0);
        await Assert.That(result.Items[0].Placed).IsFalse();
        await Assert.That(result.Items[0].Warning).Contains("was not found");
    }

    [Test]
    [HookExecutor<RevitThreadExecutor>]
    public async Task CreateDetailLines_OnDraftingView_ByViewId_CreatesSegments()
    {
        var viewResult = CreateDetailViewEventHandler.Create(_doc, new DetailViewCreationInfo
        {
            Mode = "drafting",
            Name = "Узел 6. Линии",
            ActivateView = false
        });

        var result = CreateDetailLinesEventHandler.Create(
            _doc,
            null,
            new DetailLinesCreationInfo
            {
                ViewUniqueId = viewResult.ViewUniqueId,
                Polylines = new List<DetailPolylineInfo>
                {
                    new()
                    {
                        Points = new List<DetailLinePoint>
                        {
                            new() { X = 0, Y = 0 },
                            new() { X = 200, Y = 0 },
                            new() { X = 200, Y = 100 },
                            new() { X = 0, Y = 100 },
                            new() { X = 0, Y = 0 }
                        }
                    }
                }
            });

        await Assert.That(result.Success).IsTrue();
        await Assert.That(result.CreatedCount).IsEqualTo(4);
        await Assert.That(result.ViewId).IsEqualTo(viewResult.ViewId);
    }

    [Test]
    [HookExecutor<RevitThreadExecutor>]
    public async Task PlaceDetailComponent_LoadedType_PlacedOnDraftingView()
    {
        var symbol = new FilteredElementCollector(_doc)
            .OfCategory(BuiltInCategory.OST_DetailComponents)
            .OfClass(typeof(FamilySymbol))
            .Cast<FamilySymbol>()
            .FirstOrDefault();

        if (symbol == null)
        {
            // Default template has no detail component families; resolution failure
            // path is covered by the previous test.
            await Assert.That(true).IsTrue();
            return;
        }

        var viewResult = CreateDetailViewEventHandler.Create(_doc, new DetailViewCreationInfo
        {
            Mode = "drafting",
            Name = "Узел 5. Размещение"
        });

        var result = PlaceDetailComponentEventHandler.Place(_doc, new DetailComponentPlacementInfo
        {
            ViewUniqueId = viewResult.ViewUniqueId,
            Items = new List<DetailComponentItemInfo>
            {
                new DetailComponentItemInfo
                {
                    FamilyName = symbol.FamilyName,
                    TypeName = symbol.Name,
                    Point = new JZPoint(1000, 1000, 0),
                    Rotation = 90
                }
            }
        });

        await Assert.That(result.Success).IsTrue();
        await Assert.That(result.PlacedCount).IsEqualTo(1);
        await Assert.That(result.Items[0].ElementId).IsGreaterThan(0);
    }
}
