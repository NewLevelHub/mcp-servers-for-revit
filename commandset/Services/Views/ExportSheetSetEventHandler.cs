using System.IO;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCPCommandSet.Models.Views;
using RevitMCPCommandSet.Utils;
using RevitMCPSDK.API.Interfaces;

namespace RevitMCPCommandSet.Services.Views;

/// <summary>
///     Выпуск комплекта (REV-173): reads which revisions sit on which sheet
///     (native API, nothing TypeScript can read through a generic parameter
///     call), and prints/exports the finished {sheetId, fileName} list
///     `utils/exportSheetSet.ts` already decided on.
///     <para>
///     DWG settings come from a named export setup already in the project —
///     never invented here, per the ticket's own «границы». A sheet that fails
///     to export is one row in the result, not a thrown exception that would
///     take the rest of the batch down with it.
///     </para>
/// </summary>
public class ExportSheetSetEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
{
    private ExportSheetSetInfo _info;
    private readonly ManualResetEvent _resetEvent = new ManualResetEvent(false);

    public object ResultInfo { get; private set; }
    public bool TaskCompleted { get; private set; }

    public void SetParameters(ExportSheetSetInfo info)
    {
        _info = info ?? new ExportSheetSetInfo();
        TaskCompleted = false;
        _resetEvent.Reset();
    }

    public bool WaitForCompletion(int timeoutMilliseconds = 120000)
    {
        return _resetEvent.WaitOne(timeoutMilliseconds);
    }

    public void Execute(UIApplication app)
    {
        try
        {
            var doc = app.ActiveUIDocument.Document;
            ResultInfo = string.Equals(_info.Action, "listRevisions", StringComparison.OrdinalIgnoreCase)
                ? ListRevisions(doc)
                : ExportSet(doc, _info);
        }
        catch (Exception ex)
        {
            ResultInfo = new ExportSheetSetResult { Success = false, Message = $"Error: {ex.Message}" };
        }
        finally
        {
            TaskCompleted = true;
            _resetEvent.Set();
        }
    }

    public string GetName() => "Export Sheet Set";

    private static SheetRevisionsResult ListRevisions(Document doc)
    {
        var result = new SheetRevisionsResult();

        var sheets = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>();
        foreach (var sheet in sheets)
        {
            var revisionIds = sheet.GetAllRevisionIds();
            if (revisionIds == null || revisionIds.Count == 0)
                continue;

            var entry = new SheetRevisionsEntry { SheetId = sheet.Id.GetIntValue() };
            foreach (var revisionId in revisionIds)
            {
                if (doc.GetElement(revisionId) is not Revision revision)
                    continue;

                entry.Revisions.Add(new RevisionRef
                {
                    SequenceNumber = revision.SequenceNumber,
                    Description = revision.Description ?? string.Empty
                });
            }

            entry.Revisions.Sort((a, b) => a.SequenceNumber.CompareTo(b.SequenceNumber));
            result.Sheets.Add(entry);
        }

        return result;
    }

    private static ExportSheetSetResult ExportSet(Document doc, ExportSheetSetInfo info)
    {
        var result = new ExportSheetSetResult { OutputDir = info.OutputDir };

        if (string.IsNullOrWhiteSpace(info.OutputDir) || !Directory.Exists(info.OutputDir))
        {
            result.Success = false;
            result.Message = $"Папка «{info.OutputDir}» не существует — создайте её заранее.";
            return result;
        }

        if (info.Items == null || info.Items.Count == 0)
        {
            result.Success = false;
            result.Message = "Список листов пуст — экспортировать нечего.";
            return result;
        }

        var format = (info.Format ?? "pdf").Trim().ToLowerInvariant();
        var wantsPdf = format == "pdf" || format == "both";
        var wantsDwg = format == "dwg" || format == "both";

        if (!wantsPdf && !wantsDwg)
        {
            result.Success = false;
            result.Message = $"Неизвестный format «{info.Format}» — pdf, dwg или both.";
            return result;
        }

#if REVIT2022_OR_GREATER
        if (wantsPdf)
        {
            // Nothing project-specific to resolve for PDF — Revit prints from the view's own
            // print setup. DWG is the half of this ticket that reads settings from the project.
        }
#else
        if (wantsPdf)
        {
            result.Success = false;
            result.Message = "PDF-экспорт через PDFExportOptions доступен с Revit 2022 — на этой версии не реализован.";
            return result;
        }
#endif

        DWGExportOptions dwgOptions = null;
        if (wantsDwg)
        {
            var setupNames = BaseExportOptions.GetPredefinedSetupNames(doc)?.ToList() ?? new List<string>();

            var chosenName = info.DwgSetupName;
            if (string.IsNullOrWhiteSpace(chosenName))
            {
                if (setupNames.Count == 1)
                {
                    chosenName = setupNames[0];
                }
                else
                {
                    result.Success = false;
                    result.Message = setupNames.Count == 0
                        ? "В проекте нет ни одной именованной настройки экспорта DWG — создайте её в Revit (Экспорт → Настройки → DWG)."
                        : "В проекте несколько настроек экспорта DWG — укажите dwgSetupName.";
                    result.AvailableDwgSetups = setupNames;
                    return result;
                }
            }
            else if (!setupNames.Any(name => string.Equals(name, chosenName, StringComparison.OrdinalIgnoreCase)))
            {
                result.Success = false;
                result.Message = $"Настройки экспорта DWG «{chosenName}» в проекте нет.";
                result.AvailableDwgSetups = setupNames;
                return result;
            }

            dwgOptions = DWGExportOptions.GetPredefinedOptions(doc, chosenName);
            result.DwgSetupUsed = chosenName;
        }

        foreach (var item in info.Items)
        {
            var itemResult = new SheetExportItemResult { SheetId = item.SheetId, FileName = item.FileName };

            try
            {
                var sheetElementId = RevitMCPCommandSet.Utils.ElementIdExtensions.FromLong(item.SheetId);
                if (doc.GetElement(sheetElementId) is not ViewSheet sheet)
                    throw new InvalidOperationException("Лист не найден — возможно, был удалён после отбора.");

                var viewIds = new List<ElementId> { sheet.Id };

#if REVIT2022_OR_GREATER
                if (wantsPdf)
                {
                    var pdfOptions = new PDFExportOptions
                    {
                        FileName = item.FileName,
                        Combine = true
                    };
                    doc.Export(info.OutputDir, viewIds, pdfOptions);
                    itemResult.PdfPath = Path.Combine(info.OutputDir, item.FileName + ".pdf");
                }
#endif

                if (wantsDwg)
                {
                    doc.Export(info.OutputDir, item.FileName, viewIds, dwgOptions);
                    itemResult.DwgPath = Path.Combine(info.OutputDir, item.FileName + ".dwg");
                }

                itemResult.Success = true;
            }
            catch (Exception ex)
            {
                // One bad sheet is one row, not a reason to lose the rest of the batch.
                itemResult.Success = false;
                itemResult.Error = ex.Message;
            }

            result.Results.Add(itemResult);
        }

        var succeeded = result.Results.Count(r => r.Success);
        result.Success = succeeded > 0;
        result.Message = $"Экспортировано {succeeded} из {result.Results.Count}.";
        return result;
    }
}
