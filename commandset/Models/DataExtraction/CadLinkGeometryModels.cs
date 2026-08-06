using Newtonsoft.Json;

namespace RevitMCPCommandSet.Models.DataExtraction
{
    public class CadPointMm
    {
        [JsonProperty("x")]
        public double X { get; set; }

        [JsonProperty("y")]
        public double Y { get; set; }

        [JsonProperty("z")]
        public double Z { get; set; }
    }

    public class CadSegmentItem
    {
        [JsonProperty("startMm")]
        public CadPointMm StartMm { get; set; } = new CadPointMm();

        [JsonProperty("endMm")]
        public CadPointMm EndMm { get; set; } = new CadPointMm();

        [JsonProperty("layer")]
        public string Layer { get; set; } = string.Empty;

        [JsonProperty("cadId")]
        public string CadId { get; set; } = string.Empty;

        [JsonProperty("lengthMm")]
        public double LengthMm { get; set; }

        [JsonProperty("curveType")]
        public string CurveType { get; set; } = "line";

        [JsonProperty("cadLinkName")]
        public string CadLinkName { get; set; } = string.Empty;

        [JsonProperty("cadLinkElementId")]
        public long CadLinkElementId { get; set; }

        /// <summary>Origin of the segment: importInstance | modelLine | detailLine.</summary>
        [JsonProperty("source")]
        public string Source { get; set; } = "importInstance";
    }

    public class CadBboxMm
    {
        [JsonProperty("minX")]
        public double MinX { get; set; }

        [JsonProperty("minY")]
        public double MinY { get; set; }

        [JsonProperty("maxX")]
        public double MaxX { get; set; }

        [JsonProperty("maxY")]
        public double MaxY { get; set; }
    }

    public class CadLinkInfo
    {
        [JsonProperty("elementId")]
        public long ElementId { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; } = string.Empty;

        [JsonProperty("isLinked")]
        public bool IsLinked { get; set; }

        [JsonProperty("segmentCount")]
        public int SegmentCount { get; set; }
    }

    public class CadLayerSummaryItem
    {
        [JsonProperty("layer")]
        public string Layer { get; set; } = string.Empty;

        [JsonProperty("count")]
        public int Count { get; set; }
    }

    /// <summary>
    /// REV-138: Cursor-facing contract { ok, summary, items[] }.
    /// </summary>
    public class GetCadLinkGeometryResult
    {
        [JsonProperty("ok")]
        public bool Ok { get; set; }

        [JsonProperty("summary")]
        public string Summary { get; set; } = string.Empty;

        [JsonProperty("count")]
        public int Count { get; set; }

        [JsonProperty("items")]
        public List<CadSegmentItem> Items { get; set; } = new List<CadSegmentItem>();

        [JsonProperty("bboxMm")]
        public CadBboxMm BboxMm { get; set; }

        [JsonProperty("cadLinkName")]
        public string CadLinkName { get; set; }

        [JsonProperty("cadLinkElementId")]
        public long? CadLinkElementId { get; set; }

        [JsonProperty("importUnits")]
        public string ImportUnits { get; set; } = "mm";

        [JsonProperty("viewId")]
        public long? ViewId { get; set; }

        [JsonProperty("viewName")]
        public string ViewName { get; set; }

        [JsonProperty("availableLinks")]
        public List<CadLinkInfo> AvailableLinks { get; set; }

        [JsonProperty("truncated")]
        public bool Truncated { get; set; }

        [JsonProperty("message")]
        public string Message { get; set; }

        [JsonProperty("layerSummary")]
        public List<CadLayerSummaryItem> LayerSummary { get; set; }
    }
}
