using Newtonsoft.Json;

namespace RevitMCPCommandSet.Models.DataExtraction
{
    /// <summary>One loaded type and how many instances of it exist (REV-179).</summary>
    public class ModelStandardTypeInfo
    {
        [JsonProperty("category")]
        public string Category { get; set; } = string.Empty;

        /// <summary>Empty for system types (walls, floors, ceilings) — they have no Family container.</summary>
        [JsonProperty("familyName")]
        public string FamilyName { get; set; } = string.Empty;

        [JsonProperty("typeName")]
        public string TypeName { get; set; } = string.Empty;

        [JsonProperty("typeId")]
        public long TypeId { get; set; }

        /// <summary>0 means the type is loaded but never placed — the "unused type" case.</summary>
        [JsonProperty("instanceCount")]
        public int InstanceCount { get; set; }
    }

    /// <summary>
    /// How many elements of one category sit without a level, or in one workset — aggregated,
    /// not per-element, so the payload stays small regardless of model size (REV-179).
    /// </summary>
    public class ModelStandardCategoryCount
    {
        [JsonProperty("category")]
        public string Category { get; set; } = string.Empty;

        [JsonProperty("count")]
        public int Count { get; set; }

        /// <summary>Up to 5 element ids, for "click and see" without shipping the whole list.</summary>
        [JsonProperty("sampleElementIds")]
        public List<long> SampleElementIds { get; set; } = new();
    }

    /// <summary>One (category, workset) pair and how many elements of that category sit there.</summary>
    public class ModelStandardWorksetCategoryCount
    {
        [JsonProperty("category")]
        public string Category { get; set; } = string.Empty;

        [JsonProperty("worksetName")]
        public string WorksetName { get; set; } = string.Empty;

        [JsonProperty("count")]
        public int Count { get; set; }

        [JsonProperty("sampleElementIds")]
        public List<long> SampleElementIds { get; set; } = new();
    }

    public class ModelStandardWorksetInfo
    {
        [JsonProperty("name")]
        public string Name { get; set; } = string.Empty;

        [JsonProperty("kind")]
        public string Kind { get; set; } = string.Empty;

        [JsonProperty("elementCount")]
        public int ElementCount { get; set; }
    }

    public class ModelStandardGroupInfo
    {
        [JsonProperty("name")]
        public string Name { get; set; } = string.Empty;

        /// <summary>"Model" or "Detail".</summary>
        [JsonProperty("kind")]
        public string Kind { get; set; } = string.Empty;

        [JsonProperty("instanceCount")]
        public int InstanceCount { get; set; }

        /// <summary>Elements inside one instance of this group type.</summary>
        [JsonProperty("memberCount")]
        public int MemberCount { get; set; }
    }

    public class ModelStandardViewInfo
    {
        [JsonProperty("name")]
        public string Name { get; set; } = string.Empty;

        [JsonProperty("viewType")]
        public string ViewType { get; set; } = string.Empty;

        [JsonProperty("scale", NullValueHandling = NullValueHandling.Ignore)]
        public int? Scale { get; set; }

        [JsonProperty("hasTemplate")]
        public bool HasTemplate { get; set; }

        [JsonProperty("templateName", NullValueHandling = NullValueHandling.Ignore)]
        public string TemplateName { get; set; }
    }

    /// <summary>A linked file's load status — just enough to flag it as suspicious (REV-179).</summary>
    public class ModelStandardLinkInfo
    {
        [JsonProperty("name")]
        public string Name { get; set; } = string.Empty;

        [JsonProperty("status")]
        public string Status { get; set; } = string.Empty;
    }

    /// <summary>
    /// Raw facts about the model — no grading here. <c>server/src/quality/standardRules.ts</c>
    /// applies the organization's config to these facts and produces the prioritized findings;
    /// this class only reports what is true, cheaply, regardless of model size (REV-179).
    /// </summary>
    public class CheckModelStandardResult
    {
        [JsonProperty("success")]
        public bool Success { get; set; }

        [JsonProperty("message")]
        public string Message { get; set; } = string.Empty;

        [JsonProperty("worksharingEnabled")]
        public bool WorksharingEnabled { get; set; }

        [JsonProperty("worksets")]
        public List<ModelStandardWorksetInfo> Worksets { get; set; } = new();

        [JsonProperty("types")]
        public List<ModelStandardTypeInfo> Types { get; set; } = new();

        /// <summary>Model-category elements only — annotation/view-specific elements have no level.</summary>
        [JsonProperty("elementsWithoutLevel")]
        public List<ModelStandardCategoryCount> ElementsWithoutLevel { get; set; } = new();

        /// <summary>Empty when <see cref="WorksharingEnabled"/> is false.</summary>
        [JsonProperty("worksetByCategory")]
        public List<ModelStandardWorksetCategoryCount> WorksetByCategory { get; set; } = new();

        [JsonProperty("groups")]
        public List<ModelStandardGroupInfo> Groups { get; set; } = new();

        [JsonProperty("views")]
        public List<ModelStandardViewInfo> Views { get; set; } = new();

        [JsonProperty("links")]
        public List<ModelStandardLinkInfo> Links { get; set; } = new();

        [JsonProperty("elapsedMs")]
        public long ElapsedMs { get; set; }
    }
}
