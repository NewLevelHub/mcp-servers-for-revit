using Newtonsoft.Json;
using System.Collections.Generic;
using System.Linq;

namespace RevitMCPCommandSet.Models.Architecture;

/// <summary>
/// Model for creating grid system with smart spacing generation,
/// explicit positions, or auto-alignment to load-bearing wall centerlines.
/// </summary>
public class GridCreationInfo
{
    /// <summary>
    /// Number of grid lines along X-axis (vertical grids). Ignored when xPositionsMm or autoFromWalls is set.
    /// </summary>
    [JsonProperty("xCount")]
    public int XCount { get; set; }

    /// <summary>
    /// Spacing between X-axis grid lines in millimeters. Ignored when xPositionsMm or autoFromWalls is set.
    /// </summary>
    [JsonProperty("xSpacing")]
    public double XSpacing { get; set; }

    /// <summary>
    /// Starting label for X-axis grids (e.g., "A", "1", or "А")
    /// </summary>
    [JsonProperty("xStartLabel")]
    public string XStartLabel { get; set; } = "1";

    /// <summary>
    /// Naming style for X-axis: "alphabetic", "numeric", or "cyrillic"
    /// </summary>
    [JsonProperty("xNamingStyle")]
    public string XNamingStyle { get; set; } = "numeric";

    /// <summary>
    /// Number of grid lines along Y-axis (horizontal grids). Ignored when yPositionsMm or autoFromWalls is set.
    /// </summary>
    [JsonProperty("yCount")]
    public int YCount { get; set; }

    /// <summary>
    /// Spacing between Y-axis grid lines in millimeters. Ignored when yPositionsMm or autoFromWalls is set.
    /// </summary>
    [JsonProperty("ySpacing")]
    public double YSpacing { get; set; }

    /// <summary>
    /// Starting label for Y-axis grids (e.g., "1", "A", or "А")
    /// </summary>
    [JsonProperty("yStartLabel")]
    public string YStartLabel { get; set; } = "А";

    /// <summary>
    /// Naming style for Y-axis: "alphabetic", "numeric", or "cyrillic"
    /// </summary>
    [JsonProperty("yNamingStyle")]
    public string YNamingStyle { get; set; } = "cyrillic";

    /// <summary>
    /// Minimum extent along X-axis in millimeters (from project base point)
    /// </summary>
    [JsonProperty("xExtentMin")]
    public double XExtentMin { get; set; } = 0;

    /// <summary>
    /// Maximum extent along X-axis in millimeters (from project base point)
    /// </summary>
    [JsonProperty("xExtentMax")]
    public double XExtentMax { get; set; } = 50000;

    /// <summary>
    /// Minimum extent along Y-axis in millimeters (from project base point)
    /// </summary>
    [JsonProperty("yExtentMin")]
    public double YExtentMin { get; set; } = 0;

    /// <summary>
    /// Maximum extent along Y-axis in millimeters (from project base point)
    /// </summary>
    [JsonProperty("yExtentMax")]
    public double YExtentMax { get; set; } = 50000;

    /// <summary>
    /// Elevation for grid lines in millimeters (Z-coordinate)
    /// </summary>
    [JsonProperty("elevation")]
    public double Elevation { get; set; } = 0;

    /// <summary>
    /// Starting position for first X-axis grid in millimeters
    /// </summary>
    [JsonProperty("xStartPosition")]
    public double XStartPosition { get; set; } = 0;

    /// <summary>
    /// Starting position for first Y-axis grid in millimeters
    /// </summary>
    [JsonProperty("yStartPosition")]
    public double YStartPosition { get; set; } = 0;

    /// <summary>
    /// Explicit X positions in mm (vertical grids). When set, overrides xCount/xSpacing/xStartPosition.
    /// </summary>
    [JsonProperty("xPositionsMm")]
    public List<double> XPositionsMm { get; set; } = new();

    /// <summary>
    /// Explicit Y positions in mm (horizontal grids). When set, overrides yCount/ySpacing/yStartPosition.
    /// </summary>
    [JsonProperty("yPositionsMm")]
    public List<double> YPositionsMm { get; set; } = new();

    /// <summary>
    /// Place grids on load-bearing wall centerlines of the active (or specified) level.
    /// Matches architectural practice: axes through structural walls, extents beyond the building.
    /// </summary>
    [JsonProperty("autoFromWalls")]
    public bool AutoFromWalls { get; set; } = false;

    /// <summary>
    /// Wall filter for autoFromWalls: "structural" (default), "exterior", or "all".
    /// </summary>
    [JsonProperty("wallFilter")]
    public string WallFilter { get; set; } = "structural";

    /// <summary>
    /// Level name for autoFromWalls. Empty = active floor plan level.
    /// </summary>
    [JsonProperty("levelName")]
    public string LevelName { get; set; } = string.Empty;

    /// <summary>
    /// Minimum wall thickness (mm) treated as structural when autoFromWalls=true. Default 300.
    /// </summary>
    [JsonProperty("minWallThicknessMm")]
    public double MinWallThicknessMm { get; set; } = 400;

