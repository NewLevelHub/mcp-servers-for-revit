using Newtonsoft.Json;
using RevitMCPCommandSet.Models.Common;

namespace RevitMCPCommandSet.Models.DataExtraction
{
    /// <summary>
    /// One element as a snapshot records it (REV-170) — identity, where it sits, and
    /// the key parameters the diff is allowed to notice.
    /// </summary>
    /// <remarks>
    /// The hash of the parameters is **not** computed here. It is computed on the
    /// server, where the tests run, and where changing the rule does not mean
    /// reinstalling a plugin into every architect's Revit.
    /// </remarks>
    public class ModelSnapshotElement
    {
        [JsonProperty("elementId")]
        public long ElementId { get; set; }

        /// <summary>
        /// The identity a snapshot is keyed on. <c>ElementId</c> survives an edit but not
        /// a copy-paste between files or a workset round trip; <c>UniqueId</c> does, so a
        /// diff that matched on the numeric id alone would report half a model as
        /// "удалено + добавлено".
        /// </summary>
        [JsonProperty("uniqueId")]
        public string UniqueId { get; set; } = string.Empty;

        /// <summary>
        /// The <c>BuiltInCategory</c> name, e.g. <c>OST_Walls</c>. This is what the diff
        /// compares on: <see cref="Category"/> is the localised label, and Revit on this
        /// very machine has been seen switching between RU and EN between sessions —
        /// keying on it would turn a language change into "вся модель переделана".
        /// </summary>
        [JsonProperty("categoryKey")]
        public string CategoryKey { get; set; } = string.Empty;

        /// <summary>Category as the running Revit spells it — for showing, not for comparing.</summary>
        [JsonProperty("category")]
        public string Category { get; set; } = string.Empty;

        [JsonProperty("familyName")]
        public string FamilyName { get; set; } = string.Empty;

        [JsonProperty("typeName")]
        public string TypeName { get; set; } = string.Empty;

        [JsonProperty("typeId")]
        public long TypeId { get; set; }

        [JsonProperty("levelName")]
        public string LevelName { get; set; } = string.Empty;

        [JsonProperty("roomName")]
        public string RoomName { get; set; } = string.Empty;

        [JsonProperty("roomNumber")]
        public string RoomNumber { get; set; } = string.Empty;

        /// <summary>Bounding box in OUR coordinates, mm. Null when the element has no geometry.</summary>
        [JsonProperty("boundingBoxMm", NullValueHandling = NullValueHandling.Ignore)]
        public ModelSnapshotBox BoundingBoxMm { get; set; }

        /// <summary>
        /// Key parameters, keyed by the stable identifier (a <c>BuiltInParameter</c> name,
        /// or the name as asked for in <c>extraParameters</c>) — never by the localised
        /// display name, for the same reason as <see cref="CategoryKey"/>. Values are
        /// raw: doubles in Revit's internal units, ElementId resolved to the element's
        /// name. Rounding and hashing happen on the server.
        /// </summary>
        [JsonProperty("parameters")]
        public Dictionary<string, object> Parameters { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>Габарит элемента — two corners in mm.</summary>
    public class ModelSnapshotBox
    {
        [JsonProperty("min")]
        public JZPoint Min { get; set; } = new JZPoint();

        [JsonProperty("max")]
        public JZPoint Max { get; set; } = new JZPoint();
    }

    /// <summary>
    /// One page of a snapshot (REV-170). A 300k-element model does not fit a single
    /// socket frame — the cap is 50 MB — so the model is read in pages and the server
    /// writes each one into SQLite before asking for the next.
    /// </summary>
    public class ModelSnapshotPage
    {
        [JsonProperty("success")]
        public bool Success { get; set; }

        [JsonProperty("message")]
        public string Message { get; set; } = string.Empty;

        [JsonProperty("modelName")]
        public string ModelName { get; set; } = string.Empty;

        [JsonProperty("modelPath")]
        public string ModelPath { get; set; } = string.Empty;

        [JsonProperty("revitVersion")]
        public string RevitVersion { get; set; } = string.Empty;

        /// <summary>Elements that pass the snapshot filter, across the whole model.</summary>
        [JsonProperty("totalElements")]
        public int TotalElements { get; set; }

        [JsonProperty("offset")]
        public int Offset { get; set; }

        [JsonProperty("count")]
        public int Count { get; set; }

        [JsonProperty("hasMore")]
        public bool HasMore { get; set; }

        /// <summary>
        /// Identifies the element list this page was cut from. The list is built once —
        /// a full pass over the model — and cached for the pages that follow; a token
        /// that comes back different mid-run means the model was edited while the
        /// snapshot was being taken, and the server says so instead of quietly
        /// stitching two different models together.
        /// </summary>
        [JsonProperty("snapshotToken")]
        public string SnapshotToken { get; set; } = string.Empty;

        /// <summary>
        /// Stable parameter key → the label this Revit shows for it. Sent once per page
        /// rather than per element, so the diff can speak to the architect in their own
        /// words without every row carrying the language of the session it was taken in.
        /// </summary>
        [JsonProperty("parameterLabels")]
        public Dictionary<string, string> ParameterLabels { get; set; } = new Dictionary<string, string>();

        /// <summary>Wall-clock time of this page, including the full pass on the first one.</summary>
        [JsonProperty("elapsedMs")]
        public long ElapsedMs { get; set; }

        /// <summary>Set on the page that had to build the element list — the expensive one.</summary>
        [JsonProperty("scanElapsedMs")]
        public long ScanElapsedMs { get; set; }

        [JsonProperty("elements")]
        public List<ModelSnapshotElement> Elements { get; set; } = new List<ModelSnapshotElement>();
    }
}
