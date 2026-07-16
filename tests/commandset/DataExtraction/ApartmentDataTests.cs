using RevitMCPCommandSet.Models.DataExtraction;
using RevitMCPCommandSet.Utils;
using TUnit.Core;

namespace RevitMCPCommandSet.Tests.DataExtraction;

/// <summary>
/// Квартирография: room classification and apartment aggregation per
/// СП РК 3.02-101-2012*, приложение А, п. А.8 (лоджии 0,5; балконы/террасы 0,3;
/// веранды 0,8; совмещённые 0,4). Pure tests — no Revit document required.
/// </summary>
public class ApartmentRoomClassifierTests
{
    [Test]
    [Arguments("Спальня")]
    [Arguments("Гостиная")]
    [Arguments("Детская")]
    [Arguments("Кабинет")]
    [Arguments("Столовая")]
    [Arguments("Кухня-гостиная")]
    [Arguments("Жилая комната")]
    public async Task LivingRooms_AreClassifiedLiving(string name)
    {
        var category = ApartmentRoomClassifier.Classify(name, out _, out var coefficient);

        await Assert.That(category).IsEqualTo(ApartmentRoomCategory.Living);
        await Assert.That(coefficient).IsEqualTo(1.0);
    }

    [Test]
    [Arguments("Кухня")]
    [Arguments("Кухня-ниша")]
    [Arguments("Кухня-столовая")]
    [Arguments("Коридор")]
    [Arguments("Прихожая")]
    [Arguments("Санузел")]
    [Arguments("С/У совмещенный")]
    [Arguments("Ванная")]
    [Arguments("Гардеробная")]
    [Arguments("Постирочная")]
    [Arguments("Нежилое помещение")]
    public async Task AuxiliaryRooms_AreClassifiedAuxiliary(string name)
    {
        var category = ApartmentRoomClassifier.Classify(name, out _, out var coefficient);

        await Assert.That(category).IsEqualTo(ApartmentRoomCategory.Auxiliary);
        await Assert.That(coefficient).IsEqualTo(1.0);
    }

    [Test]
    [Arguments("Лоджия", "loggia", 0.5)]
    [Arguments("Балкон", "balcony", 0.3)]
    [Arguments("Терраса", "terrace", 0.3)]
    [Arguments("Веранда", "veranda", 0.8)]
    [Arguments("Совмещенная лоджия", "combined", 0.4)]
    [Arguments("Балкон совмещенный", "combined", 0.4)]
    public async Task SummerRooms_GetNormCoefficients(string name, string expectedKind, double expectedCoefficient)
    {
        var category = ApartmentRoomClassifier.Classify(name, out var kind, out var coefficient);

        await Assert.That(category).IsEqualTo(ApartmentRoomCategory.Summer);
        await Assert.That(kind).IsEqualTo(expectedKind);
        await Assert.That(coefficient).IsEqualTo(expectedCoefficient);
    }

    [Test]
    public async Task ApartmentType_FollowsLivingRoomCount()
    {
        await Assert.That(ApartmentRoomClassifier.GetApartmentType(0)).IsEqualTo("Студия");
        await Assert.That(ApartmentRoomClassifier.GetApartmentType(1)).IsEqualTo("1К");
        await Assert.That(ApartmentRoomClassifier.GetApartmentType(3)).IsEqualTo("3К");
    }
}

public class ApartmentAggregatorTests
{
    private static ApartmentRoomInput Room(string apartment, string name, double areaM2, string level = "Этаж 1")
    {
        return new ApartmentRoomInput
        {
            Id = 1,
            Name = name,
            Level = level,
            AreaM2 = areaM2,
            ApartmentNumber = apartment
        };
    }

    private static List<ApartmentRoomInput> SampleRooms()
    {
        return new List<ApartmentRoomInput>
        {
            // Кв. 1 — 1К с лоджией
            Room("1", "Спальня", 12.0),
            Room("1", "Кухня", 9.0),
            Room("1", "Санузел", 4.0),
            Room("1", "Коридор", 5.0),
            Room("1", "Лоджия", 4.0),
            // Кв. 2 — 2К с балконом
            Room("2", "Спальня", 12.0),
            Room("2", "Гостиная", 18.0),
            Room("2", "Кухня", 10.0),
            Room("2", "С/У", 4.0),
            Room("2", "Балкон", 3.0),
            // МОП без номера квартиры
            Room("", "Лестничная клетка", 15.0)
        };
    }

