using Newtonsoft.Json;
using RevitMCPCommandSet.Models.Common;

namespace RevitMCPCommandSet.Models.Architecture
{
    /// <summary>
    /// One opening the смежник needs cut in our structure (REV-168) — a row of the
    /// задание на отверстия, whether or not it has been built yet.
    /// </summary>
    public class MepOpeningPlanItem
    {
        /// <summary>«ОТВ-2эт-03» — the mark on the drawing and in the ведомость.</summary>
        [JsonProperty("mark")]
        public string Mark { get; set; } = string.Empty;

        /// <summary>Our element to be cut. This id works in operate_element as is.</summary>
        [JsonProperty("hostElementId")]
        public long HostElementId { get; set; }

        /// <summary>wall | floor — decides how the opening is made.</summary>
        [JsonProperty("hostKind")]
        public string HostKind { get; set; } = string.Empty;

        [JsonProperty("hostCategory")]
        public string HostCategory { get; set; } = string.Empty;

        [JsonProperty("hostType", NullValueHandling = NullValueHandling.Ignore)]
        public string HostType { get; set; }

        [JsonProperty("hostLevel", NullValueHandling = NullValueHandling.Ignore)]
        public string HostLevel { get; set; }

        /// <summary>
        /// Thickness of this element, mm — how the structural layer is told from the
        /// finish when the layers of one wall are folded into one opening.
        /// </summary>
        [JsonProperty("hostThicknessMm")]
        public double HostThicknessMm { get; set; }

        /// <summary>
        /// The other layers of the same wall this hole also has to go through — утеплитель,
        /// штукатурка, отделка. One row is one hole, but every layer is a separate element
        /// in Revit and every one of them gets cut, or the pipe runs into the insulation.
        /// </summary>
        [JsonProperty("alsoCuts", NullValueHandling = NullValueHandling.Ignore)]
        public List<MepOpeningLayerCut> AlsoCuts { get; set; }

        [JsonProperty("roomName", NullValueHandling = NullValueHandling.Ignore)]
        public string RoomName { get; set; }

        [JsonProperty("linkName")]
        public string LinkName { get; set; } = string.Empty;

        [JsonProperty("linkSection", NullValueHandling = NullValueHandling.Ignore)]
        public string LinkSection { get; set; }

        /// <summary>
        /// The engineering elements this opening is for. More than one when a пачка труб
        /// was folded into a single hole — their ids are what the смежник checks.
        /// </summary>
        [JsonProperty("mepElementIds")]
        public List<long> MepElementIds { get; set; } = new List<long>();

        /// <summary>«Труба ⌀110 ×2, Воздуховод 400×200» — what goes through the hole.</summary>
        [JsonProperty("mepDescription")]
        public string MepDescription { get; set; } = string.Empty;

        [JsonProperty("widthMm")]
        public double WidthMm { get; set; }

        [JsonProperty("heightMm")]
        public double HeightMm { get; set; }

        /// <summary>Measured size before clearance and rounding — the evidence.</summary>
        [JsonProperty("measuredWidthMm")]
        public double MeasuredWidthMm { get; set; }

        [JsonProperty("measuredHeightMm")]
        public double MeasuredHeightMm { get; set; }

        /// <summary>Centre of the opening in OUR coordinates, mm.</summary>
        [JsonProperty("centreMm")]
        public JZPoint CentreMm { get; set; } = new JZPoint();

        /// <summary>
        /// Bottom of the opening above its level, mm — the отметка низа a монтажник
        /// measures on site. Null for a floor opening, which has no such thing.
        /// </summary>
        [JsonProperty("bottomAboveLevelMm", NullValueHandling = NullValueHandling.Ignore)]
        public double? BottomAboveLevelMm { get; set; }

        /// <summary>Plan rotation of a floor opening, degrees CCW. Zero for a wall.</summary>
        [JsonProperty("rotationDeg")]
        public double RotationDeg { get; set; }

        /// <summary>planned | created | exists | failed.</summary>
        [JsonProperty("status")]
        public string Status { get; set; } = "planned";

        /// <summary>The opening element once it is in the model.</summary>
        [JsonProperty("openingElementId", NullValueHandling = NullValueHandling.Ignore)]
        public long? OpeningElementId { get; set; }

