using RevitMCPCommandSet.Utils.Detailing;
using TUnit.Core;

namespace RevitMCPCommandSet.Tests.Detailing;

public class LayerHatchResolverTests
{
    [Test]
    public async Task StructureLooksForConcreteFirst()
    {
        var candidates = LayerHatchResolver.CandidatesFor("Structure");

        await Assert.That(candidates[0]).IsEqualTo("Бетон");
        await Assert.That(candidates).Contains("Concrete");
    }

    [Test]
    public async Task FunctionMatchingIsCaseInsensitive()
    {
        var upper = LayerHatchResolver.CandidatesFor("INSULATION");
        var lower = LayerHatchResolver.CandidatesFor("insulation");

        await Assert.That(upper).IsEquivalentTo(lower.ToList());
    }

    [Test]
    public async Task MembraneHasNoHatch()
    {
        // A membrane is a line on the drawing; hatching a zero-width layer is not a thing.
        await Assert.That(LayerHatchResolver.CandidatesFor("Membrane").Count).IsEqualTo(0);
    }

    [Test]
    public async Task UnknownFunctionStillOffersAFallback()
    {
        await Assert.That(LayerHatchResolver.CandidatesFor("Whatever").Count).IsGreaterThan(0);
        await Assert.That(LayerHatchResolver.CandidatesFor(null).Count).IsGreaterThan(0);
    }
}
