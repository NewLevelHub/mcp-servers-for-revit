using Newtonsoft.Json;

namespace RevitMCPCommandSet.Models.Views
{
    /// <summary>
    /// Create floor экспликация ViewSchedule(s) from the seeded «Короткий блок» recipe (REV-49 B).
    /// Default: discover levels with (полы)*, merge typified consecutive storeys, place all on one sheet.
    /// </summary>
    public class FloorExplicationCreationInfo
    {
        [JsonProperty("name")]
        public string Name { get; set; } = FloorExplicationDefaults.ScheduleName;

        [JsonProperty("useRecipe")]
        public bool UseRecipe { get; set; } = true;

        [JsonProperty("templateId")]
        public string TemplateId { get; set; } = "";

        [JsonProperty("templateName")]
        public string TemplateName { get; set; } = "";

        [JsonProperty("discoverTemplate")]
        public bool DiscoverTemplate { get; set; } = false;

        /// <summary>
        /// When true (default) and useRecipe=true, create one schedule per discovered finish-floor group.
        /// </summary>
        [JsonProperty("splitByLevelGroups")]
        public bool SplitByLevelGroups { get; set; } = true;

        [JsonProperty("placeOnSheet")]
        public bool PlaceOnSheet { get; set; } = true;

        [JsonProperty("sheetNumber")]
        public string SheetNumber { get; set; } = "";

        [JsonProperty("sheetName")]
        public string SheetName { get; set; } = "Экспликация полов";

        [JsonProperty("titleBlockFamilyName")]
        public string TitleBlockFamilyName { get; set; } = "ADSK_ОсновнаяНадпись";

        [JsonProperty("titleBlockTypeName")]
        public string TitleBlockTypeName { get; set; } = "Форма 3";

        /// <summary>
        /// Paper format for the explication sheet(s): "A0".."A4". Default "A2" (matches
        /// reference RD sheets — floor схема columns are ~234 mm wide, so A3 fits only one
        /// column and schedules overlap; A2 fits two columns).
        /// </summary>
        [JsonProperty("sheetFormat")]
        public string SheetFormat { get; set; } = "A2";

        /// <summary>
        /// When true (default) pack schedules with the shelf auto-layout engine (columns,
        /// title-block aware, overflow to extra sheets) instead of naive top→bottom stacking.
        /// </summary>
        [JsonProperty("autoLayout")]
        public bool AutoLayout { get; set; } = true;

        [JsonProperty("positionX")]
        public double PositionX { get; set; } = 20;

        [JsonProperty("positionY")]
        public double PositionY { get; set; } = 200;
    }

    public static class FloorExplicationDefaults
    {
        public const string ScheduleName = "О_АР_Экспликация полов";
    }

    public class FloorExplicationCreatedItem
    {
        [JsonProperty("groupKey")]
        public string GroupKey { get; set; } = "";

        [JsonProperty("title")]
        public string Title { get; set; } = "";

        [JsonProperty("levelNames")]
        public List<string> LevelNames { get; set; } = new List<string>();

        [JsonProperty("scheduleId")]
        public long ScheduleId { get; set; }

        [JsonProperty("scheduleUniqueId")]
        public string ScheduleUniqueId { get; set; } = "";

        [JsonProperty("scheduleName")]
        public string ScheduleName { get; set; } = "";

        [JsonProperty("sheetId")]
        public long? SheetId { get; set; }

        [JsonProperty("sheetUniqueId")]
        public string SheetUniqueId { get; set; }

        [JsonProperty("sheetNumber")]
        public string SheetNumber { get; set; }

        [JsonProperty("sheetName")]
        public string SheetName { get; set; }
    }

    public class FloorExplicationCreationResult
    {
        [JsonProperty("success")]
        public bool Success { get; set; }

        [JsonProperty("message")]
        public string Message { get; set; } = "";

        [JsonProperty("scheduleId")]
        public long ScheduleId { get; set; }

        [JsonProperty("scheduleUniqueId")]
        public string ScheduleUniqueId { get; set; } = "";

        [JsonProperty("scheduleName")]
        public string ScheduleName { get; set; } = "";

        [JsonProperty("source")]
        public string Source { get; set; } = "";

        [JsonProperty("templateName")]
        public string TemplateName { get; set; } = "";

        [JsonProperty("sheetId")]
        public long? SheetId { get; set; }

        [JsonProperty("sheetUniqueId")]
        public string SheetUniqueId { get; set; }

        [JsonProperty("sheetNumber")]
        public string SheetNumber { get; set; }

        [JsonProperty("sheetName")]
        public string SheetName { get; set; }

        [JsonProperty("created")]
        public List<FloorExplicationCreatedItem> Created { get; set; } = new List<FloorExplicationCreatedItem>();

        [JsonProperty("warnings")]
        public List<string> Warnings { get; set; } = new List<string>();

        [JsonProperty("executionTimeMs")]
        public long ExecutionTimeMs { get; set; }
    }
}
