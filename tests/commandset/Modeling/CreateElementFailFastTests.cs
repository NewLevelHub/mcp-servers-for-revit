using Autodesk.Revit.DB;
using Nice3point.TUnit.Revit;
using Nice3point.TUnit.Revit.Executors;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Services;
using TUnit.Core;
using TUnit.Core.Executors;

namespace RevitMCPCommandSet.Tests.Modeling;

/// <summary>
/// REV-72: create_* fail-fast (typeId / hostWallId) + mm-payload box from REV-71 audit.
/// Full ADSK «Короткий блок» acceptance remains manual after DLL rebuild.
/// </summary>
public class CreateElementFailFastTests : RevitApiTest
{
    private static Document _doc;
    private static Level _level;
    private static double _levelMm;
    private static WallType _basicWallType;
    private static FloorType _floorType;
    private static FamilySymbol _doorType;
    private static FamilySymbol _windowType;

    // REV-71 test zone (mm), away from origin
    private const double X0 = 100000;
    private const double Y0 = 100000;
    private const double Width = 6000;
    private const double Depth = 4000;
    private const double WallHeight = 3000;

    [Before(HookType.Class)]
    [HookExecutor<RevitThreadExecutor>]
    public static void Setup()
    {
        _doc = Application.NewProjectDocument(UnitSystem.Imperial);

        using var tx = new Transaction(_doc, "REV-72 setup");
        tx.Start();

        _level = Level.Create(_doc, 0.0);
        _level.Name = "REV-72 Level";
        _levelMm = _level.Elevation * 304.8;

        _basicWallType = new FilteredElementCollector(_doc)
            .OfClass(typeof(WallType))
            .Cast<WallType>()
            .FirstOrDefault(wt => wt.Kind == WallKind.Basic)
            ?? new FilteredElementCollector(_doc)
                .OfClass(typeof(WallType))
                .Cast<WallType>()
                .FirstOrDefault();

        _floorType = new FilteredElementCollector(_doc)
            .OfClass(typeof(FloorType))
            .OfCategory(BuiltInCategory.OST_Floors)
            .Cast<FloorType>()
            .FirstOrDefault();

        _doorType = new FilteredElementCollector(_doc)
            .OfClass(typeof(FamilySymbol))
            .OfCategory(BuiltInCategory.OST_Doors)
            .Cast<FamilySymbol>()
            .FirstOrDefault();

        _windowType = new FilteredElementCollector(_doc)
            .OfClass(typeof(FamilySymbol))
            .OfCategory(BuiltInCategory.OST_Windows)
            .Cast<FamilySymbol>()
            .FirstOrDefault();

        tx.Commit();
    }

    [After(HookType.Class)]
    [HookExecutor<RevitThreadExecutor>]
    public static void Cleanup()
    {
        _doc?.Close(false);
    }

    private static UIApplication RequireUiApp()
    {
        return Application.ActiveUIDocument?.Application
               ?? throw new InvalidOperationException("No UIApplication");
    }

    private static LineElement WallPayload(double x0, double y0, double x1, double y1, int typeId) =>
        new()
        {
            Category = "OST_Walls",
            TypeId = typeId,
            LocationLine = new JZLine(x0, y0, _levelMm, x1, y1, _levelMm),
            Thickness = 200,
            Height = WallHeight,
            BaseLevel = _levelMm,
            BaseOffset = 0,
        };

    [Test]
    [HookExecutor<RevitThreadExecutor>]
    public async Task CreateLine_WithoutTypeId_SuccessFalse()
    {
        var handler = new CreateLineElementEventHandler();
        handler.SetParameters(new List<LineElement>
        {
            WallPayload(X0, Y0 - 20000, X0 + 3000, Y0 - 20000, typeId: -1),
        });
        handler.Execute(RequireUiApp());

        await Assert.That(handler.Result.Success).IsFalse();
        await Assert.That(handler.Result.Message).Contains("typeId is required");
        await Assert.That(handler.Result.Response == null || handler.Result.Response.Count == 0).IsTrue();
    }

    [Test]
    [HookExecutor<RevitThreadExecutor>]
    public async Task CreateSurface_WithoutTypeId_SuccessFalse()
    {
        var z = _levelMm;
        var handler = new CreateSurfaceElementEventHandler();
        handler.SetParameters(new List<SurfaceElement>
        {
            new()
            {
                Category = "OST_Floors",
                TypeId = -1,
                Boundary = new JZFace
                {
                    OuterLoop = new List<JZLine>
                    {
                        new(X0, Y0 - 30000, z, X0 + 2000, Y0 - 30000, z),
                        new(X0 + 2000, Y0 - 30000, z, X0 + 2000, Y0 - 28000, z),
                        new(X0 + 2000, Y0 - 28000, z, X0, Y0 - 28000, z),
                        new(X0, Y0 - 28000, z, X0, Y0 - 30000, z),
                    },
                },
                Thickness = 80,
                BaseLevel = _levelMm,
                BaseOffset = 0,
            },
        });
        handler.Execute(RequireUiApp());

        await Assert.That(handler.Result.Success).IsFalse();
        await Assert.That(handler.Result.Message).Contains("typeId is required");
    }

    [Test]
    [HookExecutor<RevitThreadExecutor>]
    public async Task CreatePoint_DoorWithoutHostWallId_SuccessFalse()
    {
        if (_doorType == null)
        {
            // Blank template may lack door families — skip without failing the suite.
            await Assert.That(true).IsTrue();
            return;
        }

        var handler = new CreatePointElementEventHandler();
        handler.SetParameters(new List<PointElement>
        {
            new()
            {
                Category = "OST_Doors",
                TypeId = _doorType.Id.IntegerValue,
                LocationPoint = new JZPoint(X0 + 1000, Y0 - 40000, _levelMm),
                Width = 900,
                Height = 2100,
                BaseLevel = _levelMm,
                BaseOffset = 0,
                HostWallId = -1,
            },
        });
        handler.Execute(RequireUiApp());

        await Assert.That(handler.Result.Success).IsFalse();
        await Assert.That(handler.Result.Message).Contains("hostWallId is required");
    }

