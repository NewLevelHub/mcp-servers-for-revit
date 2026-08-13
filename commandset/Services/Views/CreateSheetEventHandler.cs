using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCPCommandSet.Models.Views;
using RevitMCPCommandSet.Utils;
using RevitMCPSDK.API.Interfaces;

namespace RevitMCPCommandSet.Services.Views;

public class CreateSheetEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
{
    private SheetCreationInfo _sheetInfo;
    private readonly ManualResetEvent _resetEvent = new ManualResetEvent(false);

    public SheetCreationResult ResultInfo { get; private set; } = new SheetCreationResult();
    public bool TaskCompleted { get; private set; }

    public void SetParameters(SheetCreationInfo sheetInfo)
    {
        _sheetInfo = sheetInfo ?? throw new ArgumentNullException(nameof(sheetInfo));
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
        var warnings = new List<string>();

        try
        {
            var doc = app.ActiveUIDocument.Document;
            ViewSheet sheet;
            FamilySymbol titleBlock;

            using (var tx = new Transaction(doc, "Create Sheet"))
            {
                tx.Start();

                titleBlock = ResolveTitleBlock(doc, _sheetInfo, warnings);
                sheet = ViewSheet.Create(doc, titleBlock.Id);

                if (!string.IsNullOrWhiteSpace(_sheetInfo.SheetNumber))
                    sheet.SheetNumber = GetUniqueSheetNumber(doc, _sheetInfo.SheetNumber.Trim());

                if (!string.IsNullOrWhiteSpace(_sheetInfo.SheetName))
                    sheet.Name = _sheetInfo.SheetName.Trim();

                ApplyRevisions(doc, sheet, _sheetInfo, warnings);

                doc.Regenerate();
                ApplySheetFormat(doc, sheet, _sheetInfo.SheetFormat, warnings);

                tx.Commit();
            }

            ResultInfo = new SheetCreationResult
            {
                Success = true,
                Message = $"Successfully created sheet '{sheet.SheetNumber}'",
                SheetId = GetElementIdValue(sheet.Id),
                SheetUniqueId = sheet.UniqueId,
                SheetNumber = sheet.SheetNumber,
                SheetName = sheet.Name,
                TitleBlockTypeId = GetElementIdValue(titleBlock.Id),
                TitleBlockFamilyName = titleBlock.FamilyName,
                TitleBlockTypeName = titleBlock.Name,
                Warnings = warnings
            };
        }
        catch (Exception ex)
        {
            ResultInfo = new SheetCreationResult
            {
                Success = false,
                Message = $"Error creating sheet: {ex.Message}",
                Warnings = warnings
            };
        }
        finally
        {
            TaskCompleted = true;
            _resetEvent.Set();
        }
    }

    private static FamilySymbol ResolveTitleBlock(
        Document doc,
        SheetCreationInfo info,
        List<string> warnings)
    {
        if (info.TitleBlockTypeId > 0)
        {
            var symbol = doc.GetElement(new ElementId(info.TitleBlockTypeId)) as FamilySymbol;
            if (symbol != null && symbol.Category?.Id.GetIntValue() == (int)BuiltInCategory.OST_TitleBlocks)
                return EnsureTitleBlockActive(doc, symbol);

            warnings.Add($"Title block type id '{info.TitleBlockTypeId}' was not found.");
        }

        if (!string.IsNullOrWhiteSpace(info.TitleBlockFamilyName) ||
            !string.IsNullOrWhiteSpace(info.TitleBlockTypeName))
        {
            var symbol = FindTitleBlockByName(doc, info.TitleBlockFamilyName, info.TitleBlockTypeName);
            if (symbol != null)
                return EnsureTitleBlockActive(doc, symbol);

            warnings.Add(
                $"Title block '{info.TitleBlockFamilyName}' / '{info.TitleBlockTypeName}' was not found.");
        }

        var symbols = new FilteredElementCollector(doc)
            .OfCategory(BuiltInCategory.OST_TitleBlocks)
            .OfClass(typeof(FamilySymbol))
            .Cast<FamilySymbol>()
            .ToList();

        if (symbols.Count == 0)
            throw new InvalidOperationException("No title block types are loaded in the project.");

        // Prefer основная надпись (working sheets), never ADSK_Титул / cover sheets.
        var preferred = symbols.FirstOrDefault(IsWorkingStampTitleBlock);

        if (preferred == null)
        {
            preferred = symbols.FirstOrDefault(s =>
                s.FamilyName.IndexOf("Титул", StringComparison.OrdinalIgnoreCase) < 0
                && s.FamilyName.IndexOf("Начальный", StringComparison.OrdinalIgnoreCase) < 0);
        }

        preferred ??= symbols.First();

        warnings.Add($"Using default title block '{preferred.FamilyName} - {preferred.Name}'.");
        return EnsureTitleBlockActive(doc, preferred);
    }

