using Newtonsoft.Json;

namespace RevitMCPCommandSet.Models.Views;

/// <summary>
/// Parameters for fit_schedule_to_sheet (REV-68).
/// Attempts to make the schedule fit within a target sheet width by applying
/// a cascade of non-destructive strategies: hide optional columns → narrow columns
/// → add a level filter → report the final width.
/// </summary>
public class FitScheduleToSheetInfo
{
    /// <summary>Numeric element id of the schedule to fit.</summary>
    [JsonProperty("scheduleId")]
    public long? ScheduleId { get; set; }

    /// <summary>UniqueId of the schedule to fit.</summary>
    [JsonProperty("scheduleUniqueId")]
    public string ScheduleUniqueId { get; set; } = string.Empty;

    /// <summary>Schedule name to look up when id / uniqueId are not provided.</summary>
    [JsonProperty("scheduleName")]
    public string ScheduleName { get; set; } = string.Empty;

    /// <summary>
    /// Maximum allowed schedule width in mm.
    /// Default 0 = infer from sheet titleblock (A3 ≈ 277 mm working zone; A2 ≈ 400 mm).
    /// </summary>
    [JsonProperty("maxWidthMm")]
    public double MaxWidthMm { get; set; }

    /// <summary>
    /// Sheet element id used to infer maxWidthMm when maxWidthMm = 0.
    /// </summary>
    [JsonProperty("sheetId")]
    public long? SheetId { get; set; }

    // ── Strategy toggles (all default true) ──────────────────────────────────

    /// <summary>Allow hiding optional columns listed in optionalColumns.</summary>
    [JsonProperty("allowHideColumns")]
    public bool AllowHideColumns { get; set; } = true;

    /// <summary>
    /// Columns that may be hidden if the schedule is too wide.
    /// Matched by parameterName or fieldIndex. If empty, any hidden-eligible
    /// column (IsHidden=false, not a key field, not a calculated field) may be hidden.
    /// </summary>
    [JsonProperty("optionalColumns")]
    public List<string> OptionalColumns { get; set; } = new();

    /// <summary>Allow shrinking column widths proportionally to hit target.</summary>
    [JsonProperty("allowNarrowColumns")]
    public bool AllowNarrowColumns { get; set; } = true;

    /// <summary>Minimum column width in mm when narrowing. Default 15 mm.</summary>
    [JsonProperty("minColumnWidthMm")]
    public double MinColumnWidthMm { get; set; } = 15;

    /// <summary>
    /// When true and a "Level" / "Уровень" field exists, add a filter
    /// to show only rows matching levelId / levelName.
    /// </summary>
    [JsonProperty("allowLevelFilter")]
    public bool AllowLevelFilter { get; set; } = true;

    /// <summary>Level element id to filter by. Checked before levelName.</summary>
    [JsonProperty("levelId")]
    public long? LevelId { get; set; }

    /// <summary>Level name to filter by when levelId is not given.</summary>
    [JsonProperty("levelName")]
    public string LevelName { get; set; } = string.Empty;
}

/// <summary>Result returned by fit_schedule_to_sheet.</summary>
public class FitScheduleToSheetResult
{
    [JsonProperty("success")]
    public bool Success { get; set; }

    [JsonProperty("message")]
    public string Message { get; set; } = string.Empty;

    [JsonProperty("scheduleId")]
    public long ScheduleId { get; set; }

    [JsonProperty("scheduleName")]
    public string ScheduleName { get; set; } = string.Empty;

    /// <summary>Total schedule width in mm after all applied strategies.</summary>
    [JsonProperty("finalWidthMm")]
    public double FinalWidthMm { get; set; }

    /// <summary>Target max width that was used.</summary>
    [JsonProperty("targetWidthMm")]
    public double TargetWidthMm { get; set; }

    /// <summary>True when finalWidthMm &lt;= targetWidthMm.</summary>
    [JsonProperty("fits")]
    public bool Fits { get; set; }

    /// <summary>Human-readable list of changes applied.</summary>
    [JsonProperty("appliedStrategies")]
    public List<string> AppliedStrategies { get; set; } = new();

    [JsonProperty("warnings")]
    public List<string> Warnings { get; set; } = new();
}
