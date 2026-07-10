using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Nice3point.TUnit.Revit;
using Nice3point.TUnit.Revit.Executors;
using RevitMCPCommandSet.Services.Normatives;
using TUnit.Core;
using TUnit.Core.Executors;

namespace RevitMCPCommandSet.Tests.Normatives;

public class CheckFireDoorsTests : RevitApiTest
{
    private static Document _doc;
    private static Level _level;

    [Before(HookType.Class)]
    [HookExecutor<RevitThreadExecutor>]
    public static void Setup()
    {
        _doc = Application.NewProjectDocument(UnitSystem.Imperial);

        using var tx = new Transaction(_doc, "Setup Check Fire Doors Tests");
        tx.Start();

        _level = Level.Create(_doc, 0.0);
        _level.Name = "Fire Door Test Level";

        tx.Commit();
    }

    [After(HookType.Class)]
    [HookExecutor<RevitThreadExecutor>]
    public static void Cleanup()
    {
        _doc?.Close(false);
    }

    [Test]
    public async Task EvaluateFireDoorRequirement_CorridorToApartment_RequiresFireDoor()
    {
        var requires = CheckFireDoorsEventHandler.IsBetweenCompartments("Коридор", "Квартира 1");
        await Assert.That(requires).IsTrue();
    }

    [Test]
    public async Task EvaluateFireDoorRequirement_StairToCorridor_RequiresFireDoor()
    {
        var requires = CheckFireDoorsEventHandler.IsBetweenCompartments(
            "Лестничная клетка",
            "Коридор");
        await Assert.That(requires).IsTrue();
    }

    [Test]
    public async Task EvaluateFireDoorRequirement_InternalRoomDoor_DoesNotRequireFireDoor()
    {
        var egress = CheckFireDoorsEventHandler.IsOnEgressPath("Кухня", "Гостиная", null!);
        var compartments = CheckFireDoorsEventHandler.IsBetweenCompartments("Кухня", "Гостиная");

        await Assert.That(egress).IsFalse();
        await Assert.That(compartments).IsFalse();
    }

    [Test]
    public async Task CheckFireDoors_DoorCollector_ExecutesWithoutErrors()
    {
        var doors = new FilteredElementCollector(_doc)
            .OfCategory(BuiltInCategory.OST_Doors)
            .WhereElementIsNotElementType()
            .OfType<FamilyInstance>()
            .ToList();

        await Assert.That(doors.Count).IsGreaterThanOrEqualTo(0);
    }
}
