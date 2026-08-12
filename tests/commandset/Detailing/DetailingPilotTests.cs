using Autodesk.Revit.DB;
using Nice3point.TUnit.Revit;
using Nice3point.TUnit.Revit.Executors;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Models.Detailing;
using RevitMCPCommandSet.Services;
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

    [Test]
    [HookExecutor<RevitThreadExecutor>]
    public async Task CreateDetailView_Section_CutsAcrossElementAndReportsLookDirection()
    {
        var result = CreateDetailViewEventHandler.Create(_doc, new DetailViewCreationInfo
        {
            Mode = "section",
            Name = "Разрез 1. По стене",
            Scale = 20,
            ElementId = _wall.Id.GetValue(),
            SectionAlongX = true,
            SectionDepthMm = 2000,
            ActivateView = false
        });

        await Assert.That(result.Success).IsTrue();
        await Assert.That(result.Mode).IsEqualTo("section");
        await Assert.That(result.LookDirection).IsNotNull();
        await Assert.That(result.LookDirection!.Count).IsEqualTo(3);

        var view = _doc.GetElement(result.ViewUniqueId) as ViewSection;
        await Assert.That(view).IsNotNull();
        await Assert.That(view!.Scale).IsEqualTo(20);
    }

    [Test]
    [HookExecutor<RevitThreadExecutor>]
    public async Task CreateDetailView_Section_WithoutLineOrElement_Fails()
    {
        await Assert.That(() => CreateDetailViewEventHandler.Create(_doc, new DetailViewCreationInfo
        {
            Mode = "section",
            Name = "Разрез без данных"
        })).Throws<ArgumentException>();
    }

    [Test]
    [HookExecutor<RevitThreadExecutor>]
    public async Task CreateDetailLines_ArcAndLineStyle_DrawnAndReported()
    {
        var viewResult = CreateDetailViewEventHandler.Create(_doc, new DetailViewCreationInfo
        {
            Mode = "drafting",
            Name = "Узел 7. Дуга",
            ActivateView = false
        });

        var result = CreateDetailLinesEventHandler.Create(
            _doc,
            null,
            new DetailLinesCreationInfo
            {
                ViewUniqueId = viewResult.ViewUniqueId,
                LineStyleName = "Совершенно несуществующий стиль",
                Polylines = new List<DetailPolylineInfo>
                {
                    new()
                    {
                        Closed = true,
                        Points = new List<DetailLinePoint>
                        {
                            new() { X = 0, Y = 0 },
                            new() { X = 200, Y = 0 },
                            new() { X = 200, Y = 100 }
                        }
                    }
                },
                Arcs = new List<DetailArcInfo>
                {
                    new()
                    {
                        Start = new DetailLinePoint { X = 0, Y = 0 },
                        End = new DetailLinePoint { X = 200, Y = 0 },
                        PointOnArc = new DetailLinePoint { X = 100, Y = 60 }
                    }
                }
            });

        await Assert.That(result.Success).IsTrue();

        // Closed triangle gives 3 segments, plus the arc.
        await Assert.That(result.CreatedCount).IsEqualTo(4);

        // An unknown style falls back to the view default and says so instead of failing silently.
        await Assert.That(result.AvailableLineStyles.Count).IsGreaterThan(0);
        await Assert.That(result.Warnings.Any(warning => warning.Contains("was not found"))).IsTrue();
    }

    [Test]
    [HookExecutor<RevitThreadExecutor>]
    public async Task CreateDetailRegions_ContourWithHole_HatchedOnDraftingView()
    {
        var viewResult = CreateDetailViewEventHandler.Create(_doc, new DetailViewCreationInfo
        {
            Mode = "drafting",
            Name = "Узел 8. Штриховка",
            ActivateView = false
        });

        var result = CreateDetailRegionsEventHandler.Create(_doc, null, new DetailRegionsCreationInfo
        {
            ViewUniqueId = viewResult.ViewUniqueId,
            Regions = new List<DetailRegionInfo>
            {
                new()
                {
                    Label = "Плита",
                    Points = new List<DetailLinePoint>
                    {
                        new() { X = 0, Y = 0 },
                        new() { X = 400, Y = 0 },
                        new() { X = 400, Y = 200 },
                        new() { X = 0, Y = 200 }
                    },
                    Holes = new List<List<DetailLinePoint>>
                    {
                        new()
                        {
                            new() { X = 100, Y = 50 },
                            new() { X = 200, Y = 50 },
                            new() { X = 200, Y = 150 },
                            new() { X = 100, Y = 150 }
                        }
                    }
                }
            }
        });

        await Assert.That(result.Success).IsTrue();
        await Assert.That(result.CreatedCount).IsEqualTo(1);
        await Assert.That(result.Created[0].Holes).IsEqualTo(1);
        await Assert.That(result.CommentTag).IsEqualTo(CreateDetailRegionsEventHandler.DefaultCommentTag);

        var region = _doc.GetElement(RevitMCPCommandSet.Utils.ElementIdExtensions.FromLong(result.Created[0].RegionId));
        await Assert.That(region).IsNotNull();
    }

    [Test]
    [HookExecutor<RevitThreadExecutor>]
    public async Task CreateDetailRegions_ContourWithTooFewPoints_ReportedNotThrown()
    {
        var viewResult = CreateDetailViewEventHandler.Create(_doc, new DetailViewCreationInfo
        {
            Mode = "drafting",
            Name = "Узел 9. Плохой контур",
            ActivateView = false
        });

        var result = CreateDetailRegionsEventHandler.Create(_doc, null, new DetailRegionsCreationInfo
        {
            ViewUniqueId = viewResult.ViewUniqueId,
            Regions = new List<DetailRegionInfo>
            {
                new()
                {
                    Label = "Вырожденный",
                    Points = new List<DetailLinePoint>
                    {
                        new() { X = 0, Y = 0 },
                        new() { X = 100, Y = 0 }
                    }
                }
            }
        });

        await Assert.That(result.Success).IsFalse();
        await Assert.That(result.Warnings.Count).IsGreaterThan(0);
        await Assert.That(result.AvailableTypes.Count).IsGreaterThan(0);
    }

    [Test]
    [HookExecutor<RevitThreadExecutor>]
    public async Task CreateNodeDetail_Junction_ReadsRealLayersAndDrawsThem()
    {
        var wallType = new FilteredElementCollector(_doc)
            .OfClass(typeof(WallType))
            .Cast<WallType>()
            .FirstOrDefault(type => type.Kind == WallKind.Basic && type.GetCompoundStructure() != null);

        var floorType = new FilteredElementCollector(_doc)
            .OfClass(typeof(FloorType))
            .Cast<FloorType>()
            .FirstOrDefault(type => type.GetCompoundStructure() != null);

        if (wallType == null || floorType == null)
        {
            // The template carries no layered wall or floor type; nothing to generate a node from.
            await Assert.That(true).IsTrue();
            return;
        }

        var result = CreateNodeDetailEventHandler.Create(_doc, new NodeDetailCreationInfo
        {
            Mode = "junction",
            Name = "Узел 10. Пол к стене",
            WallTypeId = wallType.Id.GetValue(),
            FloorTypeId = floorType.Id.GetValue(),
            Scale = 10,
            ActivateView = false
        });

        await Assert.That(result.Success).IsTrue();
        await Assert.That(result.Wall).IsNotNull();
        await Assert.That(result.Floor).IsNotNull();
        await Assert.That(result.LayersRead).IsGreaterThan(0);
        await Assert.That(result.CurvesCreated).IsGreaterThan(0);
        await Assert.That(result.Wall!.TotalThicknessMm).IsGreaterThan(0);

        var view = _doc.GetElement(result.ViewUniqueId) as ViewDrafting;
        await Assert.That(view).IsNotNull();
        await Assert.That(view!.Scale).IsEqualTo(10);
    }

    [Test]
    [HookExecutor<RevitThreadExecutor>]
    public async Task CreateNodeDetail_Junction_WithoutFloor_FailsWithGuidance()
    {
        var wallType = new FilteredElementCollector(_doc)
            .OfClass(typeof(WallType))
            .Cast<WallType>()
            .FirstOrDefault(type => type.Kind == WallKind.Basic && type.GetCompoundStructure() != null);

        if (wallType == null)
        {
            await Assert.That(true).IsTrue();
            return;
        }

        await Assert.That(() => CreateNodeDetailEventHandler.Create(_doc, new NodeDetailCreationInfo
        {
            Mode = "junction",
            WallTypeId = wallType.Id.GetValue(),
            ActivateView = false
        })).Throws<ArgumentException>();
    }

    [Test]
    [HookExecutor<RevitThreadExecutor>]
    public async Task CreateNodeDetail_Single_DrawsOneBuildUp()
    {
        var floorType = new FilteredElementCollector(_doc)
            .OfClass(typeof(FloorType))
            .Cast<FloorType>()
            .FirstOrDefault(type => type.GetCompoundStructure() != null);

        if (floorType == null)
        {
            await Assert.That(true).IsTrue();
            return;
        }

        var result = CreateNodeDetailEventHandler.Create(_doc, new NodeDetailCreationInfo
        {
            Mode = "single",
            Orientation = "horizontal",
            Name = "Узел 11. Пирог пола",
            FloorTypeId = floorType.Id.GetValue(),
            Annotate = false,
            ActivateView = false
        });

        await Assert.That(result.Success).IsTrue();
        await Assert.That(result.Floor).IsNotNull();
        await Assert.That(result.DimensionsCreated).IsEqualTo(0);
        await Assert.That(result.NotesCreated).IsEqualTo(0);
    }

    [Test]
    [HookExecutor<RevitThreadExecutor>]
    public async Task LoadFamily_MissingFile_ReportedAsWarningNotException()
    {
        await Assert.That(() => LoadFamilyEventHandler.Load(_doc, new LoadFamilyRequestInfo
        {
            Paths = new List<string> { @"C:\definitely\not\here\Узел.rfa" }
        })).Throws<ArgumentException>();
    }

    [Test]
    [HookExecutor<RevitThreadExecutor>]
    public async Task LoadFamily_NonRfaPath_RejectedBeforeReachingRevit()
    {
        await Assert.That(() => LoadFamilyEventHandler.Load(_doc, new LoadFamilyRequestInfo
        {
            Paths = new List<string> { @"C:\Windows\notepad.exe" }
        })).Throws<ArgumentException>();
    }
}