    [Test]
    public async Task Aggregate_ComputesAreasPerNorm()
    {
        var result = ApartmentAggregator.Aggregate("Тест", "Номер квартиры", SampleRooms(), includeRooms: true);

        await Assert.That(result.Success).IsTrue();
        await Assert.That(result.TotalApartments).IsEqualTo(2);
        await Assert.That(result.UnassignedRoomCount).IsEqualTo(1);

        var flat1 = result.Apartments[0];
        await Assert.That(flat1.ApartmentNumber).IsEqualTo("1");
        await Assert.That(flat1.Type).IsEqualTo("1К");
        await Assert.That(flat1.LivingAreaM2).IsEqualTo(12.0);
        await Assert.That(flat1.UsefulAreaM2).IsEqualTo(30.0);
        await Assert.That(flat1.SummerAreaM2).IsEqualTo(4.0);
        await Assert.That(flat1.SummerAreaReducedM2).IsEqualTo(2.0); // лоджия 4 × 0,5
        await Assert.That(flat1.TotalAreaM2).IsEqualTo(32.0);
        await Assert.That(flat1.Rooms!.Count).IsEqualTo(5);

        var flat2 = result.Apartments[1];
        await Assert.That(flat2.Type).IsEqualTo("2К");
        await Assert.That(flat2.LivingAreaM2).IsEqualTo(30.0);
        await Assert.That(flat2.UsefulAreaM2).IsEqualTo(44.0);
        await Assert.That(flat2.SummerAreaReducedM2).IsEqualTo(0.9); // балкон 3 × 0,3
        await Assert.That(flat2.TotalAreaM2).IsEqualTo(44.9);
    }

    [Test]
    public async Task Aggregate_BuildsTypeSummaryAndTotals()
    {
        var result = ApartmentAggregator.Aggregate("Тест", "Номер квартиры", SampleRooms(), includeRooms: false);

        await Assert.That(result.ByType.Count).IsEqualTo(2);

        var oneRoom = result.ByType.First(summary => summary.Type == "1К");
        await Assert.That(oneRoom.ApartmentCount).IsEqualTo(1);
        await Assert.That(oneRoom.SharePercent).IsEqualTo(50.0);

        await Assert.That(result.Totals.LivingAreaM2).IsEqualTo(42.0);
        await Assert.That(result.Totals.UsefulAreaM2).IsEqualTo(74.0);
        await Assert.That(result.Totals.SummerAreaReducedM2).IsEqualTo(2.9);
        await Assert.That(result.Totals.TotalAreaM2).IsEqualTo(76.9);

        // rooms omitted without includeRooms
        await Assert.That(result.Apartments[0].Rooms).IsNull();
    }

    [Test]
    public async Task Aggregate_CitesNormForCoefficients()
    {
        var result = ApartmentAggregator.Aggregate("Тест", "Номер квартиры", SampleRooms(), includeRooms: false);

        await Assert.That(result.Norm.Code).IsEqualTo("СП РК 3.02-101-2012*");
        await Assert.That(result.Norm.Clause).Contains("А.8");
        await Assert.That(result.Norm.Quote).Contains("лоджий - 0,5");
        await Assert.That(result.Norm.Quote).Contains("балконов и террас - 0,3");
        await Assert.That(result.Norm.Coefficients["loggia"]).IsEqualTo(0.5);
        await Assert.That(result.Norm.Coefficients["balcony"]).IsEqualTo(0.3);
    }

    [Test]
    public async Task Aggregate_SortsApartmentNumbersNumerically()
    {
        var rooms = new List<ApartmentRoomInput>
        {
            Room("10", "Спальня", 12.0),
            Room("9", "Спальня", 12.0),
            Room("2", "Спальня", 12.0)
        };

        var result = ApartmentAggregator.Aggregate("Тест", "Номер квартиры", rooms, includeRooms: false);

        await Assert.That(result.Apartments.Select(a => a.ApartmentNumber).ToList())
            .IsEquivalentTo(new List<string> { "2", "9", "10" });
    }

    [Test]
    public async Task Aggregate_NoAssignedRooms_FailsExplicitly()
    {
        var rooms = new List<ApartmentRoomInput> { Room("", "Лестничная клетка", 15.0) };

        var result = ApartmentAggregator.Aggregate("Тест", "Номер квартиры", rooms, includeRooms: false);

        await Assert.That(result.Success).IsFalse();
        await Assert.That(result.Message).Contains("apartmentNumberParameter");
    }
}
