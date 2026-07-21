using Newtonsoft.Json;

namespace RevitMCPCommandSet.Models.Architecture;

public class FloorOpeningResultInfo
{
    [JsonProperty("elementId")]
    public int ElementId { get; set; }

    [JsonProperty("uniqueId")]
    public string UniqueId { get; set; }

    [JsonProperty("mode")]
    public string Mode { get; set; }

    [JsonProperty("hostFloorId")]
    public int? HostFloorId { get; set; }

    [JsonProperty("baseLevelId")]
    public int? BaseLevelId { get; set; }

    [JsonProperty("topLevelId")]
    public int? TopLevelId { get; set; }

    [JsonProperty("boundaryPointCount")]
    public int BoundaryPointCount { get; set; }

    [JsonProperty("widthMm")]
    public double? WidthMm { get; set; }

    [JsonProperty("depthMm")]
    public double? DepthMm { get; set; }
}
