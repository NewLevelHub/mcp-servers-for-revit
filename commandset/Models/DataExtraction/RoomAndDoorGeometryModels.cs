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

    /// <summary>
    /// Window / door opening metrics for sill height and opening height audits (REV-58).
    /// </summary>
    public class OpeningGeometryInfo
    {
        [JsonProperty("id")]
        public long Id { get; set; }

        [JsonProperty("uniqueId")]
        public string UniqueId { get; set; } = string.Empty;

        /// <summary>window | door</summary>
        [JsonProperty("category")]
        public string Category { get; set; } = string.Empty;

        [JsonProperty("family")]
        public string Family { get; set; } = string.Empty;

        [JsonProperty("type")]
        public string Type { get; set; } = string.Empty;

        [JsonProperty("level")]
        public string Level { get; set; } = string.Empty;

        [JsonProperty("hostWallId")]
        public long? HostWallId { get; set; }

        /// <summary>INSTANCE_SILL_HEIGHT_PARAM for windows (mm from level).</summary>
        [JsonProperty("sillHeightMm")]
        public double? SillHeightMm { get; set; }

        /// <summary>WINDOW_HEIGHT / DOOR_HEIGHT (mm).</summary>
        [JsonProperty("openingHeightMm")]
        public double? OpeningHeightMm { get; set; }

        [JsonProperty("isOnEgressPath")]
        public bool IsOnEgressPath { get; set; }

        [JsonProperty("location")]
        public string Location { get; set; } = string.Empty;
    }

    public class GetOpeningGeometryInfoResult
    {
        [JsonProperty("success")]
        public bool Success { get; set; }

        [JsonProperty("message")]
        public string Message { get; set; } = string.Empty;

        [JsonProperty("totalOpenings")]
        public int TotalOpenings { get; set; }

        [JsonProperty("totalWindows")]
        public int TotalWindows { get; set; }

        [JsonProperty("totalDoors")]
        public int TotalDoors { get; set; }

        [JsonProperty("openings")]
        public List<OpeningGeometryInfo> Openings { get; set; } = new List<OpeningGeometryInfo>();
    }

    /// <summary>Stair metrics for REV-59 (stair_width / stair_riser_tread).</summary>
    public class StairGeometryInfo
    {
        [JsonProperty("id")]
        public long Id { get; set; }

        [JsonProperty("uniqueId")]
        public string UniqueId { get; set; } = string.Empty;

        [JsonProperty("name")]
        public string Name { get; set; } = string.Empty;

        [JsonProperty("type")]
        public string Type { get; set; } = string.Empty;

        [JsonProperty("level")]
        public string Level { get; set; } = string.Empty;

        [JsonProperty("widthMm")]
        public double? WidthMm { get; set; }

        [JsonProperty("riserMm")]
        public double? RiserMm { get; set; }

        [JsonProperty("treadMm")]
        public double? TreadMm { get; set; }
    }

    /// <summary>Ramp metrics for REV-59 (ramp_slope_width).</summary>
    public class RampGeometryInfo
    {
        [JsonProperty("id")]
        public long Id { get; set; }

        [JsonProperty("uniqueId")]
        public string UniqueId { get; set; } = string.Empty;

        [JsonProperty("name")]
        public string Name { get; set; } = string.Empty;

        [JsonProperty("type")]
        public string Type { get; set; } = string.Empty;

        [JsonProperty("level")]
        public string Level { get; set; } = string.Empty;

        [JsonProperty("widthMm")]
        public double? WidthMm { get; set; }

        /// <summary>Longitudinal slope in percent (e.g. 5.0 = 5%).</summary>
        [JsonProperty("slopePercent")]
        public double? SlopePercent { get; set; }
    }

    /// <summary>Railing metrics for REV-59 (railing_height).</summary>
    public class RailingGeometryInfo
    {
        [JsonProperty("id")]
        public long Id { get; set; }

        [JsonProperty("uniqueId")]
        public string UniqueId { get; set; } = string.Empty;

        [JsonProperty("name")]
        public string Name { get; set; } = string.Empty;

        [JsonProperty("type")]
        public string Type { get; set; } = string.Empty;

        [JsonProperty("level")]
        public string Level { get; set; } = string.Empty;

        [JsonProperty("heightMm")]
        public double? HeightMm { get; set; }

        [JsonProperty("hostElementId")]
        public long? HostElementId { get; set; }
    }

    public class GetVerticalCirculationInfoResult
    {
        [JsonProperty("success")]
        public bool Success { get; set; }

        [JsonProperty("message")]
        public string Message { get; set; } = string.Empty;

        [JsonProperty("totalStairs")]
        public int TotalStairs { get; set; }

        [JsonProperty("totalRamps")]
        public int TotalRamps { get; set; }

        [JsonProperty("totalRailings")]
        public int TotalRailings { get; set; }

        [JsonProperty("stairs")]
        public List<StairGeometryInfo> Stairs { get; set; } = new List<StairGeometryInfo>();

        [JsonProperty("ramps")]
        public List<RampGeometryInfo> Ramps { get; set; } = new List<RampGeometryInfo>();

        [JsonProperty("railings")]
        public List<RailingGeometryInfo> Railings { get; set; } = new List<RailingGeometryInfo>();
    }
}
