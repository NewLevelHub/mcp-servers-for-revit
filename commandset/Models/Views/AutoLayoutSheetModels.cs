using Newtonsoft.Json;

namespace RevitMCPCommandSet.Models.Views;

/// <summary>
///     One view or schedule to place during auto-layout
/// </summary>
public class AutoLayoutItemInfo
{
    [JsonProperty("viewId")]
    public long ViewId { get; set; }

    [JsonProperty("viewUniqueId")]
    public string ViewUniqueId { get; set; } = string.Empty;

    [JsonProperty("viewName")]
    public string ViewName { get; set; } = string.Empty;
}

/// <summary>
///     Information for automatic sheet layout
/// </summary>
public class AutoLayoutSheetInfo
{
    [JsonProperty("sheetId")]
    public long SheetId { get; set; }

    [JsonProperty("sheetUniqueId")]
    public string SheetUniqueId { get; set; } = string.Empty;

    [JsonProperty("sheetNumber")]
    public string SheetNumber { get; set; } = string.Empty;

    [JsonProperty("sheetName")]
    public string SheetName { get; set; } = string.Empty;

    /// <summary>
    ///     Create the sheet (with a project title block) when it cannot be resolved
    /// </summary>
    [JsonProperty("createSheetIfMissing")]
    public bool CreateSheetIfMissing { get; set; } = true;

    [JsonProperty("titleBlockFamilyName")]
    public string TitleBlockFamilyName { get; set; } = string.Empty;

    [JsonProperty("titleBlockTypeName")]
    public string TitleBlockTypeName { get; set; } = string.Empty;

    /// <summary>
    ///     Views and schedules to place, in the requested order
    /// </summary>
    [JsonProperty("items")]
    public List<AutoLayoutItemInfo> Items { get; set; } = new List<AutoLayoutItemInfo>();

    /// <summary>
    ///     Gap between placed elements, mm
    /// </summary>
    [JsonProperty("spacing")]
    public double Spacing { get; set; } = 10;

    /// <summary>
    ///     Left margin, mm (GOST binding edge is 20 mm)
    /// </summary>
    [JsonProperty("marginLeft")]
    public double MarginLeft { get; set; } = 20;

    [JsonProperty("marginTop")]
    public double MarginTop { get; set; } = 5;

    [JsonProperty("marginRight")]
    public double MarginRight { get; set; } = 5;

    [JsonProperty("marginBottom")]
    public double MarginBottom { get; set; } = 5;

    /// <summary>
    ///     Height of the title block (основная надпись) zone reserved along the sheet bottom, mm
    /// </summary>
    [JsonProperty("titleBlockReserveBottom")]
    public double TitleBlockReserveBottom { get; set; } = 55;

    /// <summary>
    ///     Packing order: input (as requested), heightDesc, or areaDesc
    /// </summary>
    [JsonProperty("order")]
    public string Order { get; set; } = "input";

    /// <summary>
    ///     Treat elements already placed on the sheet as obstacles
    /// </summary>
    [JsonProperty("avoidExisting")]
    public bool AvoidExisting { get; set; } = true;

    /// <summary>
    ///     When a view is already placed on another sheet, duplicate it as a dependent view
    ///     and place the dependent. Defaults to true so customer sheets with occupied plans work.
    /// </summary>
    [JsonProperty("createDependentViewIfNeeded")]
    public bool CreateDependentViewIfNeeded { get; set; } = true;
}

/// <summary>
///     Placement result for one requested item. Coordinates are the lower-left corner
///     in mm from the sheet outline lower-left corner.
/// </summary>
public class AutoLayoutPlacedItem
{
    [JsonProperty("viewId")]
    public long ViewId { get; set; }

    [JsonProperty("viewName")]
    public string ViewName { get; set; } = string.Empty;

    /// <summary>
    ///     viewport or schedule
    /// </summary>
    [JsonProperty("placementType")]
    public string PlacementType { get; set; } = string.Empty;

    [JsonProperty("placed")]
    public bool Placed { get; set; }

    [JsonProperty("elementId")]
    public long ElementId { get; set; }

    [JsonProperty("elementUniqueId")]
    public string ElementUniqueId { get; set; } = string.Empty;

    [JsonProperty("x")]
    public double X { get; set; }

    [JsonProperty("y")]
    public double Y { get; set; }

    [JsonProperty("width")]
    public double Width { get; set; }

    [JsonProperty("height")]
    public double Height { get; set; }

    [JsonProperty("warning")]
    public string Warning { get; set; } = string.Empty;
}

/// <summary>
///     Result of automatic sheet layout
/// </summary>
public class AutoLayoutSheetResult
{
    [JsonProperty("success")]
    public bool Success { get; set; }

    [JsonProperty("message")]
    public string Message { get; set; } = string.Empty;

    [JsonProperty("sheetId")]
    public long SheetId { get; set; }

    [JsonProperty("sheetUniqueId")]
    public string SheetUniqueId { get; set; } = string.Empty;

    [JsonProperty("sheetNumber")]
    public string SheetNumber { get; set; } = string.Empty;

    [JsonProperty("sheetName")]
    public string SheetName { get; set; } = string.Empty;

    [JsonProperty("sheetCreated")]
    public bool SheetCreated { get; set; }

    /// <summary>
    ///     Usable layout area (sheet outline minus margins and title block zone), mm
    /// </summary>
    [JsonProperty("usableWidth")]
    public double UsableWidth { get; set; }

    [JsonProperty("usableHeight")]
    public double UsableHeight { get; set; }

    [JsonProperty("placedCount")]
    public int PlacedCount { get; set; }

    [JsonProperty("skippedCount")]
    public int SkippedCount { get; set; }

    /// <summary>
    ///     True when some items placed and some skipped
    /// </summary>
    [JsonProperty("partialSuccess")]
    public bool PartialSuccess { get; set; }

    /// <summary>
    ///     True when every requested item was placed
    /// </summary>
    [JsonProperty("allPlaced")]
    public bool AllPlaced { get; set; }

    [JsonProperty("items")]
    public List<AutoLayoutPlacedItem> Items { get; set; } = new List<AutoLayoutPlacedItem>();

    [JsonProperty("executionTimeMs")]
    public long ExecutionTimeMs { get; set; }

    [JsonProperty("warnings")]
    public List<string> Warnings { get; set; } = new List<string>();
}
