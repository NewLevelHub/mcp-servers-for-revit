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

public class ElementsParametersBatchResult
{
    [JsonProperty("success")]
    public bool Success { get; set; }

    [JsonProperty("message")]
    public string Message { get; set; } = string.Empty;

    [JsonProperty("results")]
    public List<ElementParametersResult> Results { get; set; } = new();
}

/// <summary>One element and the parameters to write on it, for the batch write.</summary>
public class ElementParameterEdit
{
    public long ElementId { get; set; }

    /// <summary>Parameter name (Russian or English — see ElementParameterHelper) → new value.</summary>
    public Dictionary<string, object?> Parameters { get; set; } = new();
}

/// <summary>Outcome of writing one parameter on one element.</summary>
public class ParameterWriteResult
{
    [JsonProperty("success")]
    public bool Success { get; set; }

    [JsonProperty("message")]
    public string Message { get; set; } = string.Empty;

    [JsonProperty("elementId")]
    public long ElementId { get; set; }

    /// <summary>The name as asked for — kept so the model can match a miss to its own argument.</summary>
    [JsonProperty("requestedName")]
    public string RequestedName { get; set; } = string.Empty;

    /// <summary>The name Revit actually resolved it to (may differ: «Марка» → Mark).</summary>
    [JsonProperty("parameterName")]
    public string ParameterName { get; set; } = string.Empty;

    [JsonProperty("newDisplayValue")]
    public string NewDisplayValue { get; set; } = string.Empty;
}

public class SetElementsParametersResult
{
    [JsonProperty("success")]
    public bool Success { get; set; }

    [JsonProperty("message")]
    public string Message { get; set; } = string.Empty;

    [JsonProperty("updatedCount")]
    public int UpdatedCount { get; set; }

    [JsonProperty("failedCount")]
    public int FailedCount { get; set; }

    [JsonProperty("results")]
    public List<ParameterWriteResult> Results { get; set; } = new();
}
