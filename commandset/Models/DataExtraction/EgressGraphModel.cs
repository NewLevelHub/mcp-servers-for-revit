using Newtonsoft.Json;

namespace RevitMCPCommandSet.Models.DataExtraction
{
    /// <summary>2D point in millimetres (plan coordinates).</summary>
    public class EgressPoint
    {
        [JsonProperty("x")]
        public double X { get; set; }

        [JsonProperty("y")]
        public double Y { get; set; }
    }

    /// <summary>
    /// Room node of the egress walkability graph: outer boundary polygon in mm
    /// so the server can trace paths along real geometry, not straight lines.
    /// </summary>
    public class EgressRoomExport
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

        [JsonProperty("centroid")]
        public EgressPoint Centroid { get; set; } = new EgressPoint();

        /// <summary>Outer boundary loop vertices, mm. Inner loops (columns) omitted in v1.</summary>
        [JsonProperty("boundary")]
        public List<EgressPoint> Boundary { get; set; } = new List<EgressPoint>();
    }

    /// <summary>Door edge of the egress graph: connects up to two rooms at a point.</summary>
    public class EgressDoorExport
    {
        [JsonProperty("id")]
        public long Id { get; set; }

        [JsonProperty("uniqueId")]
        public string UniqueId { get; set; } = string.Empty;

        [JsonProperty("name")]
        public string Name { get; set; } = string.Empty;

        [JsonProperty("level")]
        public string Level { get; set; } = string.Empty;

        [JsonProperty("x")]
        public double X { get; set; }

        [JsonProperty("y")]
        public double Y { get; set; }

        /// <summary>Room element id on the from side; null = outside / unplaced.</summary>
        [JsonProperty("fromRoomId")]
        public long? FromRoomId { get; set; }

        [JsonProperty("toRoomId")]
        public long? ToRoomId { get; set; }

        [JsonProperty("widthMm")]
        public double? WidthMm { get; set; }

        /// <summary>True when the host wall is an exterior wall (candidate exit to outside).</summary>
        [JsonProperty("isExteriorWall")]
        public bool IsExteriorWall { get; set; }
    }

    public class ExportEgressGraphResult
    {
        [JsonProperty("success")]
        public bool Success { get; set; }

        [JsonProperty("message")]
        public string Message { get; set; } = string.Empty;

        [JsonProperty("levelName")]
        public string LevelName { get; set; } = string.Empty;

        [JsonProperty("rooms")]
        public List<EgressRoomExport> Rooms { get; set; } = new List<EgressRoomExport>();

        [JsonProperty("doors")]
        public List<EgressDoorExport> Doors { get; set; } = new List<EgressDoorExport>();

        [JsonProperty("warnings")]
        public List<string> Warnings { get; set; } = new List<string>();

        [JsonProperty("executionTimeMs")]
        public long ExecutionTimeMs { get; set; }
    }
}
