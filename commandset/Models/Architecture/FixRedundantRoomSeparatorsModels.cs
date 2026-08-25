using Newtonsoft.Json;

namespace RevitMCPCommandSet.Models.Architecture
{
    /// <summary>
    /// REV-180: the one warning class this repo auto-fixes so far — a room-separation line made
    /// redundant by a wall already sitting on top of it (FailureDefinitionId
    /// f7b3a015-c3eb-4a3f-b345-c474ec07d43f, "Стена и линия-разделитель помещений перекрываются").
    /// Preview by default; only <see cref="Confirm"/>=true writes to the model, and only the
    /// separation lines are ever touched — never a wall.
    /// </summary>
    public class FixRedundantRoomSeparatorsResult
    {
        [JsonProperty("success")]
        public bool Success { get; set; }

        [JsonProperty("message")]
        public string Message { get; set; } = string.Empty;

        /// <summary>True when this run actually deleted elements; false for a preview-only pass.</summary>
        [JsonProperty("applied")]
        public bool Applied { get; set; }

        /// <summary>Distinct redundant separation-line elements found (or, if applied, removed).</summary>
        [JsonProperty("count")]
        public int Count { get; set; }

        [JsonProperty("elementIds")]
        public List<long> ElementIds { get; set; } = new();
    }
}
