using Newtonsoft.Json;

namespace RevitMCPCommandSet.Models.Views;

/// <summary>
/// Parameters for editing an existing ViewSchedule in-place (REV-68).
/// All collections are applied incrementally unless the corresponding clear* flag is set.
/// </summary>
public class ConfigureScheduleInfo
{
    /// <summary>Numeric element id of the schedule to edit.</summary>
    [JsonProperty("scheduleId")]
    public long? ScheduleId { get; set; }

    /// <summary>UniqueId of the schedule to edit.</summary>
    [JsonProperty("scheduleUniqueId")]
    public string ScheduleUniqueId { get; set; } = string.Empty;

    /// <summary>Schedule name to look up when id / uniqueId are not provided.</summary>
    [JsonProperty("scheduleName")]
    public string ScheduleName { get; set; } = string.Empty;

    // ── Display options ──────────────────────────────────────────────────────

    [JsonProperty("showTitle")]
    public bool? ShowTitle { get; set; }

    [JsonProperty("showHeaders")]
    public bool? ShowHeaders { get; set; }

    [JsonProperty("showGridLines")]
    public bool? ShowGridLines { get; set; }

    [JsonProperty("isItemized")]
    public bool? IsItemized { get; set; }

    // ── Field mutations ───────────────────────────────────────────────────────

    /// <summary>
    /// Column-width overrides applied to existing fields (matched by fieldIndex or parameterName).
    /// Does not add new columns.
    /// </summary>
    [JsonProperty("fieldWidths")]
    public List<ScheduleFieldWidthInfo> FieldWidths { get; set; } = new();

    /// <summary>
    /// Fields to hide (isHidden = true) matched by fieldIndex or parameterName.
    /// </summary>
    [JsonProperty("hideFields")]
    public List<string> HideFields { get; set; } = new();

    /// <summary>
    /// Fields to show (isHidden = false) matched by fieldIndex or parameterName.
    /// </summary>
    [JsonProperty("showFields")]
    public List<string> ShowFields { get; set; } = new();

    // ── Filter mutations ──────────────────────────────────────────────────────

    /// <summary>When true, all existing filters are removed before applying <see cref="Filters"/>.</summary>
    [JsonProperty("clearExistingFilters")]
    public bool ClearExistingFilters { get; set; }

    [JsonProperty("filters")]
    public List<ScheduleFilterInfo> Filters { get; set; } = new();

    // ── Sort / group mutations ────────────────────────────────────────────────

    [JsonProperty("clearExistingSorts")]
    public bool ClearExistingSorts { get; set; }

    [JsonProperty("sortFields")]
    public List<ScheduleSortInfo> SortFields { get; set; } = new();

    [JsonProperty("clearExistingGroups")]
    public bool ClearExistingGroups { get; set; }

    [JsonProperty("groupFields")]
    public List<ScheduleGroupInfo> GroupFields { get; set; } = new();
}

/// <summary>Column-width override for a single field.</summary>
public class ScheduleFieldWidthInfo
{
    /// <summary>0-based column index (-1 = match by name).</summary>
    [JsonProperty("fieldIndex")]
    public int FieldIndex { get; set; } = -1;

    /// <summary>Parameter name to match when fieldIndex is -1.</summary>
    [JsonProperty("parameterName")]
    public string ParameterName { get; set; } = string.Empty;

    /// <summary>New column width in mm.</summary>
    [JsonProperty("widthMm")]
    public double WidthMm { get; set; }
}

/// <summary>Result returned by configure_schedule.</summary>
public class ConfigureScheduleResult
{
    [JsonProperty("success")]
    public bool Success { get; set; }

    [JsonProperty("message")]
    public string Message { get; set; } = string.Empty;

    [JsonProperty("scheduleId")]
    public long ScheduleId { get; set; }

    [JsonProperty("scheduleName")]
    public string ScheduleName { get; set; } = string.Empty;

    [JsonProperty("fieldCount")]
    public int FieldCount { get; set; }

    [JsonProperty("filterCount")]
    public int FilterCount { get; set; }

    [JsonProperty("warnings")]
    public List<string> Warnings { get; set; } = new();
}
