using Newtonsoft.Json;
using RevitMCPCommandSet.Models.Common;

namespace RevitMCPCommandSet.Models.DataExtraction
{
    /// <summary>
    /// Where a link sits in our coordinates — the numbers behind
    /// <c>RevitLinkInstance.GetTotalTransform()</c> (REV-166).
    /// </summary>
    public class LinkPlacementInfo
    {
        /// <summary>Link origin expressed in the host model's internal coordinates, mm.</summary>
        [JsonProperty("originMm")]
        public JZPoint OriginMm { get; set; } = new JZPoint();

        /// <summary>Rotation about Z, degrees CCW. 0 for a link inserted straight.</summary>
        [JsonProperty("rotationDegrees")]
        public double RotationDegrees { get; set; }

        /// <summary>Set when the link is mirrored — a negative-determinant transform.</summary>
        [JsonProperty("mirrored")]
        public bool Mirrored { get; set; }

        /// <summary>
        /// True when the link sits exactly on our origin unrotated, i.e. link
        /// coordinates and ours are already the same numbers.
        /// </summary>
        [JsonProperty("originShared")]
        public bool OriginShared { get; set; }
    }

    /// <summary>
    /// One element of the link shown in both coordinate systems, so the offset can be
    /// checked against the model instead of taken on trust (REV-166).
    /// </summary>
    public class LinkCoordinateSample
    {
        /// <summary>Element id **inside the linked document**, not ours.</summary>
        [JsonProperty("elementId")]
        public long ElementId { get; set; }

        [JsonProperty("category")]
        public string Category { get; set; } = string.Empty;

        /// <summary>As the linked file itself stores it, mm.</summary>
        [JsonProperty("linkPointMm")]
        public JZPoint LinkPointMm { get; set; } = new JZPoint();

        /// <summary>The same element after <c>GetTotalTransform()</c> — our coordinates, mm.</summary>
        [JsonProperty("hostPointMm")]
        public JZPoint HostPointMm { get; set; } = new JZPoint();
    }

    /// <summary>A Revit category inside a link and how many elements it holds.</summary>
    public class LinkCategoryCount
    {
        [JsonProperty("category")]
        public string Category { get; set; } = string.Empty;

        [JsonProperty("count")]
        public int Count { get; set; }
    }

    /// <summary>
    /// A level of a model — ours or a link's — for сверка общей площадки (REV-169).
    /// </summary>
    public class SiteLevelInfo
    {
        [JsonProperty("name")]
        public string Name { get; set; } = string.Empty;

        /// <summary>Elevation above the internal origin, mm. The number that has to match.</summary>
        [JsonProperty("elevationMm")]
        public double ElevationMm { get; set; }

        [JsonProperty("elementId")]
        public long ElementId { get; set; }
    }

    /// <summary>
    /// A grid line. Compared by name and by where it runs — a matching name on a line
    /// half a metre away is worse than a missing one, because it looks fine.
    /// </summary>
    public class SiteGridInfo
    {
        [JsonProperty("name")]
        public string Name { get; set; } = string.Empty;

        [JsonProperty("startMm")]
        public JZPoint StartMm { get; set; } = new JZPoint();

        [JsonProperty("endMm")]
        public JZPoint EndMm { get; set; } = new JZPoint();

        /// <summary>Curved grids exist and cannot be compared as two endpoints alone.</summary>
        [JsonProperty("isCurved")]
        public bool IsCurved { get; set; }

        [JsonProperty("elementId")]
        public long ElementId { get; set; }
    }

    /// <summary>
    /// Where a model thinks it stands: the two points every проект is set out from.
    /// </summary>
    public class SitePointsInfo
    {
        /// <summary>Project Base Point, in the internal coordinates of that model, mm.</summary>
        [JsonProperty("projectBasePointMm", NullValueHandling = NullValueHandling.Ignore)]
        public JZPoint ProjectBasePointMm { get; set; }

        /// <summary>Survey Point — where the model sits on the actual site, mm.</summary>
        [JsonProperty("surveyPointMm", NullValueHandling = NullValueHandling.Ignore)]
        public JZPoint SurveyPointMm { get; set; }

        /// <summary>Angle from Project North to True North, degrees.</summary>
        [JsonProperty("angleToTrueNorthDeg", NullValueHandling = NullValueHandling.Ignore)]
        public double? AngleToTrueNorthDeg { get; set; }
    }

    /// <summary>Levels, grids and setting-out points of one model (REV-169).</summary>
    public class SiteSurveyInfo
    {
        [JsonProperty("levels", NullValueHandling = NullValueHandling.Ignore)]
        public List<SiteLevelInfo> Levels { get; set; }

        [JsonProperty("grids", NullValueHandling = NullValueHandling.Ignore)]
        public List<SiteGridInfo> Grids { get; set; }

        [JsonProperty("points", NullValueHandling = NullValueHandling.Ignore)]
        public SitePointsInfo Points { get; set; }

        /// <summary>Why something is missing — a link that could not be read, say.</summary>
        [JsonProperty("note", NullValueHandling = NullValueHandling.Ignore)]
        public string Note { get; set; }
    }