    private static FamilySymbol FindTitleBlockByName(
        Document doc,
        string familyName,
        string typeName)
    {
        var symbols = new FilteredElementCollector(doc)
            .OfCategory(BuiltInCategory.OST_TitleBlocks)
            .OfClass(typeof(FamilySymbol))
            .Cast<FamilySymbol>()
            .ToList();

        // A caller that names only the format ("А3А") means a working sheet, but that type
        // name also exists in ADSK_Титул (the cover page). Match основная надпись first so a
        // format-only request never lands the drawing on the title page.
        foreach (var symbol in symbols.OrderByDescending(IsWorkingStampTitleBlock))
        {
            var familyMatches = string.IsNullOrWhiteSpace(familyName) ||
                                symbol.FamilyName.Equals(familyName.Trim(), StringComparison.OrdinalIgnoreCase);
            var typeMatches = string.IsNullOrWhiteSpace(typeName) ||
                              symbol.Name.Equals(typeName.Trim(), StringComparison.OrdinalIgnoreCase);

            if (familyMatches && typeMatches)
                return symbol;
        }

        return null;
    }

    private static bool IsWorkingStampTitleBlock(FamilySymbol symbol)
    {
        var family = symbol?.FamilyName ?? string.Empty;
        return family.IndexOf("ОсновнаяНадпись", StringComparison.OrdinalIgnoreCase) >= 0
               || family.IndexOf("основная надпись", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static FamilySymbol EnsureTitleBlockActive(Document doc, FamilySymbol symbol)
    {
        if (!symbol.IsActive)
        {
            symbol.Activate();
            doc.Regenerate();
        }

        return symbol;
    }

    /// <summary>
    ///     Set the paper format on the sheet's title block instance. ADSK «ОсновнаяНадпись»
    ///     draws one family at every size and picks the frame from the integer «Формат А»
    ///     parameter (3 = A3), so the format cannot be chosen by type name.
    /// </summary>
    private static void ApplySheetFormat(
        Document doc,
        ViewSheet sheet,
        string format,
        List<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(format))
            return;

        var digits = new string(format.Where(char.IsDigit).ToArray());
        if (!int.TryParse(digits, out var formatNumber))
        {
            warnings.Add($"Unrecognized sheetFormat '{format}'; sheet left with the default format.");
            return;
        }

        var titleBlock = new FilteredElementCollector(doc, sheet.Id)
            .OfCategory(BuiltInCategory.OST_TitleBlocks)
            .WhereElementIsNotElementType()
            .FirstElement();

        if (titleBlock == null)
        {
            warnings.Add("Sheet has no title block instance; sheetFormat was ignored.");
            return;
        }

        var formatParam = titleBlock.LookupParameter("Формат А");
        if (formatParam == null || formatParam.IsReadOnly)
        {
            warnings.Add(
                $"Title block '{titleBlock.Name}' has no writable 'Формат А' parameter; " +
                $"sheetFormat '{format}' was ignored. Pick a title block type of the needed size instead.");
            return;
        }

        formatParam.Set(formatNumber);
        doc.Regenerate();
    }

    private static void ApplyRevisions(
        Document doc,
        ViewSheet sheet,
        SheetCreationInfo info,
        List<string> warnings)
    {
        if (info.RevisionIds == null || info.RevisionIds.Count == 0)
            return;

        warnings.Add(
            "revisionIds are accepted but automatic revision assignment on new sheets is not supported yet.");
    }

    private static string GetUniqueSheetNumber(Document doc, string requestedNumber)
    {
        var baseNumber = requestedNumber;
        var suffix = 1;
        var number = baseNumber;

        while (SheetNumberExists(doc, number))
            number = $"{baseNumber}-{suffix++}";

        return number;
    }

    private static bool SheetNumberExists(Document doc, string sheetNumber)
    {
        return new FilteredElementCollector(doc)
            .OfClass(typeof(ViewSheet))
            .Cast<ViewSheet>()
            .Any(sheet => sheet.SheetNumber.Equals(sheetNumber, StringComparison.OrdinalIgnoreCase));
    }

    private static long GetElementIdValue(ElementId elementId)
    {
#if REVIT2024_OR_GREATER
        return elementId.Value;
#else
        return elementId.IntegerValue;
#endif
    }

    public string GetName() => "Create Sheet";
}
