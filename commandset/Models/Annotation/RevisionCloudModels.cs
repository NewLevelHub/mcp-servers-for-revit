using Newtonsoft.Json;

namespace RevitMCPCommandSet.Models.Annotation;

/// <summary>
///     One cloud's worth of the diff, already clustered and rectangled by
///     <c>utils/revisionClouds.ts</c> (REV-172). The plugin draws exactly this
///     rectangle — it does not re-cluster or re-decide anything about which
///     changes belong together.
/// </summary>
public class RevisionCloudClusterInfo
{
    [JsonProperty("level")]
    public string Level { get; set; } = string.Empty;

    /// <summary>Stable identity of this exact set of changed elements — how a re-run recognises its own cloud.</summary>
    [JsonProperty("signature")]
    public string Signature { get; set; } = string.Empty;

    [JsonProperty("changeCount")]
    public int ChangeCount { get; set; }

    [JsonProperty("minXMm")]
    public double MinXMm { get; set; }

    [JsonProperty("minYMm")]
    public double MinYMm { get; set; }

    [JsonProperty("maxXMm")]
    public double MaxXMm { get; set; }

    [JsonProperty("maxYMm")]
    public double MaxYMm { get; set; }

    /// <summary>Written into the cloud's Comments, signature first, so a person reading it in Revit sees what changed.</summary>
    [JsonProperty("comment")]
    public string Comment { get; set; } = string.Empty;
}

/// <summary>Explicit view choice for a level, when auto-resolution would be ambiguous or wrong.</summary>
public class RevisionCloudViewOverride
{
    [JsonProperty("level")]
    public string Level { get; set; } = string.Empty;

    [JsonProperty("viewName")]
    public string ViewName { get; set; } = string.Empty;
}

public class RevisionCloudsCreationInfo
{
    [JsonProperty("revisionDescription")]
    public string RevisionDescription { get; set; } = string.Empty;

    [JsonProperty("viewMap")]
    public List<RevisionCloudViewOverride> ViewMap { get; set; } = new List<RevisionCloudViewOverride>();

    [JsonProperty("clusters")]
    public List<RevisionCloudClusterInfo> Clusters { get; set; } = new List<RevisionCloudClusterInfo>();
}

public class CreatedRevisionCloudItem
{
    [JsonProperty("cloudId")]
    public long CloudId { get; set; }

    [JsonProperty("level")]
    public string Level { get; set; } = string.Empty;

    [JsonProperty("viewName")]
    public string ViewName { get; set; } = string.Empty;

    /// <summary>Empty when the view is not yet placed on any sheet — the cloud exists but no revision table sees it.</summary>
    [JsonProperty("sheetNumber")]
    public string SheetNumber { get; set; } = string.Empty;

    [JsonProperty("sheetName")]
    public string SheetName { get; set; } = string.Empty;

    [JsonProperty("signature")]
    public string Signature { get; set; } = string.Empty;

    [JsonProperty("changeCount")]
    public int ChangeCount { get; set; }
}

public class SkippedRevisionCloudItem
{
    [JsonProperty("level")]
    public string Level { get; set; } = string.Empty;

    [JsonProperty("signature")]
    public string Signature { get; set; } = string.Empty;

    [JsonProperty("changeCount")]
    public int ChangeCount { get; set; }

    /// <summary>"already exists" for a repeat run, or why no cloud could be drawn at all.</summary>
    [JsonProperty("reason")]
    public string Reason { get; set; } = string.Empty;
}

public class RevisionCloudsCreationResult
{
    [JsonProperty("success")]
    public bool Success { get; set; }

    [JsonProperty("message")]
    public string Message { get; set; } = string.Empty;

    [JsonProperty("revisionId")]
    public long RevisionId { get; set; }

    [JsonProperty("revisionNumber")]
    public int RevisionNumber { get; set; }

    [JsonProperty("created")]
    public List<CreatedRevisionCloudItem> Created { get; set; } = new List<CreatedRevisionCloudItem>();

    [JsonProperty("skipped")]
    public List<SkippedRevisionCloudItem> Skipped { get; set; } = new List<SkippedRevisionCloudItem>();

    [JsonProperty("warnings")]
    public List<string> Warnings { get; set; } = new List<string>();
}
