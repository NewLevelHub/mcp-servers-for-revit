using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Nice3point.TUnit.Revit;
using Nice3point.TUnit.Revit.Executors;
using RevitMCPCommandSet.Utils;
using TUnit.Core;
using TUnit.Core.Executors;

namespace RevitMCPCommandSet.Tests.Access;

public class ElementParameterTests : RevitApiTest
{
    private static Document _doc;
    private static Level _level;
    private static Room _room;

    [Before(HookType.Class)]
    [HookExecutor<RevitThreadExecutor>]
    public static void Setup()
    {
        _doc = Application.NewProjectDocument(UnitSystem.Imperial);

        using var tx = new Transaction(_doc, "Setup Element Parameter Tests");
        tx.Start();

        _level = Level.Create(_doc, 0.0);
        _level.Name = "Parameter Test Level";

        var floorPlanType = new FilteredElementCollector(_doc)
            .OfClass(typeof(ViewFamilyType))
            .Cast<ViewFamilyType>()
            .FirstOrDefault(vft => vft.ViewFamily == ViewFamily.FloorPlan);

        if (floorPlanType != null)
        {
            ViewPlan.Create(_doc, floorPlanType.Id, _level.Id);
        }

        CreateEnclosure(_doc, _level.Id, 0, 0, 10);
        _room = _doc.Create.NewRoom(_level, new UV(5.0, 5.0));
        _room?.get_Parameter(BuiltInParameter.ROOM_NAME)?.Set("Parameter Test Room");
        _room?.get_Parameter(BuiltInParameter.ROOM_DEPARTMENT)?.Set("Testing");

        tx.Commit();
    }

    [After(HookType.Class)]
    [HookExecutor<RevitThreadExecutor>]
    public static void Cleanup()
    {
        _doc?.Close(false);
    }

    [Test]
    public async Task GetParameters_Room_ReturnsNameDepartmentAndStorageTypes()
    {
        await Assert.That(_room).IsNotNull();

        var parameters = _room!.Parameters
            .Cast<Parameter>()
            .Where(parameter => parameter?.Definition != null)
            .Select(parameter => ElementParameterHelper.ToParameterInfo(parameter, _doc))
            .ToList();

        await Assert.That(parameters.Count).IsGreaterThan(0);

        var nameParameter = parameters.FirstOrDefault(parameter =>
            parameter.Name.Equals("Name", StringComparison.OrdinalIgnoreCase));
        await Assert.That(nameParameter).IsNotNull();
        await Assert.That(nameParameter!.StorageType).IsEqualTo(StorageType.String.ToString());
        await Assert.That(nameParameter.DisplayValue).IsEqualTo("Parameter Test Room");

        var departmentParameter = parameters.FirstOrDefault(parameter =>
            parameter.Name.Equals("Department", StringComparison.OrdinalIgnoreCase));
        await Assert.That(departmentParameter).IsNotNull();
        await Assert.That(departmentParameter!.DisplayValue).IsEqualTo("Testing");
    }

    [Test]
    public async Task SetParameter_StringValue_UpdatesRoomName()
    {
        await Assert.That(_room).IsNotNull();

        using var tx = new Transaction(_doc, "Set Room Name Parameter");
        tx.Start();

        var nameParameter = ElementParameterHelper.FindParameter(_room!, "Name");
        await Assert.That(nameParameter).IsNotNull();
        await Assert.That(nameParameter!.IsReadOnly).IsFalse();

        ElementParameterHelper.SetParameterValue(nameParameter, "Updated Room Name", _doc);
        tx.Commit();

        var updatedValue = _room!.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString();
        await Assert.That(updatedValue).IsEqualTo("Updated Room Name");
    }

    [Test]
    public async Task SetParameter_ReadOnlyParameter_ThrowsClearError()
    {
        await Assert.That(_room).IsNotNull();

        var areaParameter = ElementParameterHelper.FindParameter(_room!, "Area");
        await Assert.That(areaParameter).IsNotNull();
        await Assert.That(areaParameter!.IsReadOnly).IsTrue();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
        {
            ElementParameterHelper.SetParameterValue(areaParameter, 100, _doc);
            return Task.CompletedTask;
        });

        await Assert.That(exception.Message).Contains("read-only");
    }

    [Test]
    public async Task SetParameter_TypeMismatch_ThrowsClearError()
    {
        await Assert.That(_room).IsNotNull();

        var nameParameter = ElementParameterHelper.FindParameter(_room!, "Name");
        await Assert.That(nameParameter).IsNotNull();

        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
        {
            ElementParameterHelper.SetParameterValue(nameParameter!, 123, _doc);
            return Task.CompletedTask;
        });

        await Assert.That(exception.Message).Contains("expects a string value");
    }

    private static void CreateEnclosure(Document doc, ElementId levelId, double originX, double originY, double size)
    {
        var p1 = new XYZ(originX, originY, 0);
        var p2 = new XYZ(originX + size, originY, 0);
        var p3 = new XYZ(originX + size, originY + size, 0);
        var p4 = new XYZ(originX, originY + size, 0);

        Wall.Create(doc, Line.CreateBound(p1, p2), levelId, false);
        Wall.Create(doc, Line.CreateBound(p2, p3), levelId, false);
        Wall.Create(doc, Line.CreateBound(p3, p4), levelId, false);
        Wall.Create(doc, Line.CreateBound(p4, p1), levelId, false);
    }
}
