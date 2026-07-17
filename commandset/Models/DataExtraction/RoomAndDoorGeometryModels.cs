using Newtonsoft.Json;

namespace RevitMCPCommandSet.Models.DataExtraction
{
    public class RoomGeometryMetric
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

        [JsonProperty("areaM2")]
        public double AreaM2 { get; set; }

        [JsonProperty("widthMm")]
        public double WidthMm { get; set; }

        [JsonProperty("depthMm")]
        public double DepthMm { get; set; }

        [JsonProperty("corridorWidthMm")]
        public double? CorridorWidthMm { get; set; }
    }

    public class GetRoomGeometryMetricsResult
    {
        [JsonProperty("success")]
        public bool Success { get; set; }

        [JsonProperty("message")]
        public string Message { get; set; } = string.Empty;

        [JsonProperty("totalRooms")]
        public int TotalRooms { get; set; }

        [JsonProperty("rooms")]
        public List<RoomGeometryMetric> Rooms { get; set; } = new List<RoomGeometryMetric>();
    }

    public class DoorEgressInfo
    {
        [JsonProperty("id")]
        public long Id { get; set; }

        [JsonProperty("uniqueId")]
        public string UniqueId { get; set; } = string.Empty;

        [JsonProperty("family")]
        public string Family { get; set; } = string.Empty;

        [JsonProperty("type")]
        public string Type { get; set; } = string.Empty;

        [JsonProperty("level")]
        public string Level { get; set; } = string.Empty;

        [JsonProperty("hostWallId")]
        public long? HostWallId { get; set; }

        [JsonProperty("openingWidthMm")]
        public double? OpeningWidthMm { get; set; }

        [JsonProperty("clearWidthMm")]
        public double? ClearWidthMm { get; set; }

        [JsonProperty("widthSource")]
        public string WidthSource { get; set; } = string.Empty;

        [JsonProperty("isOnEgressPath")]
        public bool IsOnEgressPath { get; set; }

        [JsonProperty("maneuveringDepthMm")]
        public double? ManeuveringDepthMm { get; set; }

        [JsonProperty("maneuveringWidthMm")]
        public double? ManeuveringWidthMm { get; set; }

        [JsonProperty("maneuveringRoom")]
        public string ManeuveringRoom { get; set; } = string.Empty;

        [JsonProperty("maneuveringRequiredDepthMm")]
        public double? ManeuveringRequiredDepthMm { get; set; }

        [JsonProperty("maneuveringApproach")]
        public string ManeuveringApproach { get; set; } = string.Empty;

        [JsonProperty("location")]
        public string Location { get; set; } = string.Empty;
    }

    public class RampAccessibilityInfo
    {
        [JsonProperty("id")]
        public long Id { get; set; }

        [JsonProperty("uniqueId")]
        public string UniqueId { get; set; } = string.Empty;

        [JsonProperty("name")]
        public string Name { get; set; } = string.Empty;

        [JsonProperty("level")]
        public string Level { get; set; } = string.Empty;

        [JsonProperty("slopePercent")]
        public double? SlopePercent { get; set; }

        [JsonProperty("slopeSource")]
        public string SlopeSource { get; set; } = string.Empty;

        [JsonProperty("riseMm")]
        public double? RiseMm { get; set; }

        [JsonProperty("runMm")]
        public double? RunMm { get; set; }

        [JsonProperty("isExceptionAllowed")]
        public bool IsExceptionAllowed { get; set; }
    }

    public class GetDoorEgressInfoResult
    {
        [JsonProperty("success")]
        public bool Success { get; set; }

        [JsonProperty("message")]
        public string Message { get; set; } = string.Empty;

        [JsonProperty("totalDoors")]
        public int TotalDoors { get; set; }

        [JsonProperty("doors")]
        public List<DoorEgressInfo> Doors { get; set; } = new List<DoorEgressInfo>();

        [JsonProperty("ramps")]
        public List<RampAccessibilityInfo> Ramps { get; set; } = new List<RampAccessibilityInfo>();
    }
}
