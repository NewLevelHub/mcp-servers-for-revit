using System.Diagnostics;
using System.Globalization;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCPCommandSet.Models.DataExtraction;
using RevitMCPCommandSet.Models.Views;
using RevitMCPCommandSet.Services.DataExtraction;
using RevitMCPCommandSet.Utils;
using RevitMCPSDK.API.Interfaces;

namespace RevitMCPCommandSet.Services.Views;

public class RenderTepTableEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
{
    private const double MmPerFoot = 304.8;
    private const double CellTextInsetMm = 1.5;

    private TepTableRenderInfo _info;
    private readonly ManualResetEvent _resetEvent = new ManualResetEvent(false);

    public TepTableRenderResult ResultInfo { get; private set; } = new TepTableRenderResult();
    public bool TaskCompleted { get; private set; }

    public void SetParameters(TepTableRenderInfo info)
    {
        _info = info ?? throw new ArgumentNullException(nameof(info));
        TaskCompleted = false;
        _resetEvent.Reset();
    }

    public bool WaitForCompletion(int timeoutMilliseconds = 10000)
    {
        _resetEvent.Reset();
        return _resetEvent.WaitOne(timeoutMilliseconds);
    }

    public void Execute(UIApplication app)
    {
        try
        {
            var doc = app.ActiveUIDocument.Document;
            ResultInfo = Render(doc, _info);
        }
        catch (Exception ex)
        {
            ResultInfo = new TepTableRenderResult
            {
                Success = false,
                Message = $"Error rendering TEP table: {ex.Message}"
            };
        }
        finally
        {
            TaskCompleted = true;
            _resetEvent.Set();
        }
    }

    public static TepTableRenderResult Render(Document doc, TepTableRenderInfo info)
    {
        if (doc == null)
            throw new ArgumentNullException(nameof(doc));
        if (info == null)
            throw new ArgumentNullException(nameof(info));

        var stopwatch = Stopwatch.StartNew();
        var warnings = new List<string>();
        var result = new TepTableRenderResult();

        var tepData = ExportTepDataEventHandler.Compute(
            doc,
            info.IncludeUnplacedRooms,
            info.IncludeNotEnclosedRooms);

        if (!tepData.Success)
            throw new InvalidOperationException(tepData.Message ?? "TEP data export failed.");

        if (tepData.TotalRooms == 0)
            warnings.Add("No placed rooms were found; the TEP table contains zero values.");

        var columns = ResolveColumns(doc, info, result, warnings);
        var rows = BuildRows(tepData, info);

        using (var tx = new Transaction(doc, "Render TEP Table"))
        {
            tx.Start();

            var sheet = ResolveOrCreateSheet(doc, info, result, warnings);
            var titleTypeId = ResolveTextNoteType(doc, info.TitleTextTypeName, "title", warnings);
            var headerTypeId = ResolveTextNoteType(doc, info.HeaderTextTypeName, "header", warnings);
            var bodyTypeId = ResolveTextNoteType(doc, info.BodyTextTypeName, "body", warnings);

            result.TitleTextType = GetTextTypeName(doc, titleTypeId);
            result.HeaderTextType = GetTextTypeName(doc, headerTypeId);
            result.BodyTextType = GetTextTypeName(doc, bodyTypeId);

            DrawTable(
                doc,
                sheet,
                info,
                columns,
                rows,
                titleTypeId,
                headerTypeId,
                bodyTypeId,
                result,
                warnings);

            tx.Commit();
        }

        stopwatch.Stop();

        result.Success = true;
        result.Columns = columns;
        result.RowCount = rows.Count;
        result.ExecutionTimeMs = stopwatch.ElapsedMilliseconds;
        result.Warnings = warnings;
        result.Message =
            $"Rendered TEP table with {rows.Count} rows and {columns.Count} columns on sheet '{result.SheetName}'.";
        return result;
    }

    public string GetName() => "Render TEP Table";

    private enum ColumnRole
    {
        Unknown,
        Index,
        Name,
        Unit,
        Value
    }

    private sealed class TableColumn
    {
        public string Heading;
        public double WidthMm;
        public string Alignment = "Left";
        public ColumnRole Role = ColumnRole.Unknown;
    }

