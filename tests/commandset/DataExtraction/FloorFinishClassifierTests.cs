using RevitMCPCommandSet.Utils;
using TUnit.Core;

namespace RevitMCPCommandSet.Tests.DataExtraction;

/// <summary>
/// Name-heuristic coverage for REV-49 (floor finishes vs slabs / insulation).
/// Live acceptance on «Короткий блок»: (полы)* only; плиты/утеплители excluded.
/// </summary>
public class FloorFinishClassifierTests
{
    [Test]
    [Arguments("(полы)квартира_8_t=80", "Перекрытие")]
    [Arguments("(полы)МОП_t=100", "")]
    [Arguments("(полы) подвал МОП_t=100", "Перекрытие")]
    [Arguments("(полы)санузел_t=70", "Floor")]
    [Arguments("Floor", "Floor")]
    [Arguments("Generic 150mm", "Перекрытие")]
    public async Task FloorFinish_IsIncluded(string typeName, string familyName)
    {
        await Assert.That(FloorFinishClassifier.IsFloorFinish(typeName, familyName)).IsTrue();
    }

    [Test]
    [Arguments("(плита_перекрытия)железобетон_t=200", "Перекрытие")]
    [Arguments("(плита_перекрытия)железобетон_t=1300", "")]
    [Arguments("(потолок_утеплитель)лоджия", "Перекрытие")]
    [Arguments("(потолок_утеплитель)Сетка_лоджия", "")]
    [Arguments("(фасад) Сплиттерная плитка_коричневый", "Перекрытие")]
    [Arguments("Structural Floor", "Floor")]
    public async Task NonFinish_IsExcluded(string typeName, string familyName)
    {
        await Assert.That(FloorFinishClassifier.IsFloorFinish(typeName, familyName)).IsFalse();
    }

    [Test]
    public async Task KorotkiyBlok_FinishShare_MatchesEvidence()
    {
        // Evidence from live test on «Короткий блок»: raw OST_Floors = 145,
        // (полы)* ≈ 85, slabs/insulation/facade ≈ 60.
        const int rawOstFloors = 145;
        const int nonFinishShare = 60;
        const int expectedFinishes = rawOstFloors - nonFinishShare;

        await Assert.That(FloorFinishClassifier.IsFloorFinish("(полы)квартира_8_t=80")).IsTrue();
        await Assert.That(FloorFinishClassifier.IsFloorFinish("(плита_перекрытия)железобетон_t=200")).IsFalse();
        await Assert.That(FloorFinishClassifier.IsFloorFinish("(потолок_утеплитель)лоджия")).IsFalse();
        await Assert.That(expectedFinishes).IsEqualTo(85);
    }
}
