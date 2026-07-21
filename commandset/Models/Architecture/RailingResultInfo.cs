using Newtonsoft.Json;

namespace RevitMCPCommandSet.Models.Architecture;

public class RailingResultInfo
{
    [JsonProperty("elementId")]
    public int ElementId { get; set; }

    [JsonProperty("uniqueId")]
    public string UniqueId { get; set; }

    [JsonProperty("typeId")]
    public int TypeId { get; set; }

    [JsonProperty("typeName")]
    public string TypeName { get; set; }

    [JsonProperty("hostElementId")]
    public int HostElementId { get; set; }

    [JsonProperty("levelId")]
    public int LevelId { get; set; }

    [JsonProperty("appliedHeightMm")]
    public double? AppliedHeightMm { get; set; }
}
