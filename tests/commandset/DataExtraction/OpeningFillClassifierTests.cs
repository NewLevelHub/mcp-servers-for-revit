using RevitMCPCommandSet.Utils;
using TUnit.Core;

namespace RevitMCPCommandSet.Tests.DataExtraction;

/// <summary>
/// Name-heuristic coverage for REV-41 (door slopes) / REV-48 (window accessories).
/// Live acceptance on «Короткий блок»: door schedule total ≈ 160 (not 643).
/// </summary>
public class OpeningFillClassifierTests
{
    [Test]
    [Arguments("(откос)двери_внутренний", "Тип 1")]
    [Arguments("(Откос)Двери_наружный", "")]
    [Arguments("двери_обвязка", "стандарт")]
    [Arguments("Door Reveal", "100mm")]
    [Arguments("наличник_двери", "")]
    [Arguments("добор_дверной", "тип А")]
    public async Task DoorAccessory_IsExcluded(string familyName, string typeName)
    {
        await Assert.That(OpeningFillClassifier.IsDoorAccessory(familyName, typeName)).IsTrue();
    }

    [Test]
    [Arguments("Дверь 1", "900x2100")]
    [Arguments("(дверь)внутренняя", "Д1")]
    [Arguments("Single-Flush", "36\" x 84\"")]
    [Arguments("ДВ_01", "тип 1")]
    public async Task DoorBlock_IsSchedulable(string familyName, string typeName)
    {
        await Assert.That(OpeningFillClassifier.IsDoorAccessory(familyName, typeName)).IsFalse();
    }

    [Test]
    [Arguments("(откос)окно_внутренний", "")]
    [Arguments("подоконник_ПВХ", "тип 1")]
    [Arguments("слив_окна", "")]
    [Arguments("Window Reveal", "50mm")]
    public async Task WindowAccessory_IsExcluded(string familyName, string typeName)
    {
        await Assert.That(OpeningFillClassifier.IsWindowAccessory(familyName, typeName)).IsTrue();
    }

    [Test]
    [Arguments("(окно)ОДБ-1", "ОДБ-1")]
    [Arguments("Fixed", "36\" x 48\"")]
    [Arguments("ОК-1", "тип А")]
    public async Task WindowBlock_IsSchedulable(string familyName, string typeName)
    {
        await Assert.That(OpeningFillClassifier.IsWindowAccessory(familyName, typeName)).IsFalse();
    }

    [Test]
    public async Task KorotkiyBlok_DoorNames_SlopeExcludedDoorKept()
    {
        // Evidence from live test 2026-07-13 on «Короткий блок»:
        // raw OST_Doors = 643, RD schedule = 160 → ~483 slopes named like (откос)двери_*
        const string slopeFamily = "(откос)двери_внутренний";
        const string doorFamily = "Дверь 1";

        const int rawOstDoorsCount = 643;
        const int slopeShare = 483;
        const int expectedDoorBlocks = rawOstDoorsCount - slopeShare;

        await Assert.That(OpeningFillClassifier.IsDoorAccessory(slopeFamily)).IsTrue();
        await Assert.That(OpeningFillClassifier.IsDoorAccessory(doorFamily)).IsFalse();
        await Assert.That(expectedDoorBlocks).IsEqualTo(160);
    }
}
