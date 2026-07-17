using Newtonsoft.Json;

namespace RevitMCPCommandSet.Models.Normatives
{
    public class RoomDepthCheckItem
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

        [JsonProperty("depthMm")]
        public double DepthMm { get; set; }

        [JsonProperty("widthMm")]
        public double WidthMm { get; set; }

        [JsonProperty("areaM2")]
        public double AreaM2 { get; set; }

        [JsonProperty("isCompliant")]
        public bool IsCompliant { get; set; }

        /// <summary>How far the depth is outside the allowed range, in mm (0 for compliant rooms).</summary>
        [JsonProperty("deviationMm")]
        public double DeviationMm { get; set; }
    }

    public class CheckRoomDepthResult
    {
        [JsonProperty("success")]
        public bool Success { get; set; }

        [JsonProperty("message")]
        public string Message { get; set; } = string.Empty;

        [JsonProperty("mode")]
        public string Mode { get; set; } = "report";

        [JsonProperty("minDepthMm")]
        public double? MinDepthMm { get; set; }

        [JsonProperty("maxDepthMm")]
        public double? MaxDepthMm { get; set; }

        /// <summary>"living" (default) or "all" — which rooms were in scope.</summary>
        [JsonProperty("roomScope")]
        public string RoomScope { get; set; } = "living";

        [JsonProperty("totalRoomsChecked")]
        public int TotalRoomsChecked { get; set; }

        [JsonProperty("violationCount")]
        public int ViolationCount { get; set; }

        [JsonProperty("violations")]
        public List<RoomDepthCheckItem> Violations { get; set; } = new List<RoomDepthCheckItem>();

        [JsonProperty("compliantRooms")]
        public List<RoomDepthCheckItem> CompliantRooms { get; set; } = new List<RoomDepthCheckItem>();

        [JsonProperty("highlightedCount")]
        public int HighlightedCount { get; set; }
    }
}
