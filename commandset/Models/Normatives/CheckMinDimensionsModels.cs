using Newtonsoft.Json;

namespace RevitMCPCommandSet.Models.Normatives
{
    public class MinDimensionCheckItem
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

        [JsonProperty("spaceKind")]
        public string SpaceKind { get; set; } = string.Empty;

        [JsonProperty("checkType")]
        public string CheckType { get; set; } = string.Empty;

        [JsonProperty("metric")]
        public string Metric { get; set; } = string.Empty;

        [JsonProperty("actualValueMm")]
        public double ActualValueMm { get; set; }

        [JsonProperty("requiredValueMm")]
        public double RequiredValueMm { get; set; }

        [JsonProperty("isCompliant")]
        public bool IsCompliant { get; set; }

        [JsonProperty("deviationMm")]
        public double DeviationMm { get; set; }

        [JsonProperty("widthMm")]
        public double? WidthMm { get; set; }

        [JsonProperty("depthMm")]
        public double? DepthMm { get; set; }

        [JsonProperty("areaM2")]
        public double? AreaM2 { get; set; }

        [JsonProperty("wallId")]
        public long? WallId { get; set; }

        [JsonProperty("pierKind")]
        public string PierKind { get; set; } = string.Empty;

        [JsonProperty("adjacentOpeningIds")]
        public List<long> AdjacentOpeningIds { get; set; } = new();
    }

    public class CheckMinDimensionsResult
    {
        [JsonProperty("success")]
        public bool Success { get; set; }

        [JsonProperty("message")]
        public string Message { get; set; } = string.Empty;

        [JsonProperty("mode")]
        public string Mode { get; set; } = "report";

        [JsonProperty("minBalconyWidthMm")]
        public double? MinBalconyWidthMm { get; set; }

        [JsonProperty("minLoggiaWidthMm")]
        public double? MinLoggiaWidthMm { get; set; }

        [JsonProperty("minLoggiaDepthMm")]
        public double? MinLoggiaDepthMm { get; set; }

        /// <summary>п. 4.2.30 — воздушная зона / путь к Н1 (не квартирная лоджия).</summary>
        [JsonProperty("minFirePathOutdoorWidthMm")]
        public double? MinFirePathOutdoorWidthMm { get; set; }

        [JsonProperty("minFirePierToOpeningMm")]
        public double? MinFirePierToOpeningMm { get; set; }

        [JsonProperty("minFirePierBetweenOpeningsMm")]
        public double? MinFirePierBetweenOpeningsMm { get; set; }

        [JsonProperty("totalSpacesChecked")]
        public int TotalSpacesChecked { get; set; }

        [JsonProperty("violationCount")]
        public int ViolationCount { get; set; }

        [JsonProperty("violations")]
        public List<MinDimensionCheckItem> Violations { get; set; } = new();

        [JsonProperty("compliantItems")]
        public List<MinDimensionCheckItem> CompliantItems { get; set; } = new();

        [JsonProperty("highlightedCount")]
        public int HighlightedCount { get; set; }
    }
}
