using Newtonsoft.Json;

namespace RevitMCPCommandSet.Models.Normatives
{
    /// <summary>
    /// Raw door facts collected from the Revit model.
    /// Normative matching is performed on the MCP server from PDFs in repo/normatives.
    /// </summary>
    public class DoorFireFacts
    {
        [JsonProperty("id")]
        public long Id { get; set; }

        [JsonProperty("uniqueId")]
        public string UniqueId { get; set; } = string.Empty;

        [JsonProperty("mark")]
        public string Mark { get; set; } = string.Empty;

        [JsonProperty("family")]
        public string Family { get; set; } = string.Empty;

        [JsonProperty("type")]
        public string Type { get; set; } = string.Empty;

        [JsonProperty("level")]
        public string Level { get; set; } = string.Empty;

        [JsonProperty("fromRoom")]
        public string FromRoom { get; set; } = string.Empty;

        [JsonProperty("toRoom")]
        public string ToRoom { get; set; } = string.Empty;

        [JsonProperty("openingWidthMm")]
        public double? OpeningWidthMm { get; set; }

        [JsonProperty("isOnEgressPath")]
        public bool IsOnEgressPath { get; set; }

        [JsonProperty("isMarkedAsFireDoor")]
        public bool IsMarkedAsFireDoor { get; set; }

        /// <summary>
        /// Where the fire-door mark was found: none | parameter | schedule_note | both.
        /// </summary>
        [JsonProperty("markSource")]
        public string MarkSource { get; set; } = "none";

        [JsonProperty("currentFireRating")]
        public string CurrentFireRating { get; set; } = string.Empty;

        /// <summary>
        /// Note text from door schedule «Примечание» (or type note params that feed it).
        /// </summary>
        [JsonProperty("scheduleNote")]
        public string ScheduleNote { get; set; } = string.Empty;
    }

    public class CheckFireDoorsResult
    {
        [JsonProperty("success")]
        public bool Success { get; set; }

        [JsonProperty("message")]
        public string Message { get; set; } = string.Empty;

        [JsonProperty("totalDoors")]
        public int TotalDoors { get; set; }

        [JsonProperty("doors")]
        public List<DoorFireFacts> Doors { get; set; } = new();

        [JsonProperty("warnings")]
        public List<string> Warnings { get; set; } = new();

        /// <summary>
        /// Where the time went, in ms. Present so the next slow report can be
        /// diagnosed from the answer itself rather than by rebuilding the plugin
        /// with a stopwatch in it (REV-53).
        /// </summary>
        [JsonProperty("timings", NullValueHandling = NullValueHandling.Ignore)]
        public CheckFireDoorsTimings Timings { get; set; }
    }

    public class CheckFireDoorsTimings
    {
        [JsonProperty("totalMs")]
        public long TotalMs { get; set; }

        /// <summary>door.FromRoom / door.ToRoom — Revit recomputes room boundaries here.</summary>
        [JsonProperty("roomLookupMs")]
        public long RoomLookupMs { get; set; }

        /// <summary>Schedule-note matching and type comments, both cached by door type.</summary>
        [JsonProperty("scheduleNoteMs")]
        public long ScheduleNoteMs { get; set; }

        /// <summary>Fire-rating parameter reads on the instance and its type.</summary>
        [JsonProperty("parameterMs")]
        public long ParameterMs { get; set; }

        /// <summary>Distinct door types behind the doors checked — the cache hit rate.</summary>
        [JsonProperty("doorTypes")]
        public int DoorTypes { get; set; }
    }
}
