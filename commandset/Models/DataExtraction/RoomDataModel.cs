using Newtonsoft.Json;

namespace RevitMCPCommandSet.Models.DataExtraction
{
    /// <summary>
    /// Model for room data extraction
    /// </summary>
    public class RoomDataModel
    {
        [JsonProperty("id")]
        public long Id { get; set; }

        [JsonProperty("uniqueId")]
        public string UniqueId { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("number")]
        public string Number { get; set; }

        [JsonProperty("level")]
        public string Level { get; set; }

        [JsonProperty("area")]
        public double Area { get; set; } // Square meters

        [JsonProperty("volume")]
        public double Volume { get; set; } // Cubic meters

        [JsonProperty("perimeter")]
        public double Perimeter { get; set; } // Millimeters

        [JsonProperty("unboundedHeight")]
        public double UnboundedHeight { get; set; } // Millimeters — Room.UnboundedHeight (often wrong if Limit Offset = 8')

        /// <summary>
        /// Floor-to-floor height to the next level above (mm).
        /// </summary>
        [JsonProperty("storeyHeight")]
        public double StoreyHeight { get; set; }

        /// <summary>
        /// Median thickness of floors hosted on the upper level (mm). 0 if none found.
        /// </summary>
        [JsonProperty("floorThickness")]
        public double FloorThickness { get; set; }

        /// <summary>
        /// Clear height estimate: storeyHeight − floorThickness when storey known;
        /// otherwise UnboundedHeight (mm). Prefer this for norms «от пола до низа потолков».
        /// </summary>
        [JsonProperty("clearHeight")]
        public double ClearHeight { get; set; }

        [JsonProperty("heightSource")]
        public string HeightSource { get; set; }

        [JsonProperty("upperLimitLevel")]
        public string UpperLimitLevel { get; set; }

        [JsonProperty("limitOffset")]
        public double LimitOffset { get; set; } // Millimeters

        [JsonProperty("department")]
        public string Department { get; set; }

        [JsonProperty("comments")]
        public string Comments { get; set; }

        [JsonProperty("phase")]
        public string Phase { get; set; }

        [JsonProperty("occupancy")]
        public string Occupancy { get; set; }
    }

    /// <summary>
    /// Result container for room data export
    /// </summary>
    public class ExportRoomDataResult
    {
        [JsonProperty("totalRooms")]
        public int TotalRooms { get; set; }

        [JsonProperty("totalArea")]
        public double TotalArea { get; set; }

        [JsonProperty("rooms")]
        public List<RoomDataModel> Rooms { get; set; } = new List<RoomDataModel>();

        [JsonProperty("success")]
        public bool Success { get; set; }

        [JsonProperty("message")]
        public string Message { get; set; }

        /// <summary>When filtered: how the scope was applied (e.g. activeView, levelName).</summary>
        [JsonProperty("filteredBy")]
        public string FilteredBy { get; set; }

        /// <summary>Level name used for filtering, if any.</summary>
        [JsonProperty("levelName")]
        public string LevelName { get; set; }

        /// <summary>Placed rooms in project before level/view filter (REV-132).</summary>
        [JsonProperty("totalInProject")]
        public int? TotalInProject { get; set; }
    }
}
