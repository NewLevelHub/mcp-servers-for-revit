using Newtonsoft.Json;

namespace RevitMCPCommandSet.Models.Architecture;

/// <summary>
///     REV-154: ask for a wall type of an exact thickness, creating it if the project has none.
/// </summary>
public class WallTypeRequestInfo
{
    /// <summary>Target wall thickness in mm, as measured between the DWG faces.</summary>
    [JsonProperty("thicknessMm")]
    public double ThicknessMm { get; set; }

    /// <summary>
    ///     WallType to copy when nothing matches. Its layers decide what the new type is made
    ///     of, so pass a type of the right construction (masonry, partition, curtain…).
    /// </summary>
    [JsonProperty("sourceTypeId")]
    public int SourceTypeId { get; set; } = -1;

    /// <summary>Name for the created type. Empty → "&lt;source&gt; &lt;thickness&gt;мм".</summary>
    [JsonProperty("typeName")]
    public string TypeName { get; set; }

    /// <summary>How far an existing type may be from the target before a new one is made.</summary>
    [JsonProperty("toleranceMm")]
    public double ToleranceMm { get; set; } = 5;
}

/// <summary>REV-154: the wall type that ended up being used, and whether it had to be created.</summary>
public class WallTypeResultInfo
{
    [JsonProperty("typeId")]
    public int TypeId { get; set; }

    [JsonProperty("typeName")]
    public string TypeName { get; set; } = string.Empty;

    [JsonProperty("thicknessMm")]
    public double ThicknessMm { get; set; }

    /// <summary>True when this type did not exist and was duplicated from the source.</summary>
    [JsonProperty("created")]
    public bool Created { get; set; }

    [JsonProperty("success")]
    public bool Success { get; set; }

    [JsonProperty("message")]
    public string Message { get; set; } = string.Empty;
}
