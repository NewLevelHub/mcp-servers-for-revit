using Newtonsoft.Json;

namespace RevitMCPCommandSet.Models.Normatives
{
    public class NormResultElement
    {
        [JsonProperty("elementId")]
        public long ElementId { get; set; }

        /// <summary>Per-element finding, e.g. "глубина 2100 мм &lt; 2400 мм".</summary>
        [JsonProperty("note")]
        public string Note { get; set; } = string.Empty;
    }

    public static class NormResultChangeStatus
    {
        public const string Planned = "planned";
        public const string Applied = "applied";
        public const string Skipped = "skipped";
    }

    public class NormResultChange
    {
        [JsonProperty("elementId")]
        public long ElementId { get; set; }

        [JsonProperty("elementName")]
        public string ElementName { get; set; } = string.Empty;

        [JsonProperty("category")]
        public string Category { get; set; } = string.Empty;

        [JsonProperty("action")]
        public string Action { get; set; } = string.Empty;

        [JsonProperty("parameterName")]
        public string ParameterName { get; set; } = string.Empty;

        [JsonProperty("oldValue")]
        public string OldValue { get; set; } = string.Empty;

        [JsonProperty("newValue")]
        public string NewValue { get; set; } = string.Empty;

        /// <summary>planned (preview), applied, or skipped.</summary>
        [JsonProperty("status")]
        public string Status { get; set; } = NormResultChangeStatus.Planned;

        [JsonProperty("skipReason")]
        public string SkipReason { get; set; } = string.Empty;
    }

    public class CreatedScheduleInfo
    {
        [JsonProperty("id")]
        public long Id { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; } = string.Empty;

        [JsonProperty("category")]
        public string Category { get; set; } = string.Empty;
    }

    public class ApplyNormResultResult
    {
        [JsonProperty("success")]
        public bool Success { get; set; }

        [JsonProperty("message")]
        public string Message { get; set; } = string.Empty;

        [JsonProperty("preview")]
        public bool Preview { get; set; }

        [JsonProperty("actions")]
        public List<string> Actions { get; set; } = new List<string>();

        [JsonProperty("totalElements")]
        public int TotalElements { get; set; }

        [JsonProperty("appliedCount")]
        public int AppliedCount { get; set; }

        [JsonProperty("skippedCount")]
        public int SkippedCount { get; set; }

        [JsonProperty("highlightedCount")]
        public int HighlightedCount { get; set; }

        [JsonProperty("changes")]
        public List<NormResultChange> Changes { get; set; } = new List<NormResultChange>();

        [JsonProperty("schedules")]
        public List<CreatedScheduleInfo> Schedules { get; set; } = new List<CreatedScheduleInfo>();

        [JsonProperty("warnings")]
        public List<string> Warnings { get; set; } = new List<string>();
    }
}
