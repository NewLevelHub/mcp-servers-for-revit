using Newtonsoft.Json;

namespace RevitMCPCommandSet.Models.Common
{
    public class FamilyTypeInfo
    {
        /// <summary>Revit ElementId of the type. Serialized as typeId for MCP/agent compatibility.</summary>
        [JsonProperty("typeId")]
        public long FamilyTypeId { get; set; }

        [JsonProperty("uniqueId")]
        public string UniqueId { get; set; }

        [JsonProperty("familyName")]
        public string FamilyName { get; set; }

        [JsonProperty("name")]
        public string TypeName { get; set; }

        [JsonProperty("category")]
        public string Category { get; set; }
    }
}
