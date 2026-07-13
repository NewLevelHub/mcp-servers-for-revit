using Newtonsoft.Json;

namespace RevitMCPCommandSet.Models.Views;

public class FinishScheduleCreationInfo
{
    [JsonProperty("name")]
    public string Name { get; set; } = "Room Finish Schedule";

    [JsonProperty("templateId")]
    public string TemplateId { get; set; } = string.Empty;

    [JsonProperty("type")]
    public string Type { get; set; } = "Regular";

    /// <summary>
    ///     Reproduce the full ADSK_RU-style finish schedule chain
    ///     (key styles → data filling → output list) instead of a single schedule
    /// </summary>
    [JsonProperty("reproduceTemplateChain")]
    public bool ReproduceTemplateChain { get; set; } = true;

    /// <summary>
    ///     Explicit key-schedule template name, e.g. 'В_Отделка-помещения-01_Стили_Ключевая'.
    ///     When empty, the chain is auto-discovered by ADSK_RU naming patterns.
    /// </summary>
    [JsonProperty("keyScheduleTemplateName")]
    public string KeyScheduleTemplateName { get; set; } = string.Empty;

    /// <summary>
    ///     Explicit data-filling schedule template name, e.g. 'В_Отделка-помещения-02_Заполнение данных'
    /// </summary>
    [JsonProperty("dataScheduleTemplateName")]
    public string DataScheduleTemplateName { get; set; } = string.Empty;

    /// <summary>
    ///     Explicit output schedule template names,
    ///     e.g. 'О_АР_Ведомость отделки помещений_Имя' and '…_Номер'
    /// </summary>
    [JsonProperty("outputScheduleTemplateNames")]
    public List<string> OutputScheduleTemplateNames { get; set; } = new List<string>();

    /// <summary>
    ///     Duplicate the key schedule instead of reusing it. Off by default because a duplicated
    ///     key schedule defines its own key parameter and loses the link to the data schedule.
    /// </summary>
    [JsonProperty("duplicateKeySchedule")]
    public bool DuplicateKeySchedule { get; set; }

    [JsonProperty("includeUnplacedRooms")]
    public bool IncludeUnplacedRooms { get; set; }

    [JsonProperty("includeNotEnclosedRooms")]
    public bool IncludeNotEnclosedRooms { get; set; }

    [JsonProperty("missingFinishWarningThreshold")]
    public double MissingFinishWarningThreshold { get; set; } = 0.30;
}

/// <summary>
///     One reproduced stage of the finish schedule chain
/// </summary>
public class FinishScheduleChainItem
{
    /// <summary>
    ///     Chain stage: key, data, or output
    /// </summary>
    [JsonProperty("role")]
    public string Role { get; set; } = string.Empty;

    [JsonProperty("templateId")]
    public long TemplateId { get; set; }

    [JsonProperty("templateName")]
    public string TemplateName { get; set; } = string.Empty;

    [JsonProperty("scheduleId")]
    public long ScheduleId { get; set; }

    [JsonProperty("scheduleUniqueId")]
    public string ScheduleUniqueId { get; set; } = string.Empty;

    [JsonProperty("scheduleName")]
    public string ScheduleName { get; set; } = string.Empty;

    /// <summary>
    ///     True when the existing schedule was reused instead of duplicated
    ///     (the key schedule by default, to keep the key parameter linked)
    /// </summary>
    [JsonProperty("reusedExisting")]
    public bool ReusedExisting { get; set; }
}

public class FinishScheduleCreationResult
{
    [JsonProperty("success")]
    public bool Success { get; set; }

    [JsonProperty("message")]
    public string Message { get; set; } = string.Empty;

    [JsonProperty("scheduleId")]
    public long ScheduleId { get; set; }

    [JsonProperty("scheduleUniqueId")]
    public string ScheduleUniqueId { get; set; } = string.Empty;

    [JsonProperty("scheduleName")]
    public string ScheduleName { get; set; } = string.Empty;

    [JsonProperty("templateId")]
    public string TemplateId { get; set; } = string.Empty;

    /// <summary>
    ///     True when the template chain (key → data → output) was reproduced;
    ///     false when the single-schedule fallback was used
    /// </summary>
    [JsonProperty("chainReproduced")]
    public bool ChainReproduced { get; set; }

    /// <summary>
    ///     Reproduced chain stages in order: key, data, outputs
    /// </summary>
    [JsonProperty("chain")]
    public List<FinishScheduleChainItem> Chain { get; set; } = new List<FinishScheduleChainItem>();

    [JsonProperty("totalRooms")]
    public int TotalRooms { get; set; }

    [JsonProperty("roomsWithMissingFinishes")]
    public int RoomsWithMissingFinishes { get; set; }

    [JsonProperty("missingFinishRatio")]
    public double MissingFinishRatio { get; set; }

    [JsonProperty("fieldCount")]
    public int FieldCount { get; set; }

    [JsonProperty("executionTimeMs")]
    public long ExecutionTimeMs { get; set; }

    [JsonProperty("warnings")]
    public List<string> Warnings { get; set; } = new List<string>();
}
