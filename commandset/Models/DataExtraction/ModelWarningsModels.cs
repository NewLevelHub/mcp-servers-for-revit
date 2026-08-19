using Newtonsoft.Json;

namespace RevitMCPCommandSet.Models.DataExtraction
{
    /// <summary>
    /// One kind of Revit warning, with every occurrence folded into it — the way
    /// Revit's own «Просмотр предупреждений» dialog groups them.
    /// </summary>
    public class ModelWarningGroup
    {
        /// <summary>Warning text as Revit words it, e.g. «Выделенные стены перекрываются».</summary>
        [JsonProperty("description")]
        public string Description { get; set; } = string.Empty;

        /// <summary>Warning / Error / DocumentCorruption, from FailureSeverity.</summary>
        [JsonProperty("severity")]
        public string Severity { get; set; } = string.Empty;

        /// <summary>How many times this warning occurs.</summary>
        [JsonProperty("count")]
        public int Count { get; set; }

        /// <summary>Distinct elements involved across all occurrences.</summary>
        [JsonProperty("elementCount")]
        public int ElementCount { get; set; }

        /// <summary>Revit categories of those elements, most common first.</summary>
        [JsonProperty("categories")]
        public List<string> Categories { get; set; } = new();

        /// <summary>
        /// A sample of the offending elements — capped by maxElementIdsPerGroup,
        /// because a single warning kind can name thousands.
        /// </summary>
        [JsonProperty("elementIds")]
        public List<long> ElementIds { get; set; } = new();

        /// <summary>Set when <see cref="ElementIds"/> is a sample, not the full list.</summary>
        [JsonProperty("elementIdsTruncated", NullValueHandling = NullValueHandling.Ignore)]
        public bool? ElementIdsTruncated { get; set; }
    }

    public class GetModelWarningsResult
    {
        [JsonProperty("success")]
        public bool Success { get; set; }

        [JsonProperty("message")]
        public string Message { get; set; } = string.Empty;

        /// <summary>Total occurrences, i.e. what the Revit warnings dialog counts.</summary>
        [JsonProperty("totalWarnings")]
        public int TotalWarnings { get; set; }

        /// <summary>Distinct warning kinds.</summary>
        [JsonProperty("totalGroups")]
        public int TotalGroups { get; set; }

        /// <summary>Occurrences that are errors rather than warnings.</summary>
        [JsonProperty("errorCount")]
        public int ErrorCount { get; set; }

        /// <summary>Groups, largest first — the ones worth fixing lead.</summary>
        [JsonProperty("groups")]
        public List<ModelWarningGroup> Groups { get; set; } = new();
    }
}