    private sealed class TableRow
    {
        public bool IsGroupHeader;
        public string Name;
        public string Unit;
        public string Value;
    }

    private static List<TepTableColumnInfo> ResolveColumns(
        Document doc,
        TepTableRenderInfo info,
        TepTableRenderResult result,
        List<string> warnings)
    {
        var columns = new List<TableColumn>();
        result.TemplateScheduleName = info.TemplateScheduleName ?? string.Empty;

        if (!string.IsNullOrWhiteSpace(info.TemplateScheduleName))
        {
            var definition = GetScheduleDefinitionEventHandler.Compute(
                doc,
                scheduleName: info.TemplateScheduleName);

            if (definition.Success)
            {
                foreach (var field in definition.Fields)
                {
                    if (field.IsHidden)
                        continue;

                    columns.Add(new TableColumn
                    {
                        Heading = string.IsNullOrWhiteSpace(field.Heading)
                            ? field.ParameterName
                            : field.Heading,
                        WidthMm = field.Width > 0 ? field.Width : 30,
                        Alignment = field.HorizontalAlignment
                    });
                }

                if (columns.Count == 0)
                {
                    warnings.Add(
                        $"Reference schedule '{info.TemplateScheduleName}' has no visible fields; default TEP columns are used.");
                }
                else
                {
                    result.TemplateScheduleUsed = true;
                }
            }
            else
            {
                warnings.Add(
                    $"Reference schedule '{info.TemplateScheduleName}' was not found; default TEP columns are used.");
            }
        }

        if (columns.Count == 0)
        {
            columns.Add(new TableColumn { Heading = "№ п/п", WidthMm = 15, Alignment = "Center" });
            columns.Add(new TableColumn { Heading = "Наименование", WidthMm = 90, Alignment = "Left" });
            columns.Add(new TableColumn { Heading = "Ед. изм.", WidthMm = 20, Alignment = "Center" });
            columns.Add(new TableColumn { Heading = "Кол-во", WidthMm = 30, Alignment = "Center" });
        }

        ClassifyColumns(columns, warnings);

        return columns
            .Select(column => new TepTableColumnInfo
            {
                Heading = column.Heading,
                Width = Math.Round(column.WidthMm, 2),
                HorizontalAlignment = column.Alignment,
                Role = column.Role.ToString()
            })
            .ToList();
    }

    private static void ClassifyColumns(List<TableColumn> columns, List<string> warnings)
    {
        foreach (var column in columns)
        {
            var heading = (column.Heading ?? string.Empty).ToLowerInvariant();

            if (heading.Contains("№") || heading.Contains("п/п") || heading.Contains("поз"))
                column.Role = ColumnRole.Index;
            else if (heading.Contains("ед") && heading.Contains("изм"))
                column.Role = ColumnRole.Unit;
            else if (heading.Contains("наимен") || heading.Contains("показатели") || heading.Contains("помещен"))
                column.Role = ColumnRole.Name;
            else if (heading.Contains("значен") || heading.Contains("кол") ||
                     heading.Contains("показатель") || heading.Contains("площад") ||
                     heading.Contains("объ") || heading.Contains("%"))
                column.Role = ColumnRole.Value;
        }

        if (columns.All(column => column.Role != ColumnRole.Name))
        {
            var candidate = columns.FirstOrDefault(column => column.Role == ColumnRole.Unknown);
            if (candidate != null)
                candidate.Role = ColumnRole.Name;
        }

        if (columns.All(column => column.Role != ColumnRole.Value))
        {
            var candidate = columns.LastOrDefault(column => column.Role == ColumnRole.Unknown);
            if (candidate != null)
                candidate.Role = ColumnRole.Value;
        }

        if (columns.All(column => column.Role != ColumnRole.Name) ||
            columns.All(column => column.Role != ColumnRole.Value))
        {
            warnings.Add(
                "Could not map reference columns to TEP data (name/value); some cells will remain empty.");
        }

        var unmapped = columns.Where(column => column.Role == ColumnRole.Unknown).ToList();
        if (unmapped.Count > 0)
        {
            warnings.Add(
                $"Columns without TEP mapping remain empty: {string.Join(", ", unmapped.Select(column => $"'{column.Heading}'"))}.");
        }
    }

