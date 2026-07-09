using Newtonsoft.Json;

namespace RevitMCPCommandSet.Models.Views;

/// <summary>
///     Full read-only structure of an existing ViewSchedule definition.
/// </summary>
public class ScheduleDefinitionResult
{
    [JsonProperty("success")]
    public bool Success { get; set; }

    [JsonProperty("message")]
    public string Message { get; set; } = string.Empty;

    [JsonProperty("scheduleId")]
    public long ScheduleId { get; set; }

    [JsonProperty("scheduleUniqueId")]
    public string ScheduleUniqueId { get; set; } = string.Empty;

    [JsonProperty("scheduleName")]
    public string ScheduleName { get; set; } = string.Empty;

    [JsonProperty("isTemplate")]
    public bool IsTemplate { get; set; }

    [JsonProperty("categoryId")]
    public int CategoryId { get; set; }

    [JsonProperty("categoryName")]
    public string CategoryName { get; set; } = string.Empty;

    [JsonProperty("type")]
    public string Type { get; set; } = "Regular";

    [JsonProperty("showTitle")]
    public bool ShowTitle { get; set; }

    [JsonProperty("showHeaders")]
    public bool ShowHeaders { get; set; }

    [JsonProperty("showGridLines")]
    public bool ShowGridLines { get; set; }

    [JsonProperty("fields")]
    public List<ScheduleDefinitionFieldInfo> Fields { get; set; } = new List<ScheduleDefinitionFieldInfo>();

    [JsonProperty("filters")]
    public List<ScheduleFilterInfo> Filters { get; set; } = new List<ScheduleFilterInfo>();

    [JsonProperty("sortFields")]
    public List<ScheduleSortInfo> SortFields { get; set; } = new List<ScheduleSortInfo>();

    [JsonProperty("groupFields")]
    public List<ScheduleGroupInfo> GroupFields { get; set; } = new List<ScheduleGroupInfo>();
}

/// <summary>
///     Schedule field with its column index in the definition.
/// </summary>
public class ScheduleDefinitionFieldInfo : ScheduleFieldInfo
{
    [JsonProperty("fieldIndex")]
    public int FieldIndex { get; set; }
}
