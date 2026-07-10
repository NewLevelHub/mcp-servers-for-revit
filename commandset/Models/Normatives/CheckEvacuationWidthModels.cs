using Newtonsoft.Json;

namespace RevitMCPCommandSet.Models.Normatives
{
    public class EvacuationWidthCheckItem
    {
        [JsonProperty("id")]
        public long Id { get; set; }

        [JsonProperty("uniqueId")]
        public string UniqueId { get; set; } = string.Empty;

        [JsonProperty("name")]
        public string Name { get; set; } = string.Empty;

        [JsonProperty("number")]
        public string Number { get; set; } = string.Empty;

        [JsonProperty("level")]
        public string Level { get; set; } = string.Empty;

        [JsonProperty("roomPurpose")]
        public string RoomPurpose { get; set; } = string.Empty;

        [JsonProperty("actualWidthMm")]
        public double ActualWidthMm { get; set; }

        [JsonProperty("depthMm")]
        public double DepthMm { get; set; }

        [JsonProperty("areaM2")]
        public double AreaM2 { get; set; }

        [JsonProperty("requiredWidthMm")]
        public double RequiredWidthMm { get; set; }

        [JsonProperty("isCompliant")]
        public bool IsCompliant { get; set; }

        /// <summary>How much the width is below the required minimum, in mm (0 for compliant rooms).</summary>
        [JsonProperty("deviationMm")]
        public double DeviationMm { get; set; }
    }

    public class CheckEvacuationWidthResult
    {
        [JsonProperty("success")]
        public bool Success { get; set; }

        [JsonProperty("message")]
        public string Message { get; set; } = string.Empty;

        [JsonProperty("mode")]
        public string Mode { get; set; } = "report";

        [JsonProperty("minWidthMm")]
        public double? MinWidthMm { get; set; }

        [JsonProperty("corridorOnly")]
        public bool CorridorOnly { get; set; }

        [JsonProperty("totalCorridorsChecked")]
        public int TotalCorridorsChecked { get; set; }

        [JsonProperty("violationCount")]
        public int ViolationCount { get; set; }

        [JsonProperty("violations")]
        public List<EvacuationWidthCheckItem> Violations { get; set; } = new List<EvacuationWidthCheckItem>();

        [JsonProperty("compliantCorridors")]
        public List<EvacuationWidthCheckItem> CompliantCorridors { get; set; } = new List<EvacuationWidthCheckItem>();

        [JsonProperty("highlightedCount")]
        public int HighlightedCount { get; set; }
    }
}
