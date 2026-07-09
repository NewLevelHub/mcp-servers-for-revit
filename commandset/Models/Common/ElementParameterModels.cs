using Newtonsoft.Json;

namespace RevitMCPCommandSet.Models.Common;

public class ElementParametersResult
{
    [JsonProperty("success")]
    public bool Success { get; set; }

    [JsonProperty("message")]
    public string Message { get; set; } = string.Empty;

    [JsonProperty("elementId")]
    public long ElementId { get; set; }

    [JsonProperty("elementName")]
    public string ElementName { get; set; } = string.Empty;

    [JsonProperty("category")]
    public string Category { get; set; } = string.Empty;

    [JsonProperty("parameters")]
    public List<ElementParameterInfo> Parameters { get; set; } = new();
}

public class ElementParameterInfo
{
    [JsonProperty("name")]
    public string Name { get; set; } = string.Empty;

    [JsonProperty("displayValue")]
    public string DisplayValue { get; set; } = string.Empty;

    [JsonProperty("rawValue")]
    public object? RawValue { get; set; }

    [JsonProperty("storageType")]
    public string StorageType { get; set; } = string.Empty;

    [JsonProperty("unitType")]
    public string UnitType { get; set; } = string.Empty;

    [JsonProperty("isReadOnly")]
    public bool IsReadOnly { get; set; }

    [JsonProperty("hasValue")]
    public bool HasValue { get; set; }

    [JsonProperty("isShared")]
    public bool IsShared { get; set; }

    [JsonProperty("builtInParameter")]
    public string? BuiltInParameter { get; set; }
}

public class SetElementParameterResult
{
    [JsonProperty("success")]
    public bool Success { get; set; }

    [JsonProperty("message")]
    public string Message { get; set; } = string.Empty;

    [JsonProperty("elementId")]
    public long ElementId { get; set; }

    [JsonProperty("parameterName")]
    public string ParameterName { get; set; } = string.Empty;

    [JsonProperty("newDisplayValue")]
    public string NewDisplayValue { get; set; } = string.Empty;
}
