using Newtonsoft.Json;

namespace RevitMCPCommandSet.Models.Annotation;

/// <summary>
///     Parameters for creating exterior axial dimension chains from grids,
///     offset from the full building envelope (not from axis lines).
/// </summary>
public class GridDimensionInfo
{
    /// <summary>
    ///     Grid element IDs. Empty = all grids in the document.
    /// </summary>
    [JsonProperty("gridIds")]
    public List<int> GridIds { get; set; } = new();

    /// <summary>
    ///     Offset of the first (inter-axis) chain beyond the building envelope (mm).
    ///     Default 1200 — clears walls, loggias, entrances.
    /// </summary>
    [JsonProperty("firstOffsetMm")]
    public double FirstOffsetMm { get; set; } = 1200;

    /// <summary>
    ///     Gap between dimension tiers (mm). Default 800.
    /// </summary>
    [JsonProperty("tierGapMm")]
    public double TierGapMm { get; set; } = 800;

    /// <summary>
    ///     Create overall (extreme-grid) chains on the outer tier. Default true.
    /// </summary>
    [JsonProperty("includeOverall")]
    public bool IncludeOverall { get; set; } = true;

    /// <summary>
    ///     Side for numeric (vertical) grids: "bottom" (default) or "top".
    /// </summary>
    [JsonProperty("numericSide")]
    public string NumericSide { get; set; } = "bottom";

    /// <summary>
    ///     Side for letter (horizontal) grids: "left" (default) or "right".
    /// </summary>
    [JsonProperty("letterSide")]
    public string LetterSide { get; set; } = "left";

    /// <summary>
    ///     DimensionType name from the project. Empty = ADSK working-drawing linear type.
    /// </summary>
    [JsonProperty("dimensionType")]
    public string DimensionType { get; set; } = "ADSK_Основной_2.5 мм";

    /// <summary>
    ///     Dimension style element ID. -1 = resolve by name / ADSK default.
    /// </summary>
    [JsonProperty("dimensionStyleId")]
    public int DimensionStyleId { get; set; } = -1;

    /// <summary>
    ///     View ID. -1 = active view.
    /// </summary>
    [JsonProperty("viewId")]
    public int ViewId { get; set; } = -1;

    /// <summary>
    ///     Extra padding added to wall bbox for face thickness (mm). Default 250.
    /// </summary>
    [JsonProperty("envelopePaddingMm")]
    public double EnvelopePaddingMm { get; set; } = 250;

    /// <summary>
    ///     Extend grid 2D extents past the outer dimension tier so bubbles sit outside.
    /// </summary>
    [JsonProperty("extendGridExtents")]
    public bool ExtendGridExtents { get; set; } = true;

    /// <summary>
    ///     Extra overshoot beyond the outer tier for bubbles (mm). Default 1200.
    /// </summary>
    [JsonProperty("bubbleClearanceMm")]
    public double BubbleClearanceMm { get; set; } = 1200;
}
