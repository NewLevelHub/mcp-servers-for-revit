using Newtonsoft.Json;

namespace RevitMCPCommandSet.Models.Detailing;

/// <summary>
///     A build-up layer the type does not carry but the node must show — an extra screed, an
///     underlay, sound insulation.
///     <para>
///     It is drawn like every other layer: full length, right through the assembly. Local pieces
///     of a node — a skirting, edge tape in the gap, a bead of mastic — are not layers and cannot
///     be expressed here; they belong to place_detail_component or create_detail_lines.
///     </para>
/// </summary>
public class NodeExtraLayerInfo
{
    [JsonProperty("name")]
    public string Name { get; set; } = string.Empty;

    [JsonProperty("thicknessMm")]
    public double ThicknessMm { get; set; }

    /// <summary>wall or floor (in single mode both mean the one assembly being drawn).</summary>
    [JsonProperty("target")]
    public string Target { get; set; } = "floor";

    /// <summary>Layer function used to pick a hatch when fillPatternName is omitted.</summary>
    [JsonProperty("function")]
    public string Function { get; set; } = "Finish1";

    [JsonProperty("fillPatternName")]
    public string FillPatternName { get; set; } = string.Empty;

    /// <summary>0-based position in the build-up; omit (or -1) to append at the end.</summary>
    [JsonProperty("insertAt")]
    public int InsertAt { get; set; } = -1;
}

public class NodeDetailCreationInfo
{
    /// <summary>junction — a floor meeting a wall; single — one build-up in section.</summary>
    [JsonProperty("mode")]
    public string Mode { get; set; } = "junction";

    /// <summary>Wall type id, or the id of a wall instance whose type should be read.</summary>
    [JsonProperty("wallTypeId")]
    public long WallTypeId { get; set; }

    /// <summary>Floor type id, or the id of a floor instance whose type should be read.</summary>
    [JsonProperty("floorTypeId")]
    public long FloorTypeId { get; set; }

    /// <summary>single mode: vertical draws a wall, horizontal draws a floor.</summary>
    [JsonProperty("orientation")]
    public string Orientation { get; set; } = "horizontal";

    [JsonProperty("name")]
    public string Name { get; set; } = string.Empty;

    [JsonProperty("scale")]
    public int Scale { get; set; } = 10;

    /// <summary>Visible run of the assembly, mm.</summary>
    [JsonProperty("lengthMm")]
    public double LengthMm { get; set; } = 600;

    /// <summary>How far the wall is drawn above the floor, mm.</summary>
    [JsonProperty("wallRunMm")]
    public double WallRunMm { get; set; } = 500;

    /// <summary>Expansion gap between the floor finish build-up and the wall, mm.</summary>
    [JsonProperty("gapMm")]
    public double GapMm { get; set; } = 20;

    [JsonProperty("annotate")]
    public bool Annotate { get; set; } = true;

    [JsonProperty("drawHatches")]
    public bool DrawHatches { get; set; } = true;

    [JsonProperty("createMissingTypes")]
    public bool CreateMissingTypes { get; set; } = true;

    [JsonProperty("dimensionTypeName")]
    public string DimensionTypeName { get; set; } = string.Empty;

    [JsonProperty("textTypeName")]
    public string TextTypeName { get; set; } = string.Empty;

    [JsonProperty("lineStyleName")]
    public string LineStyleName { get; set; } = string.Empty;

    [JsonProperty("extraLayers")]
    public List<NodeExtraLayerInfo> ExtraLayers { get; set; } = new List<NodeExtraLayerInfo>();

    /// <summary>Place the finished node on this existing sheet.</summary>
    [JsonProperty("sheetNumber")]
    public string SheetNumber { get; set; } = string.Empty;

    [JsonProperty("activateView")]
    public bool ActivateView { get; set; } = true;
}

public class NodeLayerReport
{
    [JsonProperty("index")]
    public int Index { get; set; }

    [JsonProperty("name")]
    public string Name { get; set; } = string.Empty;

    [JsonProperty("thicknessMm")]
    public double ThicknessMm { get; set; }

    [JsonProperty("function")]
    public string Function { get; set; } = string.Empty;

    [JsonProperty("hatchPattern")]
    public string HatchPattern { get; set; } = string.Empty;

    /// <summary>material — the material's own cut pattern; function — a fallback by layer function; none.</summary>
    [JsonProperty("hatchSource")]
    public string HatchSource { get; set; } = "none";
}

public class NodeAssemblyReport
{
    [JsonProperty("typeId")]
    public long TypeId { get; set; }

    [JsonProperty("typeName")]
    public string TypeName { get; set; } = string.Empty;

    [JsonProperty("category")]
    public string Category { get; set; } = string.Empty;

    [JsonProperty("totalThicknessMm")]
    public double TotalThicknessMm { get; set; }

    [JsonProperty("layers")]
    public List<NodeLayerReport> Layers { get; set; } = new List<NodeLayerReport>();
}

public class NodeDetailCreationResult
{
    [JsonProperty("success")]
    public bool Success { get; set; }

    [JsonProperty("message")]
    public string Message { get; set; } = string.Empty;

    [JsonProperty("mode")]
    public string Mode { get; set; } = string.Empty;

    [JsonProperty("viewId")]
    public long ViewId { get; set; }

    [JsonProperty("viewUniqueId")]
    public string ViewUniqueId { get; set; } = string.Empty;

    [JsonProperty("viewName")]
    public string ViewName { get; set; } = string.Empty;

    [JsonProperty("scale")]
    public int Scale { get; set; }

    [JsonProperty("wall")]
    public NodeAssemblyReport Wall { get; set; }

    [JsonProperty("floor")]
    public NodeAssemblyReport Floor { get; set; }

    [JsonProperty("layersRead")]
    public int LayersRead { get; set; }

    [JsonProperty("regionsCreated")]
    public int RegionsCreated { get; set; }

    /// <summary>Layers drawn without a hatch because neither the material nor the function resolved one.</summary>
    [JsonProperty("layersWithoutHatch")]
    public int LayersWithoutHatch { get; set; }

    [JsonProperty("curvesCreated")]
    public int CurvesCreated { get; set; }

    [JsonProperty("dimensionsCreated")]
    public int DimensionsCreated { get; set; }

    [JsonProperty("notesCreated")]
    public int NotesCreated { get; set; }

    [JsonProperty("createdFilledRegionTypes")]
    public List<string> CreatedFilledRegionTypes { get; set; } = new List<string>();

    [JsonProperty("sheetId")]
    public long SheetId { get; set; }

    [JsonProperty("sheetNumber")]
    public string SheetNumber { get; set; } = string.Empty;

    [JsonProperty("warnings")]
    public List<string> Warnings { get; set; } = new List<string>();
}
