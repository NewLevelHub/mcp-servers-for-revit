using Autodesk.Revit.DB;

namespace RevitMCPCommandSet.Utils.Detailing;

public class LayerHatch
{
    public FillPatternElement Pattern { get; set; }

    /// <summary>material, function or none — reported so the user knows what was guessed.</summary>
    public string Source { get; set; } = "none";

    public string PatternName => Pattern?.Name ?? string.Empty;
}

/// <summary>
///     Picks the hatch for a build-up layer: the material's own cut pattern first, a guess by layer
///     function only when the material has none.
/// </summary>
public static class LayerHatchResolver
{
    /// <summary>
    ///     Names tried for each layer function, Russian first because the templates in use are
    ///     Russian. Nothing is invented — a name that is not in the project simply does not match
    ///     and the layer is reported as unhatched.
    /// </summary>
    public static IReadOnlyList<string> CandidatesFor(string function)
    {
        return (function ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "structure" => new[] { "Бетон", "Железобетон", "Concrete", "Diagonal crosshatch", "Crosshatch" },
            "insulation" => new[] { "Утеплитель", "Изоляция", "Insulation", "Diagonal up" },
            "substrate" => new[] { "Песок", "Основание", "Sand", "Diagonal down" },
            "finish1" => new[] { "Отделка", "Штукатурка", "Finish", "Diagonal up - small", "Horizontal" },
            "finish2" => new[] { "Отделка 2", "Отделка", "Finish", "Horizontal", "Diagonal down - small" },
            "membrane" => Array.Empty<string>(),
            _ => new[] { "Diagonal up", "Штриховка" }
        };
    }

    public static LayerHatch Resolve(Document doc, AssemblyLayerInfo layer, string explicitPatternName)
    {
        var result = new LayerHatch();

        if (!string.IsNullOrWhiteSpace(explicitPatternName))
        {
            result.Pattern = FilledRegionTypes.FindPattern(doc, explicitPatternName);
            if (result.Pattern != null)
            {
                result.Source = "explicit";
                return result;
            }
        }

        if (layer == null)
            return result;

        // A material that declares its own cut pattern is the answer; anything else is a guess.
        if (layer.CutPatternId > 0 &&
            doc.GetElement(ElementIdExtensions.FromLong(layer.CutPatternId)) is FillPatternElement fromMaterial)
        {
            result.Pattern = fromMaterial;
            result.Source = "material";
            return result;
        }

        foreach (var candidate in CandidatesFor(layer.Function))
        {
            var pattern = FilledRegionTypes.FindPattern(doc, candidate);
            if (pattern == null)
                continue;

            result.Pattern = pattern;
            result.Source = "function";
            return result;
        }

        return result;
    }
}