    private static List<TableRow> BuildRows(ExportTepDataResult tepData, TepTableRenderInfo info)
    {
        var rows = new List<TableRow>
        {
            new TableRow { Name = "Площадь застройки", Unit = "м²", Value = FormatNumber(tepData.BuildingFootprintArea) },
            new TableRow { Name = "Общая площадь здания", Unit = "м²", Value = FormatNumber(tepData.TotalArea) },
            new TableRow { Name = "Строительный объём", Unit = "м³", Value = FormatNumber(tepData.TotalVolume) },
            new TableRow { Name = "Этажность", Unit = "эт.", Value = tepData.StoreyCount.ToString(CultureInfo.InvariantCulture) },
            new TableRow { Name = "Количество помещений", Unit = "шт.", Value = tepData.TotalRooms.ToString(CultureInfo.InvariantCulture) }
        };

        if (info.IncludeLevels && tepData.Levels.Count > 0)
        {
            rows.Add(new TableRow { IsGroupHeader = true, Name = "Показатели по этажам" });
            foreach (var level in tepData.Levels)
            {
                rows.Add(new TableRow
                {
                    Name = $"Площадь этажа «{level.LevelName}»",
                    Unit = "м²",
                    Value = FormatNumber(level.Area)
                });
            }
        }

        if (info.IncludeRoomsByPurpose && tepData.RoomsByPurpose.Count > 0)
        {
            rows.Add(new TableRow { IsGroupHeader = true, Name = "Площади по назначению" });
            foreach (var purpose in tepData.RoomsByPurpose)
            {
                rows.Add(new TableRow
                {
                    Name = purpose.Purpose,
                    Unit = "м²",
                    Value = FormatNumber(purpose.Area)
                });
            }
        }

        return rows;
    }

    private static ViewSheet ResolveOrCreateSheet(
        Document doc,
        TepTableRenderInfo info,
        TepTableRenderResult result,
        List<string> warnings)
    {
        var sheetName = string.IsNullOrWhiteSpace(info.SheetName) ? "Общие данные" : info.SheetName.Trim();

        var sheet = new FilteredElementCollector(doc)
            .OfClass(typeof(ViewSheet))
            .Cast<ViewSheet>()
            .Where(candidate => !candidate.IsPlaceholder)
            .FirstOrDefault(candidate =>
                candidate.Name.Equals(sheetName, StringComparison.OrdinalIgnoreCase));

        if (sheet == null)
        {
            if (!info.CreateSheetIfMissing)
                throw new InvalidOperationException(
                    $"Sheet named '{sheetName}' was not found and createSheetIfMissing is false.");

            sheet = CreateSheet(doc, sheetName, info.SheetNumber, warnings);
            result.SheetCreated = true;
        }

        result.SheetId = sheet.Id.GetValue();
        result.SheetUniqueId = sheet.UniqueId;
        result.SheetName = sheet.Name;
        result.SheetNumber = sheet.SheetNumber;
        return sheet;
    }

    private static ViewSheet CreateSheet(
        Document doc,
        string sheetName,
        string sheetNumber,
        List<string> warnings)
    {
        var titleBlock = new FilteredElementCollector(doc)
            .OfCategory(BuiltInCategory.OST_TitleBlocks)
            .OfClass(typeof(FamilySymbol))
            .Cast<FamilySymbol>()
            .FirstOrDefault();

        ViewSheet sheet;
        if (titleBlock != null)
        {
            if (!titleBlock.IsActive)
            {
                titleBlock.Activate();
                doc.Regenerate();
            }

            sheet = ViewSheet.Create(doc, titleBlock.Id);
            warnings.Add(
                $"Sheet '{sheetName}' was created with title block '{titleBlock.FamilyName} - {titleBlock.Name}'.");
        }
        else
        {
            sheet = ViewSheet.Create(doc, ElementId.InvalidElementId);
            warnings.Add($"Sheet '{sheetName}' was created without a title block (none loaded in the project).");
        }

        sheet.Name = sheetName;
        if (!string.IsNullOrWhiteSpace(sheetNumber))
            sheet.SheetNumber = GetUniqueSheetNumber(doc, sheet, sheetNumber.Trim());

        return sheet;
    }