    /// <summary>
    /// Merge wall centerlines closer than this (mm). Default 280 (merges face vs center location-line duplicates on ~500 mm walls).
    /// </summary>
    [JsonProperty("clusterToleranceMm")]
    public double ClusterToleranceMm { get; set; } = 280;

    /// <summary>
    /// How far grid extents extend beyond the wall bounding box (mm).
    /// Default 4000 — leaves room for 2 exterior dimension tiers + bubbles outside.
    /// </summary>
    [JsonProperty("extentOvershootMm")]
    public double ExtentOvershootMm { get; set; } = 4000;

    /// <summary>
    /// When true (default with autoFromWalls / explicit positions), recompute extents from walls/positions.
    /// </summary>
    [JsonProperty("autoComputeExtents")]
    public bool? AutoComputeExtents { get; set; }

    /// <summary>
    /// GridType name from the project for bubble style.
    /// </summary>
    [JsonProperty("gridTypeName")]
    public string GridTypeName { get; set; } = string.Empty;

    /// <summary>
    /// GridType element ID from the project.
    /// </summary>
    [JsonProperty("gridTypeId")]
    public int GridTypeId { get; set; } = -1;

    /// <summary>
    /// Configure 2D grid extents and bubbles on all floor plans after creation.
    /// </summary>
    [JsonProperty("configureDisplayOnAllPlans")]
    public bool ConfigureDisplayOnAllPlans { get; set; } = true;

    /// <summary>
    /// Show grid bubbles on floor plans when display is configured.
    /// </summary>
    [JsonProperty("showBubbles")]
    public bool ShowBubbles { get; set; } = true;

    /// <summary>
    /// Which end shows the bubble. Default "bottomLeft" = numbers below, letters left (working-drawing).
    /// Use "both" only when both ends are explicitly requested.
    /// </summary>
    [JsonProperty("bubbleEnd")]
    public string BubbleEnd { get; set; } = "bottomLeft";

    [JsonIgnore]
    public bool HasExplicitXPositions => XPositionsMm != null && XPositionsMm.Count > 0;

    [JsonIgnore]
    public bool HasExplicitYPositions => YPositionsMm != null && YPositionsMm.Count > 0;

    /// <summary>
    /// Validates the grid creation parameters
    /// </summary>
    public bool Validate(out string errorMessage)
    {
        var styles = new[] { "alphabetic", "numeric", "cyrillic" };
        if (!styles.Contains(XNamingStyle))
        {
            errorMessage = "xNamingStyle must be 'alphabetic', 'numeric', or 'cyrillic'";
            return false;
        }

        if (!styles.Contains(YNamingStyle))
        {
            errorMessage = "yNamingStyle must be 'alphabetic', 'numeric', or 'cyrillic'";
            return false;
        }

        if (string.IsNullOrWhiteSpace(XStartLabel))
        {
            errorMessage = "xStartLabel cannot be empty";
            return false;
        }

        if (string.IsNullOrWhiteSpace(YStartLabel))
        {
            errorMessage = "yStartLabel cannot be empty";
            return false;
        }

        if (AutoFromWalls)
        {
            errorMessage = string.Empty;
            return true;
        }

        if (!HasExplicitXPositions)
        {
            if (XCount <= 0)
            {
                errorMessage = "xCount must be greater than 0 (or provide xPositionsMm / autoFromWalls)";
                return false;
            }

            if (XSpacing <= 0)
            {
                errorMessage = "xSpacing must be greater than 0 (or provide xPositionsMm / autoFromWalls)";
                return false;
            }
        }

        if (!HasExplicitYPositions)
        {
            if (YCount <= 0)
            {
                errorMessage = "yCount must be greater than 0 (or provide yPositionsMm / autoFromWalls)";
                return false;
            }

            if (YSpacing <= 0)
            {
                errorMessage = "ySpacing must be greater than 0 (or provide yPositionsMm / autoFromWalls)";
                return false;
            }
        }

        var extentsDeferred = ShouldAutoComputeExtents();
        if (!extentsDeferred)
        {
            if (XExtentMin >= XExtentMax)
            {
                errorMessage = "xExtentMin must be less than xExtentMax";
                return false;
            }

            if (YExtentMin >= YExtentMax)
            {
                errorMessage = "yExtentMin must be less than yExtentMax";
                return false;
            }
        }

        errorMessage = string.Empty;
        return true;
    }

    public bool ShouldAutoComputeExtents()
    {
        if (AutoComputeExtents.HasValue)
            return AutoComputeExtents.Value;

        // Auto when deriving from walls, or when caller left default 0..50000 with explicit positions
        if (AutoFromWalls)
            return true;

        if (HasExplicitXPositions || HasExplicitYPositions)
            return XExtentMin == 0 && XExtentMax == 50000 && YExtentMin == 0 && YExtentMax == 50000;

        return false;
    }
}
