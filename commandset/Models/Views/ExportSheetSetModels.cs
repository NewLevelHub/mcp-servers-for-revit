using Newtonsoft.Json;

namespace RevitMCPCommandSet.Models.Views;

/// <summary>
///     One call, two actions (REV-173): <c>listRevisions</c> answers what
///     TypeScript cannot see on its own — which revisions sit on which sheet —
///     so <c>utils/exportSheetSet.ts</c> can decide "по ревизии" and fill the
///     <c>{revision}</c> placeholder before a single file is written.
///     <c>export</c> takes the finished {sheetId, fileName} list that decision
///     produced and prints/exports it.
/// </summary>
public class ExportSheetSetInfo
{
    [JsonProperty("action")]
    public string Action { get; set; } = "export";

    /// <summary>pdf | dwg | both. Only read for action=export.</summary>
    [JsonProperty("format")]
    public string Format { get; set; } = "pdf";

    [JsonProperty("outputDir")]
    public string OutputDir { get; set; } = string.Empty;

    /// <summary>Named DWG export setup from the project — required when the format needs DWG and the project has more than one.</summary>
    [JsonProperty("dwgSetupName")]
    public string DwgSetupName { get; set; } = string.Empty;

    [JsonProperty("items")]
    public List<ExportSheetItem> Items { get; set; } = new List<ExportSheetItem>();
}

public class ExportSheetItem
{
    [JsonProperty("sheetId")]
    public int SheetId { get; set; }

    /// <summary>Already resolved by exportSheetSet.ts — sanitised, template-filled, no extension.</summary>
    [JsonProperty("fileName")]
    public string FileName { get; set; } = string.Empty;
}

public class SheetRevisionsResult
{
    [JsonProperty("success")]
    public bool Success { get; set; } = true;

    [JsonProperty("message")]
    public string Message { get; set; } = string.Empty;

    [JsonProperty("sheets")]
    public List<SheetRevisionsEntry> Sheets { get; set; } = new List<SheetRevisionsEntry>();
}

public class SheetRevisionsEntry
{
    [JsonProperty("sheetId")]
    public int SheetId { get; set; }

    /// <summary>Every revision shown on this sheet, ascending by sequence number.</summary>
    [JsonProperty("revisions")]
    public List<RevisionRef> Revisions { get; set; } = new List<RevisionRef>();
}

public class RevisionRef
{
    [JsonProperty("sequenceNumber")]
    public int SequenceNumber { get; set; }

    [JsonProperty("description")]
    public string Description { get; set; } = string.Empty;
}

public class ExportSheetSetResult
{
    [JsonProperty("success")]
    public bool Success { get; set; }

    [JsonProperty("message")]
    public string Message { get; set; } = string.Empty;

    [JsonProperty("outputDir")]
    public string OutputDir { get; set; } = string.Empty;

    [JsonProperty("dwgSetupUsed")]
    public string DwgSetupUsed { get; set; } = string.Empty;

    [JsonProperty("results")]
    public List<SheetExportItemResult> Results { get; set; } = new List<SheetExportItemResult>();

    /// <summary>Filled only when DWG was asked for and dwgSetupName was ambiguous or missing — never invented.</summary>
    [JsonProperty("availableDwgSetups")]
    public List<string> AvailableDwgSetups { get; set; } = new List<string>();
}

public class SheetExportItemResult
{
    [JsonProperty("sheetId")]
    public int SheetId { get; set; }

    [JsonProperty("fileName")]
    public string FileName { get; set; } = string.Empty;

    [JsonProperty("pdfPath")]
    public string PdfPath { get; set; } = string.Empty;

    [JsonProperty("dwgPath")]
    public string DwgPath { get; set; } = string.Empty;

    [JsonProperty("success")]
    public bool Success { get; set; }

    /// <summary>Set only when this one sheet failed — a bad sheet does not stop the rest of the batch.</summary>
    [JsonProperty("error")]
    public string Error { get; set; } = string.Empty;
}