        /// <summary>Why it was skipped, or why it failed.</summary>
        [JsonProperty("note", NullValueHandling = NullValueHandling.Ignore)]
        public string Note { get; set; }
    }

    /// <summary>
    /// One more element the same hole passes through — a layer of the same wall. It
    /// carries its own centre because the layers sit at different depths across the
    /// assembly, and its own id because Revit cuts each of them separately.
    /// </summary>
    public class MepOpeningLayerCut
    {
        [JsonProperty("hostElementId")]
        public long HostElementId { get; set; }

        [JsonProperty("hostCategory")]
        public string HostCategory { get; set; } = string.Empty;

        [JsonProperty("hostType", NullValueHandling = NullValueHandling.Ignore)]
        public string HostType { get; set; }

        [JsonProperty("hostThicknessMm")]
        public double HostThicknessMm { get; set; }

        [JsonProperty("centreMm")]
        public JZPoint CentreMm { get; set; } = new JZPoint();

        /// <summary>created | failed | exists — filled in when the openings are cut.</summary>
        [JsonProperty("status", NullValueHandling = NullValueHandling.Ignore)]
        public string Status { get; set; }

        [JsonProperty("openingElementId", NullValueHandling = NullValueHandling.Ignore)]
        public long? OpeningElementId { get; set; }
    }

    /// <summary>What one link contributed to the задание.</summary>
    public class MepOpeningLinkInfo
    {
        [JsonProperty("linkName")]
        public string LinkName { get; set; } = string.Empty;

        [JsonProperty("linkInstanceId")]
        public long LinkInstanceId { get; set; }

        [JsonProperty("section", NullValueHandling = NullValueHandling.Ignore)]
        public string Section { get; set; }

        [JsonProperty("scanned")]
        public bool Scanned { get; set; }

        [JsonProperty("openingCount")]
        public int OpeningCount { get; set; }

        [JsonProperty("note", NullValueHandling = NullValueHandling.Ignore)]
        public string Note { get; set; }
    }

    /// <summary>Result of <c>create_mep_openings</c> (REV-168).</summary>
    public class CreateMepOpeningsResult
    {
        [JsonProperty("success")]
        public bool Success { get; set; }

        [JsonProperty("message")]
        public string Message { get; set; } = string.Empty;

        [JsonProperty("hostModel")]
        public string HostModel { get; set; } = string.Empty;

        /// <summary>
        /// False for the preview pass. The model is untouched unless this is true —
        /// the guarantee the whole tool is built around.
        /// </summary>
        [JsonProperty("applied")]
        public bool Applied { get; set; }

        [JsonProperty("clearanceMm")]
        public double ClearanceMm { get; set; }

        [JsonProperty("mergeGapMm")]
        public double MergeGapMm { get; set; }

        [JsonProperty("sizeStepMm")]
        public double SizeStepMm { get; set; }

        [JsonProperty("scope")]
        public string Scope { get; set; } = string.Empty;

        [JsonProperty("totalOpenings")]
        public int TotalOpenings { get; set; }

        /// <summary>Crossings found before пачки труб and wall layers were folded.</summary>
        [JsonProperty("rawCrossings")]
        public int RawCrossings { get; set; }

        /// <summary>
        /// Overlaps that turned out to run along the inside of an element rather than
        /// through it. Those are a coordination argument, not a hole to cut.
        /// </summary>
        [JsonProperty("skippedNotThrough")]
        public int SkippedNotThrough { get; set; }

        [JsonProperty("createdCount")]
        public int CreatedCount { get; set; }

        /// <summary>Openings already in the model — what keeps a re-run from doubling up.</summary>
        [JsonProperty("skippedExistingCount")]
        public int SkippedExistingCount { get; set; }

        [JsonProperty("failedCount")]
        public int FailedCount { get; set; }

        [JsonProperty("truncated")]
        public bool Truncated { get; set; }

        [JsonProperty("elapsedMs")]
        public long ElapsedMs { get; set; }

        [JsonProperty("links")]
        public List<MepOpeningLinkInfo> Links { get; set; } = new List<MepOpeningLinkInfo>();

        [JsonProperty("openings")]
        public List<MepOpeningPlanItem> Openings { get; set; } = new List<MepOpeningPlanItem>();
    }
}