    private static string GetUniqueSheetNumber(Document doc, ViewSheet ownSheet, string requestedNumber)
    {
        var existingNumbers = new FilteredElementCollector(doc)
            .OfClass(typeof(ViewSheet))
            .Cast<ViewSheet>()
            .Where(sheet => sheet.Id != ownSheet.Id)
            .Select(sheet => sheet.SheetNumber)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var number = requestedNumber;
        var suffix = 1;
        while (existingNumbers.Contains(number))
            number = $"{requestedNumber}-{suffix++}";

        return number;
    }

    private static ElementId ResolveTextNoteType(
        Document doc,
        string typeName,
        string roleLabel,
        List<string> warnings)
    {
        if (!string.IsNullOrWhiteSpace(typeName))
        {
            var byName = new FilteredElementCollector(doc)
                .OfClass(typeof(TextNoteType))
                .Cast<TextNoteType>()
                .FirstOrDefault(type =>
                    type.Name.Equals(typeName.Trim(), StringComparison.OrdinalIgnoreCase));

            if (byName != null)
                return byName.Id;

            warnings.Add(
                $"Text note type '{typeName}' for the {roleLabel} was not found; the default text type is used.");
        }

        var defaultTypeId = doc.GetDefaultElementTypeId(ElementTypeGroup.TextNoteType);
        if (defaultTypeId != ElementId.InvalidElementId && doc.GetElement(defaultTypeId) is TextNoteType)
            return defaultTypeId;

        var firstType = new FilteredElementCollector(doc)
            .OfClass(typeof(TextNoteType))
            .Cast<TextNoteType>()
            .FirstOrDefault();

        if (firstType == null)
            throw new InvalidOperationException("The project has no text note types to render the table with.");

        return firstType.Id;
    }

    private static string GetTextTypeName(Document doc, ElementId typeId)
    {
        return (doc.GetElement(typeId) as TextNoteType)?.Name ?? string.Empty;
    }

