using Newtonsoft.Json;
using RevitMCPCommandSet.Models.Common;

namespace RevitMCPCommandSet.Models.Architecture;

/// <summary>
/// Parameters for cutting openings in floors / shafts (REV-85).
/// Coordinates and dimensions are in millimeters.
/// </summary>
public class FloorOpeningCreationInfo
{
    public FloorOpeningCreationInfo()
    {
        Mode = "floor";
        BoundaryPoints = new List<JZPoint>();
        PerpendicularFace = true;
    }

    /// <summary>
    /// "floor" — cut one host Floor; "shaft" — vertical shaft between levels.
    /// </summary>
    [JsonProperty("mode")]
    public string Mode { get; set; }

    /// <summary>Host Floor ElementId (mode=floor). Preferred when known.</summary>
    [JsonProperty("hostFloorId")]
    public int HostFloorId { get; set; }

    /// <summary>
    /// Level of the slab to cut (mode=floor). Used to find a Floor when hostFloorId omitted.
    /// Pick the floor whose plan bbox contains the opening centroid.
    /// </summary>
    [JsonProperty("levelId")]
    public int LevelId { get; set; }

    /// <summary>Bottom level for mode=shaft.</summary>
    [JsonProperty("baseLevelId")]
    public int BaseLevelId { get; set; }

    /// <summary>Top level for mode=shaft (must be above base).</summary>
    [JsonProperty("topLevelId")]
    public int TopLevelId { get; set; }

    /// <summary>
    /// Closed plan polygon (≥3 points, mm). Z ignored (projected to XY).
    /// Mutually exclusive with rect — provide one.
    /// </summary>
    [JsonProperty("boundaryPoints")]
    public List<JZPoint> BoundaryPoints { get; set; }

    /// <summary>
    /// Axis-aligned (or rotated) rectangle shortcut. Mutually exclusive with boundaryPoints.
    /// </summary>
    [JsonProperty("rect")]
    public FloorOpeningRect Rect { get; set; }

    /// <summary>
    /// For floor openings: true = cut perpendicular to host face (default).
    /// </summary>
    [JsonProperty("perpendicularFace")]
    public bool PerpendicularFace { get; set; }
}

/// <summary>Rectangle defined by origin (min corner before rotation) + size in mm.</summary>
public class FloorOpeningRect
{
    /// <summary>Origin X/Y in mm (before rotation). Typically SW corner.</summary>
    [JsonProperty("origin")]
    public JZPoint Origin { get; set; }

    [JsonProperty("widthMm")]
    public double WidthMm { get; set; }

    [JsonProperty("depthMm")]
    public double DepthMm { get; set; }

    /// <summary>CCW rotation around origin in degrees (default 0).</summary>
    [JsonProperty("rotationDeg")]
    public double RotationDeg { get; set; }
}
