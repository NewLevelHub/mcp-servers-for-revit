using Autodesk.Revit.DB;

namespace RevitMCPCommandSet.Utils.Detailing;

/// <summary>
///     One layer of a wall/floor/roof/ceiling build-up, in mm, with everything a detail needs
///     to draw it: how thick, what it is made of, and which hatch the material cuts with.
/// </summary>
public class AssemblyLayerInfo
{
    /// <summary>Index in the compound structure, exterior/top side first (as Revit orders them).</summary>
    public int Index { get; set; }

    public double ThicknessMm { get; set; }

    /// <summary>MaterialFunctionAssignment name: Structure, Substrate, Insulation, Finish1, Finish2, Membrane.</summary>
    public string Function { get; set; } = string.Empty;

    public string MaterialName { get; set; } = string.Empty;

    public long MaterialId { get; set; }

    /// <summary>
    ///     Membranes are zero-width by definition. They must be drawn as a single line —
    ///     a filled region with no area throws in Revit.
    /// </summary>
    public bool IsZeroWidth { get; set; }

    /// <summary>Layer sits between the core boundaries (load-bearing part of the build-up).</summary>
    public bool IsCore { get; set; }

    /// <summary>Cut foreground pattern of the layer material, 0 when the material has none.</summary>
    public long CutPatternId { get; set; }

    public string CutPatternName { get; set; } = string.Empty;

    /// <summary>Cut foreground pattern colour as #RRGGBB, empty when unset.</summary>
    public string CutPatternColor { get; set; } = string.Empty;
}

public class AssemblyStructureInfo
{
    public long TypeId { get; set; }

    public string TypeName { get; set; } = string.Empty;

    /// <summary>Wall, Floor, Roof or Ceiling.</summary>
    public string Category { get; set; } = string.Empty;

    public double TotalThicknessMm { get; set; }

    public List<AssemblyLayerInfo> Layers { get; set; } = new List<AssemblyLayerInfo>();

    /// <summary>Layers whose material carries no cut pattern — the caller must fall back by function.</summary>
    public List<string> Warnings { get; set; } = new List<string>();
}

/// <summary>
///     Reads the layered build-up of a host type. The same three lines were being rewritten in
///     schedules, room export and wall-type matching; details need one more thing on top of that —
///     the material cut pattern, so a generated node hatches concrete as concrete.
/// </summary>
public static class CompoundStructureReader
{
    /// <summary>
    ///     Accepts either a type id or an instance id (a Wall, Floor, RoofBase or Ceiling) and
    ///     returns the host type carrying the compound structure.
    /// </summary>
    public static HostObjAttributes ResolveHostType(Document doc, long elementId)
    {
        if (doc == null || elementId <= 0)
            return null;

        var element = doc.GetElement(ElementIdExtensions.FromLong(elementId));
        if (element == null)
            return null;

        if (element is HostObjAttributes hostType)
            return hostType;

        var typeId = element.GetTypeId();
        if (typeId == null || typeId == ElementId.InvalidElementId)
            return null;

        return doc.GetElement(typeId) as HostObjAttributes;
    }

    public static AssemblyStructureInfo Read(Document doc, HostObjAttributes hostType)
    {
        if (doc == null)
            throw new ArgumentNullException(nameof(doc));
        if (hostType == null)
            throw new ArgumentNullException(nameof(hostType));

        var structure = hostType.GetCompoundStructure()
            ?? throw new InvalidOperationException(
                $"Type '{hostType.Name}' has no compound structure. Curtain walls and some " +
                "in-place types are not layered, so a layer-by-layer node cannot be generated from them.");

        var layers = structure.GetLayers();
        if (layers.Count == 0)
            throw new InvalidOperationException($"Type '{hostType.Name}' has a compound structure with no layers.");

        var firstCore = SafeCoreIndex(() => structure.GetFirstCoreLayerIndex(), 0);
        var lastCore = SafeCoreIndex(() => structure.GetLastCoreLayerIndex(), layers.Count - 1);

        var result = new AssemblyStructureInfo
        {
            TypeId = hostType.Id.GetValue(),
            TypeName = hostType.Name,
            Category = DescribeCategory(hostType),
            TotalThicknessMm = Math.Round(RevitUnitConversion.ToMillimeters(structure.GetWidth()), 2)
        };

        for (var i = 0; i < layers.Count; i++)
        {
            var layer = layers[i];
            var thicknessMm = Math.Round(RevitUnitConversion.ToMillimeters(layer.Width), 2);

            var info = new AssemblyLayerInfo
            {
                Index = i,
                ThicknessMm = thicknessMm,
                Function = layer.Function.ToString(),
                IsZeroWidth = thicknessMm <= 0.01,
                IsCore = i >= firstCore && i <= lastCore,
                MaterialId = 0
            };

            var material = layer.MaterialId != null && layer.MaterialId != ElementId.InvalidElementId
                ? doc.GetElement(layer.MaterialId) as Material
                : null;

            if (material != null)
            {
                info.MaterialName = material.Name;
                info.MaterialId = material.Id.GetValue();
                ReadCutPattern(doc, material, info);
            }
            else
            {
                result.Warnings.Add(
                    $"Layer {i + 1} ({info.Function}, {thicknessMm} mm) has no material — " +
                    "its hatch will be picked by layer function.");
            }

            if (material != null && info.CutPatternId == 0 && !info.IsZeroWidth)
            {
                result.Warnings.Add(
                    $"Material '{info.MaterialName}' has no cut pattern — " +
                    $"the hatch for layer {i + 1} will be picked by layer function.");
            }

            result.Layers.Add(info);
        }

        return result;
    }

    private static void ReadCutPattern(Document doc, Material material, AssemblyLayerInfo info)
    {
        var patternId = material.CutForegroundPatternId;
        if (patternId == null || patternId == ElementId.InvalidElementId)
            return;

        info.CutPatternId = patternId.GetValue();
        if (doc.GetElement(patternId) is FillPatternElement pattern)
            info.CutPatternName = pattern.Name;

        var color = material.CutForegroundPatternColor;
        if (color != null && color.IsValid)
            info.CutPatternColor = $"#{color.Red:X2}{color.Green:X2}{color.Blue:X2}";
    }

    /// <summary>
    ///     GetFirstCoreLayerIndex throws on structures without a defined core (single-layer floors,
    ///     for instance), so fall back to treating the whole build-up as core.
    /// </summary>
    private static int SafeCoreIndex(Func<int> read, int fallback)
    {
        try
        {
            return read();
        }
        catch
        {
            return fallback;
        }
    }

    private static string DescribeCategory(HostObjAttributes hostType)
    {
        return hostType switch
        {
            WallType => "Wall",
            FloorType => "Floor",
            RoofType => "Roof",
            CeilingType => "Ceiling",
            _ => hostType.Category?.Name ?? hostType.GetType().Name
        };
    }
}
