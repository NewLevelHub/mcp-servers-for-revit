using Newtonsoft.Json;

namespace RevitMCPCommandSet.Models.Architecture;

public class StairResultInfo
{
    [JsonProperty("elementId")]
    public int ElementId { get; set; }

    [JsonProperty("uniqueId")]
    public string UniqueId { get; set; }

    [JsonProperty("typeId")]
    public int TypeId { get; set; }

    [JsonProperty("typeName")]
    public string TypeName { get; set; }

    [JsonProperty("layout")]
    public string Layout { get; set; }

    [JsonProperty("baseLevelId")]
    public int BaseLevelId { get; set; }

    [JsonProperty("topLevelId")]
    public int TopLevelId { get; set; }

    [JsonProperty("appliedWidthMm")]
    public double AppliedWidthMm { get; set; }

    [JsonProperty("actualRiserHeightMm")]
    public double ActualRiserHeightMm { get; set; }

    [JsonProperty("actualTreadDepthMm")]
    public double ActualTreadDepthMm { get; set; }

    [JsonProperty("runCount")]
    public int RunCount { get; set; }

    [JsonProperty("landingCount")]
    public int LandingCount { get; set; }

    [JsonProperty("actualNumRisers")]
    public int ActualNumRisers { get; set; }

    [JsonProperty("warnings")]
    public List<string> Warnings { get; set; }
}
