using Newtonsoft.Json;
using RevitMCPCommandSet.Models.Common;

namespace RevitMCPCommandSet.Models.Architecture;

/// <summary>
/// Parameters for creating stairs (REV-83+): straight, L (Г), or U (П).
/// Coordinates and dimensions are in millimeters.
/// </summary>
public class StairCreationInfo
{
    public StairCreationInfo()
    {
        PathPoints = new List<JZPoint>();
        Layout = "straight";
        Turn = "right";
    }

    /// <summary>
    /// Stair plan layout: "straight" | "L" (Г-образная) | "U" (П-образная).
    /// Aliases accepted by handler: g/г → L, p/п → U.
    /// </summary>
    [JsonProperty("layout")]
    public string Layout { get; set; }

    /// <summary>Start of the first run path centerline (mm). Z overridden to base level.</summary>
    [JsonProperty("startPoint")]
    public JZPoint StartPoint { get; set; }

    /// <summary>
    /// For straight: end of the run.
    /// For L/U: optional direction hint (vector start→end); length ignored when risers auto-sized.
    /// </summary>
    [JsonProperty("endPoint")]
    public JZPoint EndPoint { get; set; }

    /// <summary>
    /// Bearing of the first run in degrees from +X toward +Y (CCW).
    /// 0 = east (+X), 90 = north (+Y). Used for L/U when endPoint is omitted.
    /// </summary>
    [JsonProperty("bearingDeg")]
    public double? BearingDeg { get; set; }

    /// <summary>
    /// Turn direction when ascending for L/U: "left" or "right" (default right).
    /// </summary>
    [JsonProperty("turn")]
    public string Turn { get; set; }

    /// <summary>Landing depth along the first-run direction (mm). Default = widthMm.</summary>
    [JsonProperty("landingDepthMm")]
    public double LandingDepthMm { get; set; }

    /// <summary>
    /// Explicit first-run length (mm). If 0, computed from height / riser / tread split.
    /// </summary>
    [JsonProperty("firstRunLengthMm")]
    public double FirstRunLengthMm { get; set; }

    /// <summary>
    /// Explicit second-run length (mm) for L/U. If 0, computed automatically.
    /// </summary>
    [JsonProperty("secondRunLengthMm")]
    public double SecondRunLengthMm { get; set; }

    /// <summary>Optional path points (reserved).</summary>
    [JsonProperty("pathPoints")]
    public List<JZPoint> PathPoints { get; set; }

    /// <summary>Base level ElementId.</summary>
    [JsonProperty("baseLevelId")]
    public int BaseLevelId { get; set; }

    /// <summary>Top level ElementId (must be above base).</summary>
    [JsonProperty("topLevelId")]
    public int TopLevelId { get; set; }

    /// <summary>Run width in mm (normative min typically 900–1350).</summary>
    [JsonProperty("widthMm")]
    public double WidthMm { get; set; }

    /// <summary>Desired riser height mm (used to split risers for L/U; StairsType still applies).</summary>
    [JsonProperty("riserHeightMm")]
    public double RiserHeightMm { get; set; }

    /// <summary>Desired tread depth mm (used to size run paths for L/U).</summary>
    [JsonProperty("treadDepthMm")]
    public double TreadDepthMm { get; set; }

    /// <summary>StairsType ElementId — required. Missing/invalid fails explicitly.</summary>
    [JsonProperty("typeId")]
    public int TypeId { get; set; }

    /// <summary>
    /// Clear stair-shaft rectangle in plan (mm). For layout U/L: fits both runs + landing
    /// inside this box (compact cell like typical floors). Origin = SW corner before rotation.
    /// </summary>
    [JsonProperty("shaftRect")]
    public FloorOpeningRect ShaftRect { get; set; }

    /// <summary>
    /// Existing Stairs ElementId — use its plan bbox as shaftRect (stack under a correct
    /// reference stair, e.g. copy 2→3 footprint for 1→2).
    /// </summary>
    [JsonProperty("mirrorElementId")]
    public int MirrorElementId { get; set; }

    /// <summary>
    /// When shaft is shorter than ideal (risers−1)×tread:
    /// "clamp" (default) = keep compact footprint;
    /// "extend" = allow run longer than shaft (old behaviour);
    /// "strict" = fail if shaft cannot fit ideal run length.
    /// </summary>
    [JsonProperty("fitMode")]
    public string FitMode { get; set; }
}
