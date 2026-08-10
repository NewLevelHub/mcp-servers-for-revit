using Newtonsoft.Json;

namespace RevitMCPCommandSet.Models.Common;

/// <summary>
///     点状构件
/// </summary>
public class PointElement
{
    public PointElement()
    {
        Parameters = new Dictionary<string, double>();
    }

    /// <summary>
    ///     构件类型
    /// </summary>
    [JsonProperty("category")]
    public string Category { get; set; } = "INVALID";

    /// <summary>
    ///     类型Id
    /// </summary>
    [JsonProperty("typeId")]
    public int TypeId { get; set; } = -1;

    /// <summary>
    ///     定位点坐标
    /// </summary>
    [JsonProperty("locationPoint")]
    public JZPoint LocationPoint { get; set; }

    /// <summary>
    ///     宽度
    /// </summary>
    [JsonProperty("width")]
    public double Width { get; set; } = -1;

    /// <summary>
    ///     深度
    /// </summary>
    [JsonProperty("depth")]
    public double Depth { get; set; }

    /// <summary>
    ///     高度
    /// </summary>
    [JsonProperty("height")]
    public double Height { get; set; }

    /// <summary>
    ///     底部标高
    /// </summary>
    [JsonProperty("baseLevel")]
    public double BaseLevel { get; set; }

    /// <summary>
    ///     底部偏移
    /// </summary>
    [JsonProperty("baseOffset")]
    public double BaseOffset { get; set; }

    /// <summary>
    ///     旋转角度（度），用于非宿主构件（如家具）
    /// </summary>
    [JsonProperty("rotation")]
    public double Rotation { get; set; } = 0;

    /// <summary>
    ///     Host wall ElementId. Required for doors/windows (−1 / missing → fail).
    ///     Optional for non-hosted families.
    /// </summary>
    [JsonProperty("hostWallId")]
    public int HostWallId { get; set; } = -1;

    /// <summary>
    ///     是否翻转门窗朝向
    /// </summary>
    [JsonProperty("facingFlipped")]
    public bool FacingFlipped { get; set; } = false;

    /// <summary>
    ///     REV-149: force a door hand (hinge side) flip. Independent of <see cref="FacingFlipped" /> —
    ///     a door has four states (facing × hand) and CAD gives both.
    /// </summary>
    [JsonProperty("handFlipped")]
    public bool HandFlipped { get; set; } = false;

    /// <summary>
    ///     REV-149: a point on the hinge side of the opening (along the wall). The handler
    ///     compares it against the live HandOrientation after placement and flips the hand
    ///     when the door came out mirrored against the CAD swing. Family-convention agnostic:
    ///     the actual orientation is read back rather than assumed.
    /// </summary>
    [JsonProperty("handHintPoint")]
    public JZPoint HandHintPoint { get; set; }

    /// <summary>
    ///     REV-149: place exactly at <see cref="LocationPoint" />. Disables end clamping,
    ///     junction nudging and batch auto-spacing; if the point cannot host the opening the
    ///     item fails loudly instead of being moved somewhere else.
    ///     Required for CAD redraw — the caller already knows the exact position.
    /// </summary>
    [JsonProperty("strictLocation")]
    public bool StrictLocation { get; set; } = false;

    /// <summary>
    ///     REV-149: max mm the placement may drift from <see cref="LocationPoint" /> in strict
    ///     mode before the item is rejected (default 50).
    /// </summary>
    [JsonProperty("strictToleranceMm")]
    public double StrictToleranceMm { get; set; } = 50;

    /// <summary>
    ///     参数化属性
    /// </summary>
    [JsonProperty("parameters")]
    public Dictionary<string, double> Parameters { get; set; }
}
