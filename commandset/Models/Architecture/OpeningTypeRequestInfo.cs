using Newtonsoft.Json;

namespace RevitMCPCommandSet.Models.Architecture;

/// <summary>
///     REV-153: ask for a door / window type of an exact size, creating it if the project
///     has none.
/// </summary>
public class OpeningTypeRequestInfo
{
    /// <summary>Target opening width in mm, as traced from the DWG.</summary>
    [JsonProperty("widthMm")]
    public double WidthMm { get; set; }

    /// <summary>Target opening height in mm. 0 keeps the source type's height.</summary>
    [JsonProperty("heightMm")]
    public double HeightMm { get; set; }

    /// <summary>
    ///     FamilySymbol to copy when nothing matches. Its family decides what the new type
    ///     looks like, so pass the family the drawing actually calls for.
    /// </summary>
    [JsonProperty("sourceTypeId")]
    public int SourceTypeId { get; set; } = -1;

    /// <summary>Name for the created type. Empty → "&lt;width&gt; x &lt;height&gt; мм".</summary>
    [JsonProperty("typeName")]
    public string TypeName { get; set; }

    /// <summary>How far an existing type may be from the target before a new one is made.</summary>
    [JsonProperty("toleranceMm")]
    public double ToleranceMm { get; set; } = 5;
}

/// <summary>REV-153: the type that ended up being used, and whether it had to be created.</summary>
public class OpeningTypeResultInfo
{
    [JsonProperty("typeId")]
    public int TypeId { get; set; }

    [JsonProperty("typeName")]
    public string TypeName { get; set; } = string.Empty;

    [JsonProperty("familyName")]
    public string FamilyName { get; set; } = string.Empty;

    [JsonProperty("widthMm")]
    public double WidthMm { get; set; }

    [JsonProperty("heightMm")]
    public double HeightMm { get; set; }

    /// <summary>True when this type did not exist and was duplicated from the source.</summary>
    [JsonProperty("created")]
    public bool Created { get; set; }

    [JsonProperty("success")]
    public bool Success { get; set; }

    [JsonProperty("message")]
    public string Message { get; set; } = string.Empty;
}
