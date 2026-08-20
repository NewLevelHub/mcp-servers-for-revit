using Newtonsoft.Json;
using RevitMCPCommandSet.Models.Common;

namespace RevitMCPCommandSet.Models.DataExtraction
{
    /// <summary>
    /// One element of the open model overlapping one element of a link (REV-167).
    /// </summary>
    /// <remarks>
    /// Both element ids are carried on purpose: <c>operate_element</c> takes the host id
    /// and highlights the element straight away, and the link id is what the смежник is
    /// told to look at in their own file. A row without both is a row nobody can act on.
    /// </remarks>
    public class LinkClashItem
    {
        /// <summary>Element of the OPEN model. This id works in operate_element as is.</summary>
        [JsonProperty("hostElementId")]
        public long HostElementId { get; set; }

        [JsonProperty("hostCategory")]
        public string HostCategory { get; set; } = string.Empty;

        /// <summary>Type name of the host element — «Стена 200 мм», not just «Стены».</summary>
        [JsonProperty("hostType", NullValueHandling = NullValueHandling.Ignore)]
        public string HostType { get; set; }

        [JsonProperty("hostLevel", NullValueHandling = NullValueHandling.Ignore)]
        public string HostLevel { get; set; }

        /// <summary>Name of the link file the other element came from.</summary>
        [JsonProperty("linkName")]
        public string LinkName { get; set; } = string.Empty;

        /// <summary>АР / КР / ИОС read off the link file name, when it says so.</summary>
        [JsonProperty("linkSection", NullValueHandling = NullValueHandling.Ignore)]
        public string LinkSection { get; set; }

        /// <summary>Id of the RevitLinkInstance in OUR model.</summary>
        [JsonProperty("linkInstanceId")]
        public long LinkInstanceId { get; set; }

        /// <summary>Element id INSIDE the linked document — not ours.</summary>
        [JsonProperty("linkElementId")]
        public long LinkElementId { get; set; }

        [JsonProperty("linkCategory")]
        public string LinkCategory { get; set; } = string.Empty;

        [JsonProperty("linkType", NullValueHandling = NullValueHandling.Ignore)]
        public string LinkType { get; set; }

        /// <summary>Centre of the overlap in OUR coordinates, mm — where to fly the view to.</summary>
        [JsonProperty("pointMm")]
        public JZPoint PointMm { get; set; } = new JZPoint();

        /// <summary>Room of the open model the overlap centre falls in, when there is one.</summary>
        [JsonProperty("roomName", NullValueHandling = NullValueHandling.Ignore)]
        public string RoomName { get; set; }

        [JsonProperty("roomNumber", NullValueHandling = NullValueHandling.Ignore)]
        public string RoomNumber { get; set; }

        /// <summary>
        /// How deep the two went into each other: the smallest side of the overlap body, mm.
        /// Null when Revit could not intersect the two solids — see <see cref="Note"/>.
        /// </summary>
        [JsonProperty("depthMm", NullValueHandling = NullValueHandling.Ignore)]
        public double? DepthMm { get; set; }

        /// <summary>Volume of the overlap, m³ — separates a graze from a beam through a wall.</summary>
        [JsonProperty("overlapVolumeM3", NullValueHandling = NullValueHandling.Ignore)]
        public double? OverlapVolumeM3 { get; set; }

        /// <summary>Set when the depth could not be measured and the row is kept anyway.</summary>
        [JsonProperty("note", NullValueHandling = NullValueHandling.Ignore)]
        public string Note { get; set; }
    }

    /// <summary>How many clashes one pair of categories accounts for.</summary>
    public class ClashPairCount
    {
        [JsonProperty("hostCategory")]
        public string HostCategory { get; set; } = string.Empty;

        [JsonProperty("linkCategory")]
        public string LinkCategory { get; set; } = string.Empty;

        [JsonProperty("count")]
        public int Count { get; set; }

        /// <summary>Deepest overlap in this pair, mm — where to start.</summary>
        [JsonProperty("maxDepthMm", NullValueHandling = NullValueHandling.Ignore)]
        public double? MaxDepthMm { get; set; }
    }

    /// <summary>What one link contributed to the run.</summary>
    public class LinkClashScanInfo
    {
        [JsonProperty("linkName")]
        public string LinkName { get; set; } = string.Empty;

        [JsonProperty("linkInstanceId")]
        public long LinkInstanceId { get; set; }

        [JsonProperty("section", NullValueHandling = NullValueHandling.Ignore)]
        public string Section { get; set; }

        /// <summary>False when the link is unloaded or missing — then <see cref="Note"/> says why.</summary>
        [JsonProperty("scanned")]
        public bool Scanned { get; set; }

        [JsonProperty("clashCount")]
        public int ClashCount { get; set; }

        [JsonProperty("elapsedMs")]
        public long ElapsedMs { get; set; }

        [JsonProperty("note", NullValueHandling = NullValueHandling.Ignore)]
        public string Note { get; set; }
    }

    /// <summary>Result of <c>check_link_clashes</c> (REV-167). Read-only by construction.</summary>
    public class CheckLinkClashesResult
    {
        [JsonProperty("success")]
        public bool Success { get; set; }

        [JsonProperty("message")]
        public string Message { get; set; } = string.Empty;

        [JsonProperty("hostModel")]
        public string HostModel { get; set; } = string.Empty;

        /// <summary>Cut-off actually applied, mm — the requested one after clamping.</summary>
        [JsonProperty("toleranceMm")]
        public double ToleranceMm { get; set; }

        /// <summary>Level the host side was limited to, or «вся модель».</summary>
        [JsonProperty("scope")]
        public string Scope { get; set; } = string.Empty;

        [JsonProperty("hostCategories")]
        public List<string> HostCategories { get; set; } = new List<string>();

        [JsonProperty("linkCategories")]
        public List<string> LinkCategories { get; set; } = new List<string>();

        /// <summary>Host elements actually put through the intersection test.</summary>
        [JsonProperty("hostElementsScanned")]
        public int HostElementsScanned { get; set; }

        /// <summary>Overlaps found before the cap — what «столько всего» means.</summary>
        [JsonProperty("totalClashes")]
        public int TotalClashes { get; set; }

        /// <summary>Overlaps dropped for being shallower than the tolerance.</summary>
        [JsonProperty("ignoredBelowTolerance")]
        public int IgnoredBelowTolerance { get; set; }

        /// <summary>True when the run hit the cap or the time budget and stopped early.</summary>
        [JsonProperty("truncated")]
        public bool Truncated { get; set; }

        [JsonProperty("elapsedMs")]
        public long ElapsedMs { get; set; }

        /// <summary>The «12 балок ↔ проёмы» reading, over ALL clashes, not just the page.</summary>
        [JsonProperty("byCategoryPair")]
        public List<ClashPairCount> ByCategoryPair { get; set; } = new List<ClashPairCount>();

        [JsonProperty("links")]
        public List<LinkClashScanInfo> Links { get; set; } = new List<LinkClashScanInfo>();

        [JsonProperty("clashes")]
        public List<LinkClashItem> Clashes { get; set; } = new List<LinkClashItem>();
    }
}
