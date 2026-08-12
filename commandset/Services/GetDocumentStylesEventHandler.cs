using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Utils;
using RevitMCPSDK.API.Interfaces;

namespace RevitMCPCommandSet.Services;

public class GetDocumentStylesEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
{
    public bool IncludeGraphicsStyles { get; set; }
    public DocumentStylesResult ResultInfo { get; private set; } = new();
    public bool TaskCompleted { get; private set; }
    private readonly ManualResetEvent _resetEvent = new(false);

    /// <summary>
    /// Reset wait state before ExternalEvent.Raise. Must be called from the command before RaiseAndWaitForCompletion.
    /// </summary>
    public void Prepare()
    {
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
            var doc = app.ActiveUIDocument?.Document
                ?? throw new InvalidOperationException("No active Revit document.");

            ResultInfo = new DocumentStylesResult
            {
                Success = true,
                Message = IncludeGraphicsStyles
                    ? "Document styles collected successfully."
                    : "Document styles collected successfully (raw graphicsStyles omitted; " +
                      "lineStyles holds the OST_Lines subcategories — pass includeGraphicsStyles=true for the full dump).",
                DimensionTypes = CollectDimensionTypes(doc),
                GridTypes = CollectGridTypes(doc),
                TextNoteTypes = CollectTextNoteTypes(doc),
                LinePatterns = CollectLinePatterns(doc),
                GraphicsStyles = IncludeGraphicsStyles
                    ? CollectGraphicsStyles(doc)
                    : new List<GraphicsStyleInfo>(),
                LineStyles = CollectLineStyles(doc),
                FilledRegionTypes = CollectFilledRegionTypes(doc),
                FillPatterns = CollectFillPatterns(doc),
                TitleBlocks = CollectTitleBlocks(doc)
            };
        }
        catch (Exception ex)
        {
            ResultInfo = new DocumentStylesResult
            {
                Success = false,
                Message = $"Failed to collect document styles: {ex.Message}"
            };
        }
        finally
        {
            TaskCompleted = true;
            _resetEvent.Set();
        }
    }

    public string GetName() => "Get Document Styles";

    private static List<DimensionTypeStyleInfo> CollectDimensionTypes(Document doc)
    {
        return new FilteredElementCollector(doc)
            .OfClass(typeof(DimensionType))
            .Cast<DimensionType>()
            .OrderBy(type => type.Name)
            .Select(type => new DimensionTypeStyleInfo
            {
                Id = type.Id.GetValue(),
                UniqueId = type.UniqueId,
                Name = type.Name,
                StyleType = type.StyleType.ToString()
            })
            .ToList();
    }

    private static List<NamedStyleInfo> CollectGridTypes(Document doc)
    {
        return new FilteredElementCollector(doc)
            .OfClass(typeof(GridType))
            .Cast<GridType>()
            .OrderBy(type => type.Name)
            .Select(type => new NamedStyleInfo
            {
                Id = type.Id.GetValue(),
                UniqueId = type.UniqueId,
                Name = type.Name
            })
            .ToList();
    }

    private static List<TextNoteTypeStyleInfo> CollectTextNoteTypes(Document doc)
    {
        return new FilteredElementCollector(doc)
            .OfClass(typeof(TextNoteType))
            .Cast<TextNoteType>()
            .OrderBy(type => type.Name)
            .Select(type =>
            {
                double? textHeightMm = null;
                var textSizeParam = type.get_Parameter(BuiltInParameter.TEXT_SIZE);
                if (textSizeParam != null && textSizeParam.HasValue)
                    textHeightMm = RevitUnitConversion.ToMillimeters(textSizeParam.AsDouble());

                var fontParam = type.get_Parameter(BuiltInParameter.TEXT_FONT);
                var font = fontParam?.AsString() ?? string.Empty;

                return new TextNoteTypeStyleInfo
                {
                    Id = type.Id.GetValue(),
                    UniqueId = type.UniqueId,
                    Name = type.Name,
                    TextHeightMm = textHeightMm,
                    Font = font
                };
            })
            .ToList();
    }

    private static List<NamedStyleInfo> CollectLinePatterns(Document doc)
    {
        return new FilteredElementCollector(doc)
            .OfClass(typeof(LinePatternElement))
            .Cast<LinePatternElement>()
            .OrderBy(pattern => pattern.Name)
            .Select(pattern => new NamedStyleInfo
            {
                Id = pattern.Id.GetValue(),
                UniqueId = pattern.UniqueId,
                Name = pattern.Name
            })
            .ToList();
    }

    private static List<GraphicsStyleInfo> CollectGraphicsStyles(Document doc)
    {
        return new FilteredElementCollector(doc)
            .OfClass(typeof(GraphicsStyle))
            .Cast<GraphicsStyle>()
            .Where(style => !string.IsNullOrWhiteSpace(style.Name))
            .OrderBy(style => style.Name)
            .Select(style => new GraphicsStyleInfo
            {
                Id = style.Id.GetValue(),
                UniqueId = style.UniqueId,
                Name = style.Name,
                GraphicsStyleType = style.GraphicsStyleType.ToString(),
                Category = style.GraphicsStyleCategory?.Name ?? string.Empty
            })
            .ToList();
    }

    /// <summary>
    ///     Line styles are subcategories of OST_Lines. graphicsStyles dumps every GraphicsStyle in
    ///     the document (thousands of them); this is the short list a detail actually draws with.
    /// </summary>
    private static List<LineStyleInfo> CollectLineStyles(Document doc)
    {
        var styles = new List<LineStyleInfo>();
        var lines = doc.Settings.Categories.get_Item(BuiltInCategory.OST_Lines);
        if (lines == null)
            return styles;

        foreach (Category subCategory in lines.SubCategories)
        {
            if (string.IsNullOrWhiteSpace(subCategory.Name))
                continue;

            var graphicsStyle = subCategory.GetGraphicsStyle(GraphicsStyleType.Projection);

            styles.Add(new LineStyleInfo
            {
                Id = graphicsStyle?.Id.GetValue() ?? subCategory.Id.GetValue(),
                UniqueId = graphicsStyle?.UniqueId ?? string.Empty,
                Name = subCategory.Name,
                LineWeight = subCategory.GetLineWeight(GraphicsStyleType.Projection),
                LinePatternName = ResolveLinePatternName(doc, subCategory),
                Color = FormatColor(subCategory.LineColor)
            });
        }

        return styles.OrderBy(style => style.Name).ToList();
    }

    private static string ResolveLinePatternName(Document doc, Category category)
    {
        var patternId = category.GetLinePatternId(GraphicsStyleType.Projection);
        if (patternId == null || patternId == ElementId.InvalidElementId)
            return string.Empty;

        // Solid is a built-in pattern with a negative id and no element behind it.
        if (patternId == LinePatternElement.GetSolidPatternId())
            return "Solid";

        return doc.GetElement(patternId) is LinePatternElement pattern ? pattern.Name : string.Empty;
    }

    private static List<FilledRegionTypeStyleInfo> CollectFilledRegionTypes(Document doc)
    {
        return new FilteredElementCollector(doc)
            .OfClass(typeof(FilledRegionType))
            .Cast<FilledRegionType>()
            .OrderBy(type => type.Name)
            .Select(type => new FilledRegionTypeStyleInfo
            {
                Id = type.Id.GetValue(),
                UniqueId = type.UniqueId,
                Name = type.Name,
                ForegroundPatternName = ResolveFillPatternName(doc, type.ForegroundPatternId),
                BackgroundPatternName = ResolveFillPatternName(doc, type.BackgroundPatternId),
                ForegroundColor = FormatColor(type.ForegroundPatternColor),
                IsMasking = type.IsMasking
            })
            .ToList();
    }

    private static List<FillPatternStyleInfo> CollectFillPatterns(Document doc)
    {
        return new FilteredElementCollector(doc)
            .OfClass(typeof(FillPatternElement))
            .Cast<FillPatternElement>()
            .Select(element =>
            {
                var pattern = element.GetFillPattern();
                return new FillPatternStyleInfo
                {
                    Id = element.Id.GetValue(),
                    UniqueId = element.UniqueId,
                    Name = element.Name,
                    Target = pattern?.Target.ToString() ?? string.Empty,
                    IsSolidFill = pattern?.IsSolidFill ?? false
                };
            })
            .OrderBy(pattern => pattern.Name)
            .ToList();
    }

    private static string ResolveFillPatternName(Document doc, ElementId patternId)
    {
        if (patternId == null || patternId == ElementId.InvalidElementId)
            return string.Empty;

        return doc.GetElement(patternId) is FillPatternElement pattern ? pattern.Name : string.Empty;
    }

    private static string FormatColor(Color color)
    {
        return color != null && color.IsValid
            ? $"#{color.Red:X2}{color.Green:X2}{color.Blue:X2}"
            : string.Empty;
    }

    private static List<TitleBlockStyleInfo> CollectTitleBlocks(Document doc)
    {
        return new FilteredElementCollector(doc)
            .OfCategory(BuiltInCategory.OST_TitleBlocks)
            .OfClass(typeof(FamilySymbol))
            .Cast<FamilySymbol>()
            .OrderBy(symbol => symbol.FamilyName)
            .ThenBy(symbol => symbol.Name)
            .Select(symbol => new TitleBlockStyleInfo
            {
                Id = symbol.Id.GetValue(),
                UniqueId = symbol.UniqueId,
                FamilyName = symbol.FamilyName,
                TypeName = symbol.Name
            })
            .ToList();
    }
}
