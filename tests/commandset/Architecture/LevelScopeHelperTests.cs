using RevitMCPCommandSet.Utils;
using TUnit.Core;

namespace RevitMCPCommandSet.Tests.Architecture;

/// <summary>
/// REV-177's floor-number extraction — pure, no Revit document required.
/// ResolveLevelsInRange itself needs a live Document (a FilteredElementCollector over
/// real Level elements) and is covered by live verification instead, same split as
/// the rest of this ticket's Revit-dependent geometry.
/// </summary>
public class LevelScopeHelperTests
{
    [Test]
    public async Task TryExtractFloorNumber_ReadsPlainDigits()
    {
        var ok = LevelScopeHelper.TryExtractFloorNumber("3 этаж", out var number);
        await Assert.That(ok).IsTrue();
        await Assert.That(number).IsEqualTo(3);
    }

    [Test]
    public async Task TryExtractFloorNumber_WorksRegardlessOfWordOrder()
    {
        var ok = LevelScopeHelper.TryExtractFloorNumber("этаж 16", out var number);
        await Assert.That(ok).IsTrue();
        await Assert.That(number).IsEqualTo(16);
    }

    [Test]
    public async Task TryExtractFloorNumber_ReadsNegativeBasementLevels()
    {
        var ok = LevelScopeHelper.TryExtractFloorNumber("-1 этаж", out var number);
        await Assert.That(ok).IsTrue();
        await Assert.That(number).IsEqualTo(-1);
    }

    [Test]
    public async Task TryExtractFloorNumber_FailsForNonNumericLevelNames()
    {
        var ok = LevelScopeHelper.TryExtractFloorNumber("Кровля", out _);
        await Assert.That(ok).IsFalse();
    }

    [Test]
    public async Task TryExtractFloorNumber_FailsForBlankInput()
    {
        var ok = LevelScopeHelper.TryExtractFloorNumber("", out _);
        await Assert.That(ok).IsFalse();
    }
}
