using Newtonsoft.Json;

namespace RevitMCPCommandSet.Models.Common;

public class LoadFamilyRequestInfo
{
    /// <summary>Full paths to .rfa files on the machine running Revit.</summary>
    [JsonProperty("paths")]
    public List<string> Paths { get; set; } = new List<string>();

    /// <summary>Folder to take families from; combined with names, or all .rfa in it when names is empty.</summary>
    [JsonProperty("directory")]
    public string Directory { get; set; } = string.Empty;

    /// <summary>Family file names inside directory, with or without the .rfa extension.</summary>
    [JsonProperty("names")]
    public List<string> Names { get; set; } = new List<string>();

    /// <summary>Overwrite parameter values of a family already in the project. Default false.</summary>
    [JsonProperty("overwriteParameterValues")]
    public bool OverwriteParameterValues { get; set; }

    /// <summary>Activate every loaded type so it can be placed straight away. Default true.</summary>
    [JsonProperty("activateSymbols")]
    public bool ActivateSymbols { get; set; } = true;
}

public class LoadedFamilyTypeInfo
{
    [JsonProperty("typeId")]
    public long TypeId { get; set; }

    [JsonProperty("typeName")]
    public string TypeName { get; set; } = string.Empty;
}

public class LoadedFamilyInfo
{
    [JsonProperty("loaded")]
    public bool Loaded { get; set; }

    [JsonProperty("path")]
    public string Path { get; set; } = string.Empty;

    [JsonProperty("familyId")]
    public long FamilyId { get; set; }

    [JsonProperty("familyName")]
    public string FamilyName { get; set; } = string.Empty;

    [JsonProperty("category")]
    public string Category { get; set; } = string.Empty;

    [JsonProperty("types")]
    public List<LoadedFamilyTypeInfo> Types { get; set; } = new List<LoadedFamilyTypeInfo>();

    [JsonProperty("error")]
    public string Error { get; set; } = string.Empty;
}

public class LoadFamilyResult
{
    [JsonProperty("success")]
    public bool Success { get; set; }

    [JsonProperty("message")]
    public string Message { get; set; } = string.Empty;

    [JsonProperty("loadedCount")]
    public int LoadedCount { get; set; }

    [JsonProperty("requestedCount")]
    public int RequestedCount { get; set; }

    [JsonProperty("families")]
    public List<LoadedFamilyInfo> Families { get; set; } = new List<LoadedFamilyInfo>();

    [JsonProperty("warnings")]
    public List<string> Warnings { get; set; } = new List<string>();
}
