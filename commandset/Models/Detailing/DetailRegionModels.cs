using Newtonsoft.Json;

namespace RevitMCPCommandSet.Models.Detailing;

/// <summary>
///     One hatched area of a detail: an outer contour in mm plus optional holes.
/// </summary>
public class DetailRegionInfo
{
    /// <summary>Outer contour, mm in the view plane. Closing point may be omitted.</summary>
    [JsonProperty("points")]
    public List<DetailLinePoint> Points { get; set; } = new List<DetailLinePoint>();

    /// <summary>Inner contours cut out of the region, mm.</summary>
    [JsonProperty("holes")]
    public List<List<DetailLinePoint>> Holes { get; set; } = new List<List<DetailLinePoint>>();

    /// <summary>Existing filled region type by name; wins over fillPatternName.</summary>
    [JsonProperty("filledRegionTypeName")]
    public string FilledRegionTypeName { get; set; } = string.Empty;

    /// <summary>
    ///     Hatch by fill pattern name (e.g. «Бетон», «Diagonal crosshatch»). When no filled region
    ///     type in the project draws with it, one is created — see createMissingTypes.
    /// </summary>
    [JsonProperty("fillPatternName")]
    public string FillPatternName { get; set; } = string.Empty;

    /// <summary>Label written into Comments alongside the tag, to identify the region later.</summary>
    [JsonProperty("label")]
    public string Label { get; set; } = string.Empty;
}

public class DetailRegionsCreationInfo
{
    [JsonProperty("viewId")]
    public long ViewId { get; set; }

    [JsonProperty("viewUniqueId")]
    public string ViewUniqueId { get; set; } = string.Empty;

    [JsonProperty("viewName")]
    public string ViewName { get; set; } = string.Empty;

    [JsonProperty("regions")]
    public List<DetailRegionInfo> Regions { get; set; } = new List<DetailRegionInfo>();

    /// <summary>Fallback type for regions that name neither a type nor a pattern.</summary>
    [JsonProperty("filledRegionTypeName")]
    public string FilledRegionTypeName { get; set; } = string.Empty;

    /// <summary>Duplicate a filled region type when the requested hatch has none. Default true.</summary>
    [JsonProperty("createMissingTypes")]
    public bool CreateMissingTypes { get; set; } = true;

    /// <summary>Delete regions previously created by this tool on the view before drawing.</summary>
    [JsonProperty("clearPrevious")]
    public bool ClearPrevious { get; set; }

    /// <summary>Delete previous regions and create nothing.</summary>
    [JsonProperty("clearOnly")]
    public bool ClearOnly { get; set; }

    [JsonProperty("commentTag")]
    public string CommentTag { get; set; } = string.Empty;
}

public class DetailRegionCreatedItem
{
    [JsonProperty("regionId")]
    public long RegionId { get; set; }

    [JsonProperty("label")]
    public string Label { get; set; } = string.Empty;

    [JsonProperty("filledRegionType")]
    public string FilledRegionType { get; set; } = string.Empty;

    [JsonProperty("holes")]
    public int Holes { get; set; }
}

public class DetailRegionsCreationResult
{
    [JsonProperty("success")]
    public bool Success { get; set; }

    [JsonProperty("message")]
    public string Message { get; set; } = string.Empty;

    [JsonProperty("viewId")]
    public long ViewId { get; set; }

    [JsonProperty("viewName")]
    public string ViewName { get; set; } = string.Empty;

    [JsonProperty("createdCount")]
    public int CreatedCount { get; set; }

    [JsonProperty("created")]
    public List<DetailRegionCreatedItem> Created { get; set; } = new List<DetailRegionCreatedItem>();

    /// <summary>Filled region types created for hatches the project did not have.</summary>
    [JsonProperty("createdTypes")]
    public List<string> CreatedTypes { get; set; } = new List<string>();

    [JsonProperty("deletedPreviousCount")]
    public int DeletedPreviousCount { get; set; }

    [JsonProperty("commentTag")]
    public string CommentTag { get; set; } = string.Empty;

    [JsonProperty("availableTypes")]
    public List<string> AvailableTypes { get; set; } = new List<string>();

    [JsonProperty("availablePatterns")]
    public List<string> AvailablePatterns { get; set; } = new List<string>();

    [JsonProperty("warnings")]
    public List<string> Warnings { get; set; } = new List<string>();
}