    private static void DrawTable(
        Document doc,
        ViewSheet sheet,
        TepTableRenderInfo info,
        List<TepTableColumnInfo> columns,
        List<TableRow> rows,
        ElementId titleTypeId,
        ElementId headerTypeId,
        ElementId bodyTypeId,
        TepTableRenderResult result,
        List<string> warnings)
    {
        var outline = sheet.Outline
            ?? throw new InvalidOperationException($"Sheet '{sheet.SheetNumber}' has no outline.");

        var rowHeight = MmToFeet(info.RowHeight > 0 ? info.RowHeight : 8);
        var tableWidth = MmToFeet(columns.Sum(column => column.Width));

        var left = outline.Min.U + MmToFeet(info.PositionX);
        var top = outline.Max.V - MmToFeet(info.PositionY);

        // 1 title row + 1 header row + data rows
        var totalRows = rows.Count + 1;
        var tableHeight = rowHeight * totalRows + rowHeight; // extra row height for the title

        if (left + tableWidth > outline.Max.U || top - tableHeight < outline.Min.V)
            warnings.Add("The TEP table does not fully fit within the sheet outline at the requested position.");

        var columnLefts = new List<double> { left };
        foreach (var column in columns)
            columnLefts.Add(columnLefts[columnLefts.Count - 1] + MmToFeet(column.Width));
        var right = columnLefts[columnLefts.Count - 1];

        // Title above the grid
        var titleNote = CreateCellText(
            doc,
            sheet,
            info.Title ?? string.Empty,
            titleTypeId,
            left,
            right,
            top,
            rowHeight,
            "Center");
        if (titleNote != null)
            result.TextNoteIds.Add(titleNote.Id.GetValue());

        var gridTop = top - rowHeight;
        var gridBottom = gridTop - rowHeight * totalRows;

        // Horizontal grid lines
        for (var rowIndex = 0; rowIndex <= totalRows; rowIndex++)
        {
            var y = gridTop - rowHeight * rowIndex;
            var line = doc.Create.NewDetailCurve(
                sheet,
                Line.CreateBound(new XYZ(left, y, 0), new XYZ(right, y, 0)));
            result.DetailLineIds.Add(line.Id.GetValue());
        }

        // Vertical grid lines
        foreach (var x in columnLefts)
        {
            var line = doc.Create.NewDetailCurve(
                sheet,
                Line.CreateBound(new XYZ(x, gridBottom, 0), new XYZ(x, gridTop, 0)));
            result.DetailLineIds.Add(line.Id.GetValue());
        }

        // Header row
        for (var columnIndex = 0; columnIndex < columns.Count; columnIndex++)
        {
            var note = CreateCellText(
                doc,
                sheet,
                columns[columnIndex].Heading,
                headerTypeId,
                columnLefts[columnIndex],
                columnLefts[columnIndex + 1],
                gridTop,
                rowHeight,
                "Center");
            if (note != null)
                result.TextNoteIds.Add(note.Id.GetValue());
        }

        // Data rows
        var dataIndex = 0;
        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            var row = rows[rowIndex];
            var cellTop = gridTop - rowHeight * (rowIndex + 1);

            if (row.IsGroupHeader)
            {
                var note = CreateCellText(
                    doc,
                    sheet,
                    row.Name,
                    headerTypeId,
                    left,
                    right,
                    cellTop,
                    rowHeight,
                    "Center");
                if (note != null)
                    result.TextNoteIds.Add(note.Id.GetValue());
                continue;
            }

            dataIndex++;
            for (var columnIndex = 0; columnIndex < columns.Count; columnIndex++)
            {
                var column = columns[columnIndex];
                var text = column.Role switch
                {
                    nameof(ColumnRole.Index) => dataIndex.ToString(CultureInfo.InvariantCulture),
                    nameof(ColumnRole.Name) => row.Name,
                    nameof(ColumnRole.Unit) => row.Unit,
                    nameof(ColumnRole.Value) => row.Value,
                    _ => string.Empty
                };

                if (string.IsNullOrEmpty(text))
                    continue;

                var note = CreateCellText(
                    doc,
                    sheet,
                    text,
                    bodyTypeId,
                    columnLefts[columnIndex],
                    columnLefts[columnIndex + 1],
                    cellTop,
                    rowHeight,
                    column.HorizontalAlignment);
                if (note != null)
                    result.TextNoteIds.Add(note.Id.GetValue());
            }
        }
    }

    private static TextNote CreateCellText(
        Document doc,
        ViewSheet sheet,
        string text,
        ElementId textTypeId,
        double cellLeft,
        double cellRight,
        double cellTop,
        double rowHeight,
        string alignment)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var inset = MmToFeet(CellTextInsetMm);
        var availableWidth = Math.Max(cellRight - cellLeft - inset * 2, inset);
        var width = ClampTextWidth(doc, textTypeId, availableWidth);

        // TextNote position is the alignment point: left/center/right edge of the text box
        var horizontalAlignment = ParseAlignment(alignment);
        var x = horizontalAlignment switch
        {
            HorizontalTextAlignment.Center => (cellLeft + cellRight) / 2,
            HorizontalTextAlignment.Right => cellRight - inset,
            _ => cellLeft + inset
        };

        var options = new TextNoteOptions
        {
            TypeId = textTypeId,
            HorizontalAlignment = horizontalAlignment,
            VerticalAlignment = VerticalTextAlignment.Middle
        };

        var position = new XYZ(x, cellTop - rowHeight / 2, 0);
        return TextNote.Create(doc, sheet.Id, position, width, NormalizeText(text), options);
    }

    private static double ClampTextWidth(Document doc, ElementId textTypeId, double requestedWidth)
    {
        var minWidth = TextNote.GetMinimumAllowedWidth(doc, textTypeId);
        var maxWidth = TextNote.GetMaximumAllowedWidth(doc, textTypeId);
        return Math.Min(Math.Max(requestedWidth, minWidth), maxWidth);
    }

    private static HorizontalTextAlignment ParseAlignment(string alignment)
    {
        return alignment?.Trim().ToLowerInvariant() switch
        {
            "center" => HorizontalTextAlignment.Center,
            "right" => HorizontalTextAlignment.Right,
            _ => HorizontalTextAlignment.Left
        };
    }

    private static string NormalizeText(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var symbol in text)
            builder.Append(char.IsControl(symbol) ? ' ' : symbol);
        return builder.ToString().Trim();
    }

    private static string FormatNumber(double value)
    {
        return Math.Round(value, 2, MidpointRounding.AwayFromZero)
            .ToString("0.##", CultureInfo.InvariantCulture);
    }

    private static double MmToFeet(double millimeters) => millimeters / MmPerFoot;
}
