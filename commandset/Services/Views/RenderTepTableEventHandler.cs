using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
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
    /// <summary>Padding inside a cell so text does not sit on grid lines.</summary>
    private const double CellTextInsetMm = 3.5;
    /// <summary>
    /// Extra gap between the title band and the table grid (mm).
    /// Prevents the title TextNote from overlapping the header row.
    /// </summary>
    private const double TitleGapMm = 4;
    /// <summary>Extra width factor so Cyrillic strings do not wrap after measurement.</summary>
    private const double WidthSafetyFactor = 1.2;
    /// <summary>Extra mm added on top of measured single-line width.</summary>
    private const double WidthSafetyMm = 6;
    /// <summary>Default bottom stamp reserve when callers omit TitleBlockReserveBottom.</summary>
    private const double DefaultTitleBlockReserveBottomMm = 55;
    private const double MinTopMarginMm = 5;

    private static readonly Regex SpacerColumnHeading = new Regex(
        @"^\d+([.,]\d+)?\s*мм$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

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
            // Do not Reset here - SetParameters/Prepare already Reset before Raise.
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

            FitColumnWidths(
                doc,
                sheet,
                info,
                columns,
                rows,
                titleTypeId,
                headerTypeId,
                bodyTypeId,
                warnings);

            var rowHeightsMm = ComputeRowHeightsMm(
                doc,
                sheet,
                info,
                columns,
                rows,
                titleTypeId,
                headerTypeId,
                bodyTypeId,
                warnings);

            var overflow = FitTableToSheet(
                sheet,
                info,
                columns,
                rowHeightsMm,
                titleTypeId,
                doc,
                warnings);

            DrawTable(
                doc,
                sheet,
                info,
                columns,
                rows,
                rowHeightsMm,
                titleTypeId,
                headerTypeId,
                bodyTypeId,
                result,
                warnings);

            tx.Commit();

            result.Success = info.AllowOverflow || !overflow;
            result.Message = overflow && !info.AllowOverflow
                ? $"TEP table rendered on sheet '{result.SheetName}' but does not fit the printable area " +
                  "(needed height exceeds usable space after title-block reserve)."
                : $"Rendered TEP table with {rows.Count} rows and {columns.Count} columns on sheet '{result.SheetName}'.";
        }

        stopwatch.Stop();

        result.Columns = columns;
        result.RowCount = rows.Count;
        result.Units = tepData.Units;
        result.ExecutionTimeMs = stopwatch.ElapsedMilliseconds;
        result.Warnings = warnings;
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
            // Floors only — FitColumnWidths grows columns to fit the longest cell text.
            columns.Add(new TableColumn { Heading = "№ п/п", WidthMm = 18, Alignment = "Center" });
            columns.Add(new TableColumn { Heading = "Наименование", WidthMm = 90, Alignment = "Left" });
            columns.Add(new TableColumn { Heading = "Ед. изм.", WidthMm = 22, Alignment = "Center" });
            columns.Add(new TableColumn { Heading = "Кол-во", WidthMm = 30, Alignment = "Center" });
        }

        ClassifyColumns(columns, warnings);

        if (columns.Count == 0)
        {
            warnings.Add("No usable columns remained after filtering; default TEP columns are used.");
            columns.Add(new TableColumn { Heading = "№ п/п", WidthMm = 18, Alignment = "Center", Role = ColumnRole.Index });
            columns.Add(new TableColumn { Heading = "Наименование", WidthMm = 90, Alignment = "Left", Role = ColumnRole.Name });
            columns.Add(new TableColumn { Heading = "Ед. изм.", WidthMm = 22, Alignment = "Center", Role = ColumnRole.Unit });
            columns.Add(new TableColumn { Heading = "Кол-во", WidthMm = 30, Alignment = "Center", Role = ColumnRole.Value });
        }

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
        // Drop spacer / thickness columns (e.g. "8мм") before role mapping so they
        // cannot be forced into Name/Value fallbacks.
        var spacers = columns
            .Where(IsSpacerColumn)
            .ToList();
        if (spacers.Count > 0)
        {
            warnings.Add(
                $"Skipped spacer/template columns without TEP data: {string.Join(", ", spacers.Select(column => $"'{column.Heading}'"))}.");
            columns.RemoveAll(IsSpacerColumn);
        }

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

        if (columns.Count == 0 ||
            columns.All(column => column.Role != ColumnRole.Name) ||
            columns.All(column => column.Role != ColumnRole.Value))
        {
            warnings.Add(
                "Could not map reference columns to TEP data (name/value); some cells will remain empty.");
        }

        var unmapped = columns.Where(column => column.Role == ColumnRole.Unknown).ToList();
        if (unmapped.Count > 0)
        {
            warnings.Add(
                $"Skipped columns without TEP mapping: {string.Join(", ", unmapped.Select(column => $"'{column.Heading}'"))}.");
            columns.RemoveAll(column => column.Role == ColumnRole.Unknown);
        }
    }

    private static bool IsSpacerColumn(TableColumn column)
    {
        var heading = (column.Heading ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(heading))
            return true;
        if (column.WidthMm <= 0)
            return true;
        return SpacerColumnHeading.IsMatch(heading);
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

    /// <summary>
    /// Grow each column so headings and cell values fit on one line (plus cell inset),
    /// never narrower than Revit's TextNote minimum. Cap the total to the sheet width.
    /// </summary>
    private static void FitColumnWidths(
        Document doc,
        ViewSheet sheet,
        TepTableRenderInfo info,
        List<TepTableColumnInfo> columns,
        List<TableRow> rows,
        ElementId titleTypeId,
        ElementId headerTypeId,
        ElementId bodyTypeId,
        List<string> warnings)
    {
        if (columns.Count == 0)
            return;

        var padMm = CellTextInsetMm * 2 + WidthSafetyMm;
        var headerFloorMm = FeetToMm(TextNote.GetMinimumAllowedWidth(doc, headerTypeId)) + padMm;
        var bodyFloorMm = FeetToMm(TextNote.GetMinimumAllowedWidth(doc, bodyTypeId)) + padMm;
        var typeFloorMm = Math.Max(headerFloorMm, bodyFloorMm);

        for (var columnIndex = 0; columnIndex < columns.Count; columnIndex++)
        {
            var column = columns[columnIndex];
            var neededMm = Math.Max(column.Width, typeFloorMm);

            var headingWidth = MeasureSingleLineWidthMm(doc, sheet, headerTypeId, column.Heading);
            neededMm = Math.Max(neededMm, headingWidth + padMm);

            var dataIndex = 0;
            foreach (var row in rows)
            {
                if (row.IsGroupHeader)
                    continue;

                dataIndex++;
                var text = column.Role switch
                {
                    nameof(ColumnRole.Index) => dataIndex.ToString(CultureInfo.InvariantCulture),
                    nameof(ColumnRole.Name) => row.Name,
                    nameof(ColumnRole.Unit) => row.Unit,
                    nameof(ColumnRole.Value) => row.Value,
                    _ => string.Empty
                };

                if (string.IsNullOrWhiteSpace(text))
                    continue;

                neededMm = Math.Max(
                    neededMm,
                    MeasureSingleLineWidthMm(doc, sheet, bodyTypeId, text) + padMm);
            }

            column.Width = Math.Round(neededMm, 1);
        }

        // Keep narrow semantic columns from ballooning when measurement overestimates.
        foreach (var column in columns)
        {
            var cap = column.Role switch
            {
                nameof(ColumnRole.Index) => 35.0,
                nameof(ColumnRole.Unit) => 45.0,
                nameof(ColumnRole.Value) => 50.0,
                _ => double.PositiveInfinity
            };
            if (column.Width > cap)
                column.Width = cap;
        }

        // Group headers and the title span the full table — expand Name (or last) column if needed.
        var spanTexts = rows
            .Where(row => row.IsGroupHeader && !string.IsNullOrWhiteSpace(row.Name))
            .Select(row => row.Name)
            .ToList();
        if (!string.IsNullOrWhiteSpace(info.Title))
            spanTexts.Add(info.Title);

        var spanNeedMm = 0.0;
        foreach (var text in spanTexts)
        {
            var typeId = text == info.Title ? titleTypeId : headerTypeId;
            spanNeedMm = Math.Max(
                spanNeedMm,
                MeasureSingleLineWidthMm(doc, sheet, typeId, text) + padMm);
        }

        var totalWidth = columns.Sum(column => column.Width);
        if (spanNeedMm > totalWidth)
        {
            var expandTarget = columns.FirstOrDefault(column => column.Role == nameof(ColumnRole.Name))
                               ?? columns[columns.Count - 1];
            expandTarget.Width = Math.Round(expandTarget.Width + (spanNeedMm - totalWidth), 1);
            totalWidth = columns.Sum(column => column.Width);
        }

        var outline = sheet.Outline;
        if (outline == null)
            return;

        var rightMarginMm = Math.Max(info.PositionX, 20);
        var availableMm = FeetToMm(outline.Max.U - outline.Min.U) - info.PositionX - rightMarginMm;
        if (availableMm <= 0 || totalWidth <= availableMm)
            return;

        var excess = totalWidth - availableMm;
        var shrinkTarget = columns.FirstOrDefault(column => column.Role == nameof(ColumnRole.Name))
                           ?? columns.OrderByDescending(column => column.Width).First();
        var minAllowed = typeFloorMm;
        if (shrinkTarget.Width - excess >= minAllowed)
        {
            shrinkTarget.Width = Math.Round(shrinkTarget.Width - excess, 1);
            return;
        }

        // Proportional shrink, never below TextNote minimum.
        var shrinkable = columns.Sum(column => Math.Max(0, column.Width - minAllowed));
        if (shrinkable <= 0)
        {
            warnings.Add(
                "TEP table is wider than the sheet printable area; text may wrap or clip.");
            return;
        }

        var scale = Math.Min(1.0, (shrinkable - Math.Min(excess, shrinkable)) / shrinkable);
        foreach (var column in columns)
        {
            var slack = column.Width - minAllowed;
            if (slack <= 0)
                continue;
            column.Width = Math.Round(minAllowed + slack * scale, 1);
        }

        warnings.Add(
            "TEP column widths were reduced to fit the sheet; long names may wrap.");
    }

    /// <summary>
    /// Width needed to keep <paramref name="text"/> on a single line for the given text type.
    /// Prefers glyph estimate; optionally widens if a regenerated on-sheet probe still wraps.
    /// </summary>
    private static double MeasureSingleLineWidthMm(
        Document doc,
        View view,
        ElementId textTypeId,
        string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return 0;

        var normalized = NormalizeText(text);
        var minW = TextNote.GetMinimumAllowedWidth(doc, textTypeId);
        var maxW = TextNote.GetMaximumAllowedWidth(doc, textTypeId);
        var textSize = MmToFeet(GetTextSizeMm(doc, textTypeId));
        // TextNote bbox is taller than TEXT_SIZE; 2.8× avoids false "wrapped" positives.
        var singleLineLimit = textSize * 2.8;

        var widthFeet = Math.Min(
            Math.Max(EstimateTextWidthFeet(doc, textTypeId, normalized) * WidthSafetyFactor, minW),
            maxW);

        // Grow only while an on-sheet probe still reports multi-line height.
        for (var step = 0; step < 6; step++)
        {
            if (!TextWrapsAtWidth(doc, view, textTypeId, normalized, widthFeet, singleLineLimit))
                break;
            var next = Math.Min(widthFeet * 1.2, maxW);
            if (next <= widthFeet + 1e-9)
                break;
            widthFeet = next;
        }

        return FeetToMm(widthFeet);
    }

    private static bool TextWrapsAtWidth(
        Document doc,
        View view,
        ElementId textTypeId,
        string text,
        double widthFeet,
        double singleLineHeightLimitFeet)
    {
        TextNote probe = null;
        try
        {
            var options = new TextNoteOptions
            {
                TypeId = textTypeId,
                HorizontalAlignment = HorizontalTextAlignment.Left,
                VerticalAlignment = VerticalTextAlignment.Middle
            };

            // Must be inside the view outline — off-sheet probes often return a null bbox.
            var origin = GetProbeOrigin(view);
            probe = TextNote.Create(
                doc,
                view.Id,
                origin,
                Math.Max(widthFeet, TextNote.GetMinimumAllowedWidth(doc, textTypeId)),
                text,
                options);
            doc.Regenerate();

            var bbox = probe.get_BoundingBox(view);
            if (bbox == null)
                return false;

            var height = Math.Abs(bbox.Max.Y - bbox.Min.Y);
            return height > singleLineHeightLimitFeet;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (probe != null)
            {
                try { doc.Delete(probe.Id); }
                catch { /* ignore */ }
            }
        }
    }

    private static XYZ GetProbeOrigin(View view)
    {
        if (view is ViewSheet sheet && sheet.Outline != null)
        {
            var outline = sheet.Outline;
            return new XYZ(
                (outline.Min.U + outline.Max.U) / 2.0,
                (outline.Min.V + outline.Max.V) / 2.0,
                0);
        }

        return XYZ.Zero;
    }

    private static double EstimateTextWidthFeet(Document doc, ElementId textTypeId, string text)
    {
        var textType = doc.GetElement(textTypeId) as TextNoteType;
        var sizeParam = textType?.get_Parameter(BuiltInParameter.TEXT_SIZE);
        var textSize = sizeParam != null && sizeParam.AsDouble() > 0
            ? sizeParam.AsDouble()
            : MmToFeet(3.5);
        // Cyrillic glyphs are typically wider than Latin; 0.78 is a safe average for Arial.
        return Math.Max(text.Length, 1) * textSize * 0.78;
    }

    /// <summary>
    /// Per-row heights (mm): index 0 = header, then one entry per data/group row.
    /// Grows with wrapped text when the sheet forced column shrink.
    /// </summary>
    private static List<double> ComputeRowHeightsMm(
        Document doc,
        ViewSheet sheet,
        TepTableRenderInfo info,
        List<TepTableColumnInfo> columns,
        List<TableRow> rows,
        ElementId titleTypeId,
        ElementId headerTypeId,
        ElementId bodyTypeId,
        List<string> warnings)
    {
        var verticalPadMm = CellTextInsetMm * 2 + GetTextSizeMm(doc, bodyTypeId) * 0.35;
        var minBodyMm = Math.Max(
            GetTextSizeMm(doc, headerTypeId),
            GetTextSizeMm(doc, bodyTypeId)) + verticalPadMm;
        var requestedFloor = info.RowHeight > 0 ? info.RowHeight : 8;
        minBodyMm = Math.Max(minBodyMm, requestedFloor);

        var heights = new List<double>();

        // Header row
        var headerHeight = minBodyMm;
        for (var columnIndex = 0; columnIndex < columns.Count; columnIndex++)
        {
            headerHeight = Math.Max(
                headerHeight,
                MeasureTextHeightMm(
                    doc,
                    sheet,
                    headerTypeId,
                    columns[columnIndex].Heading,
                    columns[columnIndex].Width) + verticalPadMm);
        }
        heights.Add(Math.Round(headerHeight, 1));

        var dataIndex = 0;
        foreach (var row in rows)
        {
            var rowHeight = minBodyMm;
            if (row.IsGroupHeader)
            {
                var spanWidthMm = columns.Sum(column => column.Width);
                rowHeight = Math.Max(
                    rowHeight,
                    MeasureTextHeightMm(doc, sheet, headerTypeId, row.Name, spanWidthMm) + verticalPadMm);
            }
            else
            {
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

                    if (string.IsNullOrWhiteSpace(text))
                        continue;

                    rowHeight = Math.Max(
                        rowHeight,
                        MeasureTextHeightMm(doc, sheet, bodyTypeId, text, column.Width) + verticalPadMm);
                }
            }

            heights.Add(Math.Round(rowHeight, 1));
        }

        // Keep info.RowHeight as the typical/max body height for callers that read it back.
        info.RowHeight = heights.Count > 0 ? heights.Max() : minBodyMm;

        return heights;
    }

    /// <summary>
    /// Auto-fit table into the printable area (outline minus top offset and title-block reserve).
    /// Returns true when the table still overflows after adjustments.
    /// </summary>
    private static bool FitTableToSheet(
        ViewSheet sheet,
        TepTableRenderInfo info,
        List<TepTableColumnInfo> columns,
        List<double> rowHeightsMm,
        ElementId titleTypeId,
        Document doc,
        List<string> warnings)
    {
        var outline = sheet.Outline;
        if (outline == null || rowHeightsMm == null || rowHeightsMm.Count == 0)
            return false;

        var stampReserve = info.TitleBlockReserveBottom > 0
            ? info.TitleBlockReserveBottom
            : DefaultTitleBlockReserveBottomMm;
        var sheetHeightMm = FeetToMm(outline.Max.V - outline.Min.V);

        var titleBandMm = Math.Max(
            GetTextSizeMm(doc, titleTypeId) + CellTextInsetMm * 2,
            rowHeightsMm[0] * 1.15);
        var tableHeightMm = titleBandMm + TitleGapMm + rowHeightsMm.Sum();
        var availableMm = sheetHeightMm - info.PositionY - stampReserve;

        if (tableHeightMm > availableMm && info.PositionY > MinTopMarginMm)
        {
            var previousY = info.PositionY;
            info.PositionY = Math.Max(
                MinTopMarginMm,
                sheetHeightMm - stampReserve - tableHeightMm);
            if (info.PositionY < previousY - 0.01)
            {
                warnings.Add(
                    $"Reduced positionY from {previousY:0.##} to {info.PositionY:0.##} mm to clear the title-block zone.");
                availableMm = sheetHeightMm - info.PositionY - stampReserve;
                tableHeightMm = titleBandMm + TitleGapMm + rowHeightsMm.Sum();
            }
        }

        if (tableHeightMm > availableMm && rowHeightsMm.Count > 0)
        {
            var overheadMm = titleBandMm + TitleGapMm;
            var rowsBudget = Math.Max(availableMm - overheadMm, rowHeightsMm.Count * 4);
            var rowsTotal = rowHeightsMm.Sum();
            if (rowsTotal > rowsBudget && rowsTotal > 0)
            {
                var scale = rowsBudget / rowsTotal;
                var textFloor = Math.Max(GetTextSizeMm(doc, titleTypeId), 4);
                for (var i = 0; i < rowHeightsMm.Count; i++)
                    rowHeightsMm[i] = Math.Round(Math.Max(textFloor, rowHeightsMm[i] * scale), 1);

                warnings.Add(
                    $"Reduced row heights to fit printable height ({availableMm:0.#} mm usable after {stampReserve:0.#} mm stamp reserve).");
                titleBandMm = Math.Max(
                    GetTextSizeMm(doc, titleTypeId) + CellTextInsetMm * 2,
                    rowHeightsMm[0] * 1.15);
                tableHeightMm = titleBandMm + TitleGapMm + rowHeightsMm.Sum();
            }
        }

        if (tableHeightMm > availableMm + 0.5)
        {
            warnings.Add(
                $"TEP table height {tableHeightMm:0.#} mm exceeds usable printable area {availableMm:0.#} mm " +
                $"(sheet {sheetHeightMm:0.#} mm, positionY {info.PositionY:0.#} mm, stamp reserve {stampReserve:0.#} mm).");
            return true;
        }

        var tableWidthMm = columns.Sum(column => column.Width);
        var sheetWidthMm = FeetToMm(outline.Max.U - outline.Min.U);
        var availableWidthMm = sheetWidthMm - info.PositionX - Math.Max(info.PositionX, 20);
        if (tableWidthMm > availableWidthMm + 0.5)
        {
            warnings.Add(
                $"TEP table width {tableWidthMm:0.#} mm exceeds usable printable width {availableWidthMm:0.#} mm.");
            return true;
        }

        return false;
    }

    private static double GetTextSizeMm(Document doc, ElementId textTypeId)
    {
        var textType = doc.GetElement(textTypeId) as TextNoteType;
        var sizeParam = textType?.get_Parameter(BuiltInParameter.TEXT_SIZE);
        if (sizeParam == null || sizeParam.AsDouble() <= 0)
            return 3.5;
        return FeetToMm(sizeParam.AsDouble());
    }

    private static double MeasureTextHeightMm(
        Document doc,
        View view,
        ElementId textTypeId,
        string text,
        double cellWidthMm)
    {
        if (string.IsNullOrWhiteSpace(text))
            return GetTextSizeMm(doc, textTypeId);

        var normalized = NormalizeText(text);
        TextNote probe = null;
        try
        {
            var inset = CellTextInsetMm * 2;
            var availableMm = Math.Max(cellWidthMm - inset, CellTextInsetMm);
            var widthFeet = ClampTextWidth(doc, textTypeId, MmToFeet(availableMm));
            var options = new TextNoteOptions
            {
                TypeId = textTypeId,
                HorizontalAlignment = HorizontalTextAlignment.Left,
                VerticalAlignment = VerticalTextAlignment.Middle
            };

            probe = TextNote.Create(
                doc,
                view.Id,
                GetProbeOrigin(view),
                widthFeet,
                normalized,
                options);
            doc.Regenerate();

            var bbox = probe.get_BoundingBox(view);
            if (bbox == null)
                return GetTextSizeMm(doc, textTypeId);

            return FeetToMm(Math.Abs(bbox.Max.Y - bbox.Min.Y));
        }
        catch
        {
            return GetTextSizeMm(doc, textTypeId);
        }
        finally
        {
            if (probe != null)
            {
                try { doc.Delete(probe.Id); }
                catch { /* ignore */ }
            }
        }
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
        List<double> rowHeightsMm,
        ElementId titleTypeId,
        ElementId headerTypeId,
        ElementId bodyTypeId,
        TepTableRenderResult result,
        List<string> warnings)
    {
        var outline = sheet.Outline
            ?? throw new InvalidOperationException($"Sheet '{sheet.SheetNumber}' has no outline.");

        if (rowHeightsMm == null || rowHeightsMm.Count != rows.Count + 1)
            throw new InvalidOperationException("Row heights must include the header plus every data row.");

        var rowHeights = rowHeightsMm.Select(MmToFeet).ToList();
        var titleBandMm = Math.Max(
            GetTextSizeMm(doc, titleTypeId) + CellTextInsetMm * 2,
            rowHeightsMm[0] * 1.15);
        var titleBand = MmToFeet(titleBandMm);
        var titleGap = MmToFeet(TitleGapMm);
        var tableWidth = MmToFeet(columns.Sum(column => column.Width));
        var gridHeight = rowHeights.Sum();

        // PositionX/Y are offsets from the sheet outline (title-block outer box).
        // Defaults leave room for the typical double-line border of the title block.
        var left = outline.Min.U + MmToFeet(info.PositionX);
        var top = outline.Max.V - MmToFeet(info.PositionY);

        var tableHeight = titleBand + titleGap + gridHeight;

        var stampReserve = info.TitleBlockReserveBottom > 0
            ? info.TitleBlockReserveBottom
            : DefaultTitleBlockReserveBottomMm;
        var printableBottom = outline.Min.V + MmToFeet(stampReserve);
        if (left + tableWidth > outline.Max.U || top - tableHeight < printableBottom)
            warnings.Add(
                "The TEP table does not fully fit within the printable area at the requested position " +
                $"(title-block reserve {stampReserve:0.#} mm).");

        var columnLefts = new List<double> { left };
        foreach (var column in columns)
            columnLefts.Add(columnLefts[columnLefts.Count - 1] + MmToFeet(column.Width));
        var right = columnLefts[columnLefts.Count - 1];

        // Title sits in its own band fully above the grid (Left, so long titles
        // do not spill past the left sheet border when centered).
        var titleNote = CreateCellText(
            doc,
            sheet,
            info.Title ?? string.Empty,
            titleTypeId,
            left,
            right,
            top,
            titleBand,
            "Left");
        if (titleNote != null)
            result.TextNoteIds.Add(titleNote.Id.GetValue());

        var gridTop = top - titleBand - titleGap;
        var gridBottom = gridTop - gridHeight;

        // Horizontal grid lines at cumulative row boundaries
        var yCursor = gridTop;
        var line = doc.Create.NewDetailCurve(
            sheet,
            Line.CreateBound(new XYZ(left, yCursor, 0), new XYZ(right, yCursor, 0)));
        result.DetailLineIds.Add(line.Id.GetValue());
        foreach (var height in rowHeights)
        {
            yCursor -= height;
            line = doc.Create.NewDetailCurve(
                sheet,
                Line.CreateBound(new XYZ(left, yCursor, 0), new XYZ(right, yCursor, 0)));
            result.DetailLineIds.Add(line.Id.GetValue());
        }

        // Vertical grid lines
        foreach (var x in columnLefts)
        {
            line = doc.Create.NewDetailCurve(
                sheet,
                Line.CreateBound(new XYZ(x, gridBottom, 0), new XYZ(x, gridTop, 0)));
            result.DetailLineIds.Add(line.Id.GetValue());
        }

        // Header row
        var headerTop = gridTop;
        var headerHeight = rowHeights[0];
        for (var columnIndex = 0; columnIndex < columns.Count; columnIndex++)
        {
            var note = CreateCellText(
                doc,
                sheet,
                columns[columnIndex].Heading,
                headerTypeId,
                columnLefts[columnIndex],
                columnLefts[columnIndex + 1],
                headerTop,
                headerHeight,
                "Center");
            if (note != null)
                result.TextNoteIds.Add(note.Id.GetValue());
        }

        // Data rows
        var dataIndex = 0;
        var cellTop = gridTop - headerHeight;
        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            var row = rows[rowIndex];
            var rowHeight = rowHeights[rowIndex + 1];

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
                cellTop -= rowHeight;
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

            cellTop -= rowHeight;
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
        // Stay inside Revit's allowed range. Narrow cells (15–20 mm) may still
        // need minWidth; CellTextInsetMm keeps glyphs off the grid lines.
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

    private static double FeetToMm(double feet) => feet * MmPerFoot;
}
