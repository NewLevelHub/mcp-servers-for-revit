using Newtonsoft.Json;

namespace RevitMCPCommandSet.Models.Common;

public class DocumentStylesResult
{
    [JsonProperty("success")]
    public bool Success { get; set; }

    [JsonProperty("message")]
    public string Message { get; set; } = string.Empty;

    [JsonProperty("dimensionTypes")]
    public List<DimensionTypeStyleInfo> DimensionTypes { get; set; } = new();

    [JsonProperty("gridTypes")]
    public List<NamedStyleInfo> GridTypes { get; set; } = new();

    [JsonProperty("textNoteTypes")]
    public List<TextNoteTypeStyleInfo> TextNoteTypes { get; set; } = new();

    [JsonProperty("linePatterns")]
    public List<NamedStyleInfo> LinePatterns { get; set; } = new();

    [JsonProperty("graphicsStyles")]
    public List<GraphicsStyleInfo> GraphicsStyles { get; set; } = new();

    /// <summary>Subcategories of OST_Lines — what "line style" means in the Revit UI.</summary>
    [JsonProperty("lineStyles")]
    public List<LineStyleInfo> LineStyles { get; set; } = new();

    [JsonProperty("filledRegionTypes")]
    public List<FilledRegionTypeStyleInfo> FilledRegionTypes { get; set; } = new();

    [JsonProperty("fillPatterns")]
    public List<FillPatternStyleInfo> FillPatterns { get; set; } = new();

    [JsonProperty("titleBlocks")]
    public List<TitleBlockStyleInfo> TitleBlocks { get; set; } = new();
}

public class NamedStyleInfo
{
    [JsonProperty("id")]
    public long Id { get; set; }

    [JsonProperty("uniqueId")]
    public string UniqueId { get; set; } = string.Empty;

    [JsonProperty("name")]
    public string Name { get; set; } = string.Empty;
}

public class DimensionTypeStyleInfo : NamedStyleInfo
{
    [JsonProperty("styleType")]
    public string StyleType { get; set; } = string.Empty;
}

public class TextNoteTypeStyleInfo : NamedStyleInfo
{
    [JsonProperty("textHeightMm")]
    public double? TextHeightMm { get; set; }

    [JsonProperty("font")]
    public string Font { get; set; } = string.Empty;
}

public class GraphicsStyleInfo : NamedStyleInfo
{
    [JsonProperty("graphicsStyleType")]
    public string GraphicsStyleType { get; set; } = string.Empty;

    [JsonProperty("category")]
    public string Category { get; set; } = string.Empty;
}

public class LineStyleInfo : NamedStyleInfo
{
    /// <summary>Line weight 1..16, null when the style does not define one.</summary>
    [JsonProperty("lineWeight")]
    public int? LineWeight { get; set; }

    [JsonProperty("linePatternName")]
    public string LinePatternName { get; set; } = string.Empty;

    /// <summary>Colour as #RRGGBB.</summary>
    [JsonProperty("color")]
    public string Color { get; set; } = string.Empty;
}

public class FilledRegionTypeStyleInfo : NamedStyleInfo
{
    [JsonProperty("foregroundPatternName")]
    public string ForegroundPatternName { get; set; } = string.Empty;

    [JsonProperty("backgroundPatternName")]
    public string BackgroundPatternName { get; set; } = string.Empty;

    [JsonProperty("foregroundColor")]
    public string ForegroundColor { get; set; } = string.Empty;

    [JsonProperty("isMasking")]
    public bool IsMasking { get; set; }
}

public class FillPatternStyleInfo : NamedStyleInfo
{
    /// <summary>Drafting patterns scale with the view, model patterns stay fixed in the model.</summary>
    [JsonProperty("target")]
    public string Target { get; set; } = string.Empty;

    [JsonProperty("isSolidFill")]
    public bool IsSolidFill { get; set; }
}

public class TitleBlockStyleInfo
{
    [JsonProperty("id")]
    public long Id { get; set; }

    [JsonProperty("uniqueId")]
    public string UniqueId { get; set; } = string.Empty;

    [JsonProperty("familyName")]
    public string FamilyName { get; set; } = string.Empty;

    [JsonProperty("typeName")]
    public string TypeName { get; set; } = string.Empty;
}