    [Test]
    [HookExecutor<RevitThreadExecutor>]
    public async Task CreateLine_PartialBatch_OneMissingTypeId_SuccessFalse()
    {
        await Assert.That(_basicWallType).IsNotNull();
        if (_basicWallType == null) return;

        var typeId = _basicWallType.Id.IntegerValue;
        var y = Y0 - 50000;
        var handler = new CreateLineElementEventHandler();
        handler.SetParameters(new List<LineElement>
        {
            WallPayload(X0, y, X0 + 2000, y, typeId),
            WallPayload(X0 + 2000, y, X0 + 4000, y, typeId: 0),
        });
        handler.Execute(RequireUiApp());

        await Assert.That(handler.Result.Success).IsFalse();
        await Assert.That(handler.Result.Message).Contains("Created 1/2");
        await Assert.That(handler.Result.Message).Contains("typeId is required");
        await Assert.That(handler.Result.Response?.Count).IsEqualTo(1);
    }

    [Test]
    [HookExecutor<RevitThreadExecutor>]
    public async Task Rev71_Box_WithTypeIdAndHost_CreatesWallsFloorDoorWindow()
    {
        await Assert.That(_basicWallType).IsNotNull();
        await Assert.That(_floorType).IsNotNull();
        if (_basicWallType == null || _floorType == null) return;

        var wallTypeId = _basicWallType.Id.IntegerValue;
        var x1 = X0 + Width;
        var y1 = Y0 + Depth;
        var z = _levelMm;

        // South / East / North / West
        var lineHandler = new CreateLineElementEventHandler();
        lineHandler.SetParameters(new List<LineElement>
        {
            WallPayload(X0, Y0, x1, Y0, wallTypeId),
            WallPayload(x1, Y0, x1, y1, wallTypeId),
            WallPayload(x1, y1, X0, y1, wallTypeId),
            WallPayload(X0, y1, X0, Y0, wallTypeId),
        });
        lineHandler.Execute(RequireUiApp());

        await Assert.That(lineHandler.Result.Success).IsTrue();
        await Assert.That(lineHandler.Result.Response?.Count).IsEqualTo(4);

        var wallIds = lineHandler.Result.Response!;
        var southWallId = wallIds[0];

        foreach (var id in wallIds)
        {
            var wall = _doc.GetElement(new ElementId(id)) as Wall;
            await Assert.That(wall).IsNotNull();
            await Assert.That(wall!.WallType.Id.IntegerValue).IsEqualTo(wallTypeId);
            await Assert.That(wall.WallType.Kind).IsNotEqualTo(WallKind.Curtain);
        }

        var surfaceHandler = new CreateSurfaceElementEventHandler();
        surfaceHandler.SetParameters(new List<SurfaceElement>
        {
            new()
            {
                Category = "OST_Floors",
                TypeId = _floorType.Id.IntegerValue,
                Boundary = new JZFace
                {
                    OuterLoop = new List<JZLine>
                    {
                        new(X0, Y0, z, x1, Y0, z),
                        new(x1, Y0, z, x1, y1, z),
                        new(x1, y1, z, X0, y1, z),
                        new(X0, y1, z, X0, Y0, z),
                    },
                },
                Thickness = 80,
                BaseLevel = _levelMm,
                BaseOffset = 0,
            },
        });
        surfaceHandler.Execute(RequireUiApp());

        await Assert.That(surfaceHandler.Result.Success).IsTrue();
        await Assert.That(surfaceHandler.Result.Response?.Count).IsEqualTo(1);

        if (_doorType != null)
        {
            var doorHandler = new CreatePointElementEventHandler();
            doorHandler.SetParameters(new List<PointElement>
            {
                new()
                {
                    Category = "OST_Doors",
                    TypeId = _doorType.Id.IntegerValue,
                    LocationPoint = new JZPoint(X0 + Width / 2, Y0, z),
                    Width = 900,
                    Height = 2100,
                    BaseLevel = _levelMm,
                    BaseOffset = 0,
                    HostWallId = southWallId,
                },
            });
            doorHandler.Execute(RequireUiApp());

            await Assert.That(doorHandler.Result.Success).IsTrue();
            var door = _doc.GetElement(new ElementId(doorHandler.Result.Response![0])) as FamilyInstance;
            await Assert.That(door).IsNotNull();
            await Assert.That(door!.Host?.Id.IntegerValue).IsEqualTo(southWallId);
        }

        if (_windowType != null)
        {
            var eastWallId = wallIds[1];
            var windowHandler = new CreatePointElementEventHandler();
            windowHandler.SetParameters(new List<PointElement>
            {
                new()
                {
                    Category = "OST_Windows",
                    TypeId = _windowType.Id.IntegerValue,
                    LocationPoint = new JZPoint(x1, Y0 + Depth / 2, z),
                    Width = 900,
                    Height = 1500,
                    BaseLevel = _levelMm,
                    BaseOffset = 900,
                    HostWallId = eastWallId,
                },
            });
            windowHandler.Execute(RequireUiApp());

            await Assert.That(windowHandler.Result.Success).IsTrue();
            var window = _doc.GetElement(new ElementId(windowHandler.Result.Response![0])) as FamilyInstance;
            await Assert.That(window).IsNotNull();
            await Assert.That(window!.Host?.Id.IntegerValue).IsEqualTo(eastWallId);
        }
    }
}