    /// <summary>One <c>RevitLinkInstance</c> of the open model.</summary>
    public class LinkedModelInfo
    {
        [JsonProperty("instanceId")]
        public long InstanceId { get; set; }

        [JsonProperty("typeId")]
        public long TypeId { get; set; }

        /// <summary>Link file name as Revit shows it in «Диспетчер связей».</summary>
        [JsonProperty("name")]
        public string Name { get; set; } = string.Empty;

        /// <summary>Full path, when Revit can still resolve one.</summary>
        [JsonProperty("path", NullValueHandling = NullValueHandling.Ignore)]
        public string Path { get; set; }

        /// <summary>АР / КР / ИОС / ГП read off the file name; empty when unclear.</summary>
        [JsonProperty("section")]
        public string Section { get; set; } = string.Empty;

        /// <summary>The token in the name the section was read from — the evidence.</summary>
        [JsonProperty("sectionFrom", NullValueHandling = NullValueHandling.Ignore)]
        public string SectionFrom { get; set; }

        /// <summary>Revit's own <c>LinkedFileStatus</c>: Loaded / Unloaded / NotFound / Invalid.</summary>
        [JsonProperty("status")]
        public string Status { get; set; } = string.Empty;

        /// <summary>The same status in Russian, for the architect rather than the log.</summary>
        [JsonProperty("statusText")]
        public string StatusText { get; set; } = string.Empty;

        /// <summary>Content is readable: the only case where counts and samples exist.</summary>
        [JsonProperty("isReadable")]
        public bool IsReadable { get; set; }

        /// <summary>A link brought in by another link rather than placed here.</summary>
        [JsonProperty("isNested", NullValueHandling = NullValueHandling.Ignore)]
        public bool? IsNested { get; set; }

        [JsonProperty("placement", NullValueHandling = NullValueHandling.Ignore)]
        public LinkPlacementInfo Placement { get; set; }

        /// <summary>
        /// Levels, grids and setting-out points of the link, when asked for (REV-169).
        /// In the link's OWN coordinates: сверка общей площадки is about whether the two
        /// models are set out the same way, and transforming them first would hide the
        /// very difference it looks for.
        /// </summary>
        [JsonProperty("site", NullValueHandling = NullValueHandling.Ignore)]
        public SiteSurveyInfo Site { get; set; }

        /// <summary>Elements in the link, or null when it could not be opened.</summary>
        [JsonProperty("elementCount", NullValueHandling = NullValueHandling.Ignore)]
        public int? ElementCount { get; set; }

        /// <summary>«вся связь» or «уровень …» — what the count covers.</summary>
        [JsonProperty("elementCountScope", NullValueHandling = NullValueHandling.Ignore)]
        public string ElementCountScope { get; set; }

        [JsonProperty("categories", NullValueHandling = NullValueHandling.Ignore)]
        public List<LinkCategoryCount> Categories { get; set; }

        [JsonProperty("samples", NullValueHandling = NullValueHandling.Ignore)]
        public List<LinkCoordinateSample> Samples { get; set; }

        /// <summary>Time spent on this link alone — the traversal cost, per link.</summary>
        [JsonProperty("elapsedMs")]
        public long ElapsedMs { get; set; }

        /// <summary>Why something is missing: unloaded, not found, or the read failed.</summary>
        [JsonProperty("note", NullValueHandling = NullValueHandling.Ignore)]
        public string Note { get; set; }
    }

    /// <summary>Result of <c>get_linked_models</c> (REV-166). Read-only by construction.</summary>
    public class GetLinkedModelsResult
    {
        [JsonProperty("success")]
        public bool Success { get; set; }

        [JsonProperty("message")]
        public string Message { get; set; } = string.Empty;

        /// <summary>
        /// Levels, grids and setting-out points of OUR model, when asked for (REV-169) —
        /// the side every link is compared against.
        /// </summary>
        [JsonProperty("hostSite", NullValueHandling = NullValueHandling.Ignore)]
        public SiteSurveyInfo HostSite { get; set; }

        /// <summary>Title of the open model, so a link list is never read against the wrong file.</summary>
        [JsonProperty("hostModel")]
        public string HostModel { get; set; } = string.Empty;

        [JsonProperty("totalLinks")]
        public int TotalLinks { get; set; }

        [JsonProperty("loadedCount")]
        public int LoadedCount { get; set; }

        /// <summary>Unloaded on purpose — the file exists, its content just is not in memory.</summary>
        [JsonProperty("unloadedCount")]
        public int UnloadedCount { get; set; }

        /// <summary>Not found or invalid — the broken links someone has to re-path.</summary>
        [JsonProperty("brokenCount")]
        public int BrokenCount { get; set; }

        /// <summary>Total wall-clock time of the traversal — the REV-166 speed answer.</summary>
        [JsonProperty("elapsedMs")]
        public long ElapsedMs { get; set; }

        [JsonProperty("links")]
        public List<LinkedModelInfo> Links { get; set; } = new List<LinkedModelInfo>();
    }
}
