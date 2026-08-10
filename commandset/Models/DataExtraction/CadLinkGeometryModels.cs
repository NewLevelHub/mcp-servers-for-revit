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

        /// <summary>
        /// REV-149: index into <see cref="GetCadLinkGeometryResult.Blocks"/> when this segment
        /// came from a DWG block instance; -1 for loose geometry.
        /// </summary>
        [JsonProperty("blockIndex")]
        public int BlockIndex { get; set; } = -1;

        /// <summary>
        /// REV-149: shared id across every chord tessellated from one arc, so the client can
        /// rebuild the original arc instead of guessing swing side from chords.
        /// </summary>
        [JsonProperty("arcId", NullValueHandling = NullValueHandling.Ignore)]
        public string ArcId { get; set; }

        [JsonProperty("arcCenterMm", NullValueHandling = NullValueHandling.Ignore)]
        public CadPointMm ArcCenterMm { get; set; }

        [JsonProperty("arcRadiusMm", NullValueHandling = NullValueHandling.Ignore)]
        public double? ArcRadiusMm { get; set; }

        /// <summary>Arc start angle, degrees CCW from +X in model space.</summary>
        [JsonProperty("arcStartAngleDeg", NullValueHandling = NullValueHandling.Ignore)]
        public double? ArcStartAngleDeg { get; set; }

        [JsonProperty("arcEndAngleDeg", NullValueHandling = NullValueHandling.Ignore)]
        public double? ArcEndAngleDeg { get; set; }
    }

    /// <summary>
    /// REV-149: a DWG block instance (door / window / column symbol) with its placement.
    /// Insert point + rotation + mirror is what a door actually needs — far more reliable
    /// than clustering the exploded line soup back into a symbol.
    /// </summary>
    public class CadBlockItem
    {
        [JsonProperty("index")]
        public int Index { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; } = string.Empty;

        /// <summary>Block insertion point (transform origin) in mm.</summary>
        [JsonProperty("insertMm")]
        public CadPointMm InsertMm { get; set; } = new CadPointMm();

        /// <summary>Rotation of the block X axis, degrees CCW from +X.</summary>
        [JsonProperty("rotationDeg")]
        public double RotationDeg { get; set; }

        /// <summary>True when the block basis is left-handed (mirrored) — flips door hand.</summary>
        [JsonProperty("mirrored")]
        public bool Mirrored { get; set; }

        [JsonProperty("layer")]
        public string Layer { get; set; } = string.Empty;

        [JsonProperty("segmentCount")]
        public int SegmentCount { get; set; }

        [JsonProperty("bboxMm", NullValueHandling = NullValueHandling.Ignore)]
        public CadBboxMm BboxMm { get; set; }

        [JsonProperty("cadLinkElementId")]
        public long CadLinkElementId { get; set; }

        /// <summary>importInstance (exploded block) | nested (block inside a linked DWG).</summary>
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

        /// <summary>REV-149: DWG block instances behind the segments (see CadSegmentItem.BlockIndex).</summary>
        [JsonProperty("blocks")]
        public List<CadBlockItem> Blocks { get; set; } = new List<CadBlockItem>();
    }
}
