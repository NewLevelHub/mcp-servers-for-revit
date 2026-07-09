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

        [JsonProperty("isOnEgressPath")]
        public bool IsOnEgressPath { get; set; }

        [JsonProperty("location")]
        public string Location { get; set; } = string.Empty;
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
    }
}
