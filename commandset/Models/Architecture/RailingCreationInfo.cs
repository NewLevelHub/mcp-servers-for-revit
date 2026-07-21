using Newtonsoft.Json;
using RevitMCPCommandSet.Models.Common;

namespace RevitMCPCommandSet.Models.Architecture;

/// <summary>
/// Parameters for creating a railing by path or hosted on a stair (REV-83).
/// Coordinates and dimensions are in millimeters.
/// </summary>
public class RailingCreationInfo
{
    public RailingCreationInfo()
    {
        PathPoints = new List<JZPoint>();
    }

    /// <summary>Path points for free-standing railing (mm). Require ≥2 when not hosting.</summary>
    [JsonProperty("pathPoints")]
    public List<JZPoint> PathPoints { get; set; }

    /// <summary>Base level ElementId for path mode.</summary>
    [JsonProperty("levelId")]
    public int LevelId { get; set; }

    /// <summary>Level offset in mm (path mode).</summary>
    [JsonProperty("levelOffsetMm")]
    public double LevelOffsetMm { get; set; }

    /// <summary>
    /// Desired railing height mm (informational / validation).
    /// Height is encoded by RailingType (e.g. ADSK … h 1200); type is not mutated.
    /// </summary>
    [JsonProperty("heightMm")]
    public double HeightMm { get; set; }

    /// <summary>Host stairs ElementId. When &gt; 0, path is ignored and railing is placed on the host.</summary>
    [JsonProperty("hostElementId")]
    public int HostElementId { get; set; } = -1;

    /// <summary>Close the path as a loop (path mode).</summary>
    [JsonProperty("isClosedLoop")]
    public bool IsClosedLoop { get; set; }

    /// <summary>RailingType ElementId — required. Missing/invalid fails explicitly.</summary>
    [JsonProperty("typeId")]
    public int TypeId { get; set; } = -1;
}
