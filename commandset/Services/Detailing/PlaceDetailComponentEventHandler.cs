using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Models.Detailing;
using RevitMCPCommandSet.Utils;
using RevitMCPSDK.API.Interfaces;

namespace RevitMCPCommandSet.Services.Detailing;

public class PlaceDetailComponentEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
{
    private const int MaxListedTypes = 20;

    private DetailComponentPlacementInfo _info;
    private readonly ManualResetEvent _resetEvent = new ManualResetEvent(false);

    public DetailComponentPlacementResult ResultInfo { get; private set; } = new DetailComponentPlacementResult();
    public bool TaskCompleted { get; private set; }

    public void SetParameters(DetailComponentPlacementInfo info)
    {
        _info = info ?? throw new ArgumentNullException(nameof(info));
        TaskCompleted = false;
        _resetEvent.Reset();
    }

    public bool WaitForCompletion(int timeoutMilliseconds = 60000)
    {
        // Do not Reset here — SetParameters already Reset; resetting after a fast
        // Execute can clear the signal and hang until timeout.
        return _resetEvent.WaitOne(timeoutMilliseconds);
    }

    public void Execute(UIApplication app)
    {
        try
        {
            var doc = app.ActiveUIDocument.Document;
            ResultInfo = Place(doc, _info);
        }
        catch (Exception ex)
        {
            ResultInfo = new DetailComponentPlacementResult
            {
                Success = false,
                Message = $"Error placing detail components: {ex.Message}"
            };
        }
        finally
        {
            TaskCompleted = true;
            _resetEvent.Set();
        }
    }

    public string GetName() => "Place Detail Component";

    public static DetailComponentPlacementResult Place(Document doc, DetailComponentPlacementInfo info)
    {
        if (doc == null)
            throw new ArgumentNullException(nameof(doc));
        if (info == null)
            throw new ArgumentNullException(nameof(info));
        if (info.Items == null || info.Items.Count == 0)
            throw new ArgumentException("At least one detail component item is required.");

        var warnings = new List<string>();
        var result = new DetailComponentPlacementResult();

        var view = ResolveView(doc, info)
            ?? throw new ArgumentException("Target view was not found. Provide viewId, viewUniqueId, or viewName.");

        result.ViewId = view.Id.GetValue();
        result.ViewName = view.Name;

        var symbols = CollectDetailComponentTypes(doc);
        var anyTypeMissing = false;

        using (var tx = new Transaction(doc, "Place Detail Components"))
        {
            tx.Start();

            foreach (var item in info.Items)
            {
                var placedItem = new DetailComponentPlacedItem
                {
                    FamilyName = item.FamilyName ?? string.Empty,
                    TypeName = item.TypeName ?? string.Empty
                };
                result.Items.Add(placedItem);

                var symbol = ResolveSymbol(symbols, item);
                if (symbol == null)
                {
                    anyTypeMissing = true;
                    placedItem.Warning =
                        $"Detail component type '{item.FamilyName}' / '{item.TypeName}' was not found in the project.";
                    warnings.Add(placedItem.Warning);
                    continue;
                }

                if (item.Point == null)
                {
                    placedItem.Warning = "Placement point is required.";
                    warnings.Add(placedItem.Warning);
                    continue;
                }

                try
                {
                    if (!symbol.IsActive)
                    {
                        symbol.Activate();
                        doc.Regenerate();
                    }

                    var instance = CreateInstance(doc, view, symbol, item);
                    placedItem.Placed = true;
                    placedItem.ElementId = instance.Id.GetValue();
                    placedItem.ElementUniqueId = instance.UniqueId;
                    placedItem.FamilyName = symbol.FamilyName;
                    placedItem.TypeName = symbol.Name;
                }
                catch (Exception ex)
                {
                    placedItem.Warning =
                        $"Failed to place '{symbol.FamilyName}: {symbol.Name}': {ex.Message}";
                    warnings.Add(placedItem.Warning);
                }
            }

            tx.Commit();
        }

        if (anyTypeMissing)
        {
            result.AvailableTypes = symbols
                .Select(symbol => $"{symbol.FamilyName}: {symbol.Name}")
                .OrderBy(name => name)
                .Take(MaxListedTypes)
                .ToList();

            if (symbols.Count == 0)
                warnings.Add("The project has no detail component families loaded; load detail item families first.");
        }

        result.PlacedCount = result.Items.Count(item => item.Placed);
        result.Success = true;
        result.Message =
            $"Placed {result.PlacedCount} of {result.Items.Count} detail components on view '{view.Name}'.";
        result.Warnings = warnings;
        return result;
    }

    private static FamilyInstance CreateInstance(
        Document doc,
        View view,
        FamilySymbol symbol,
        DetailComponentItemInfo item)
    {
        var point = JZPoint.ToXYZ(item.Point);

        if (item.EndPoint != null)
        {
            var line = Line.CreateBound(point, JZPoint.ToXYZ(item.EndPoint));
            return doc.Create.NewFamilyInstance(line, symbol, view);
        }

        var instance = doc.Create.NewFamilyInstance(point, symbol, view);

        if (Math.Abs(item.Rotation) > 1e-9)
        {
            var axis = Line.CreateBound(point, point + view.ViewDirection);
            ElementTransformUtils.RotateElement(
                doc,
                instance.Id,
                axis,
                item.Rotation * Math.PI / 180.0);
        }

        return instance;
    }

    private static List<FamilySymbol> CollectDetailComponentTypes(Document doc)
    {
        return new FilteredElementCollector(doc)
            .OfCategory(BuiltInCategory.OST_DetailComponents)
            .OfClass(typeof(FamilySymbol))
            .Cast<FamilySymbol>()
            .ToList();
    }

    private static FamilySymbol ResolveSymbol(
        IReadOnlyList<FamilySymbol> symbols,
        DetailComponentItemInfo item)
    {
        if (string.IsNullOrWhiteSpace(item.FamilyName) && string.IsNullOrWhiteSpace(item.TypeName))
            return null;

        return symbols.FirstOrDefault(symbol =>
        {
            var familyMatches = string.IsNullOrWhiteSpace(item.FamilyName) ||
                                symbol.FamilyName.Equals(item.FamilyName.Trim(), StringComparison.OrdinalIgnoreCase);
            var typeMatches = string.IsNullOrWhiteSpace(item.TypeName) ||
                              symbol.Name.Equals(item.TypeName.Trim(), StringComparison.OrdinalIgnoreCase);
            return familyMatches && typeMatches;
        });
    }

    private static View ResolveView(Document doc, DetailComponentPlacementInfo info)
    {
        if (!string.IsNullOrWhiteSpace(info.ViewUniqueId))
        {
            if (doc.GetElement(info.ViewUniqueId.Trim()) is View byUniqueId && !byUniqueId.IsTemplate)
                return byUniqueId;
        }

        if (info.ViewId > 0)
        {
            if (doc.GetElement(RevitMCPCommandSet.Utils.ElementIdExtensions.FromLong(info.ViewId)) is View byId && !byId.IsTemplate)
                return byId;
        }

        if (!string.IsNullOrWhiteSpace(info.ViewName))
        {
            return new FilteredElementCollector(doc)
                .OfClass(typeof(View))
                .Cast<View>()
                .Where(view => !view.IsTemplate && !(view is ViewSheet))
                .FirstOrDefault(view =>
                    view.Name.Equals(info.ViewName.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        return null;
    }
}
