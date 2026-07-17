using Newtonsoft.Json;
using RevitMCPCommandSet.Models.Common;

namespace RevitMCPCommandSet.Models.Annotation;

/// <summary>
/// One text note (Annotate → Text) to place on a plan, optionally with a leader.
/// Coordinates are millimetres in project space.
/// </summary>
public class TextNotePlacementInfo
{
    /// <summary>Text content (short remark, not a full norm quote).</summary>
    [JsonProperty("text")]
    public string Text { get; set; } = "";

    /// <summary>
    /// Text note insertion point (mm). Optional when <see cref="ElementId"/> is set —
    /// then the handler offsets from the element centre.
    /// </summary>
    [JsonProperty("locationMm")]
    public JZPoint LocationMm { get; set; }

    /// <summary>
    /// Leader end point (mm). When omitted and <see cref="ElementId"/> is set,
    /// the leader points at the element centre.
    /// </summary>
    [JsonProperty("leaderToMm")]
    public JZPoint LeaderToMm { get; set; }

    /// <summary>
    /// Element to annotate: used for auto text placement and/or leader end.
    /// </summary>
    [JsonProperty("elementId")]
    public long? ElementId { get; set; }

    /// <summary>
    /// TextNoteType name from the project (e.g. ADSK_Замечания).
    /// Falls back to the batch default, then project default.
    /// </summary>
    [JsonProperty("textTypeName")]
    public string TextTypeName { get; set; }

    /// <summary>Text box width in mm. 0 = Revit minimum for the type.</summary>
    [JsonProperty("widthMm")]
    public double WidthMm { get; set; }

    /// <summary>
    /// When placement=near: offset from element centre (mm).
    /// When placement=outside: margin from plan outline to the text column (mm).
    /// </summary>
    [JsonProperty("offsetMm")]
    public double OffsetMm { get; set; }

    /// <summary>
    /// When false, skip AddLeader even if elementId / leaderToMm is present.
    /// Default true when omitted (JSON null → treat as true in handler).
    /// </summary>
    [JsonProperty("leader")]
    public bool? Leader { get; set; }
}

/// <summary>
/// Optional detail line (Annotate → Detail Line) without text.
/// </summary>
public class DetailLinePlacementInfo
{
    [JsonProperty("fromMm")]
    public JZPoint FromMm { get; set; }

    [JsonProperty("toMm")]
    public JZPoint ToMm { get; set; }
}
