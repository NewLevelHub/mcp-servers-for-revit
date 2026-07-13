using Newtonsoft.Json;

namespace RevitMCPCommandSet.Models.Views;

/// <summary>
///     Information for rendering a TEP (technical-economic indicators) table on a sheet
/// </summary>
public class TepTableRenderInfo
{
    /// <summary>
    ///     Name of the reference schedule whose column layout (headings, widths, alignment)
    ///     the rendered table should replicate, e.g. 'О_АР_Квартиры_ТЭП' or 'ADSK_О_С_С'
    /// </summary>
    [JsonProperty("templateScheduleName")]
    public string TemplateScheduleName { get; set; } = string.Empty;

    /// <summary>
    ///     Target sheet name; the sheet is found by name or created when missing
    /// </summary>
    [JsonProperty("sheetName")]
    public string SheetName { get; set; } = "Общие данные";

    /// <summary>
    ///     Sheet number used when the sheet has to be created
    /// </summary>
    [JsonProperty("sheetNumber")]
    public string SheetNumber { get; set; } = string.Empty;

    /// <summary>
    ///     Create the sheet when no sheet with the requested name exists
    /// </summary>
    [JsonProperty("createSheetIfMissing")]
    public bool CreateSheetIfMissing { get; set; } = true;

    /// <summary>
    ///     Table title drawn above the header row
    /// </summary>
    [JsonProperty("title")]
    public string Title { get; set; } = "Технико-экономические показатели";

    /// <summary>
    ///     Offset of the table's top-left corner from the sheet's left edge, mm
    /// </summary>
    [JsonProperty("positionX")]
    public double PositionX { get; set; } = 20;

    /// <summary>
    ///     Offset of the table's top-left corner from the sheet's top edge, mm
    /// </summary>
    [JsonProperty("positionY")]
    public double PositionY { get; set; } = 20;

    /// <summary>
    ///     Row height, mm
    /// </summary>
    [JsonProperty("rowHeight")]
    public double RowHeight { get; set; } = 8;

    /// <summary>
    ///     TextNoteType name for the title row (see get_document_styles)
    /// </summary>
    [JsonProperty("titleTextTypeName")]
    public string TitleTextTypeName { get; set; } = string.Empty;

    /// <summary>
    ///     TextNoteType name for the header row (see get_document_styles)
    /// </summary>
    [JsonProperty("headerTextTypeName")]
    public string HeaderTextTypeName { get; set; } = string.Empty;

    /// <summary>
    ///     TextNoteType name for data rows (see get_document_styles)
    /// </summary>
    [JsonProperty("bodyTextTypeName")]
    public string BodyTextTypeName { get; set; } = string.Empty;

    /// <summary>
    ///     Append per-level area rows
    /// </summary>
    [JsonProperty("includeLevels")]
    public bool IncludeLevels { get; set; } = true;

    /// <summary>
    ///     Append rows for rooms grouped by purpose (department)
    /// </summary>
    [JsonProperty("includeRoomsByPurpose")]
    public bool IncludeRoomsByPurpose { get; set; } = true;

    /// <summary>
    ///     Include unplaced rooms in TEP aggregation
    /// </summary>
    [JsonProperty("includeUnplacedRooms")]
    public bool IncludeUnplacedRooms { get; set; }

    /// <summary>
    ///     Include not enclosed rooms in TEP aggregation
    /// </summary>
    [JsonProperty("includeNotEnclosedRooms")]
    public bool IncludeNotEnclosedRooms { get; set; }
}

/// <summary>
///     One rendered table column (geometry replicated from the reference schedule)
/// </summary>
public class TepTableColumnInfo
{
    [JsonProperty("heading")]
    public string Heading { get; set; } = string.Empty;

    [JsonProperty("width")]
    public double Width { get; set; }

    [JsonProperty("horizontalAlignment")]
    public string HorizontalAlignment { get; set; } = "Left";

    /// <summary>
    ///     Semantic role of the column: Index, Name, Unit, Value, or Unknown
    /// </summary>
    [JsonProperty("role")]
    public string Role { get; set; } = "Unknown";
}

/// <summary>
///     Result of TEP table rendering
/// </summary>
public class TepTableRenderResult
{
    [JsonProperty("success")]
    public bool Success { get; set; }

    [JsonProperty("message")]
    public string Message { get; set; } = string.Empty;

    [JsonProperty("sheetId")]
    public long SheetId { get; set; }

    [JsonProperty("sheetUniqueId")]
    public string SheetUniqueId { get; set; } = string.Empty;

    [JsonProperty("sheetName")]
    public string SheetName { get; set; } = string.Empty;

    [JsonProperty("sheetNumber")]
    public string SheetNumber { get; set; } = string.Empty;

    [JsonProperty("sheetCreated")]
    public bool SheetCreated { get; set; }

    [JsonProperty("templateScheduleName")]
    public string TemplateScheduleName { get; set; } = string.Empty;

    [JsonProperty("templateScheduleUsed")]
    public bool TemplateScheduleUsed { get; set; }

    [JsonProperty("columns")]
    public List<TepTableColumnInfo> Columns { get; set; } = new List<TepTableColumnInfo>();

    [JsonProperty("rowCount")]
    public int RowCount { get; set; }

    [JsonProperty("titleTextType")]
    public string TitleTextType { get; set; } = string.Empty;

    [JsonProperty("headerTextType")]
    public string HeaderTextType { get; set; } = string.Empty;

    [JsonProperty("bodyTextType")]
    public string BodyTextType { get; set; } = string.Empty;

    [JsonProperty("textNoteIds")]
    public List<long> TextNoteIds { get; set; } = new List<long>();

    [JsonProperty("detailLineIds")]
    public List<long> DetailLineIds { get; set; } = new List<long>();

    /// <summary>
    ///     Units of rendered values: lengths mm, areas m2, volumes m3
    /// </summary>
    [JsonProperty("units")]
    public RevitMCPCommandSet.Models.DataExtraction.TepUnits Units { get; set; } =
        new RevitMCPCommandSet.Models.DataExtraction.TepUnits();

    [JsonProperty("executionTimeMs")]
    public long ExecutionTimeMs { get; set; }

    [JsonProperty("warnings")]
    public List<string> Warnings { get; set; } = new List<string>();
}
