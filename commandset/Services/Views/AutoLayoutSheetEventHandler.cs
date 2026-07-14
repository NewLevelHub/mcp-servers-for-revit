using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCPCommandSet.Models.Views;
using RevitMCPCommandSet.Utils;
using RevitMCPSDK.API.Interfaces;

namespace RevitMCPCommandSet.Services.Views;

public class AutoLayoutSheetEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
{
    private const double MmPerFoot = 304.8;
    private const double GeometryTolerance = 1e-6;
    private const int MaxPackingStepsPerItem = 1000;

    private AutoLayoutSheetInfo _info;
    private readonly ManualResetEvent _resetEvent = new ManualResetEvent(false);

    public AutoLayoutSheetResult ResultInfo { get; private set; } = new AutoLayoutSheetResult();
    public bool TaskCompleted { get; private set; }

    public void SetParameters(AutoLayoutSheetInfo info)
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
            ResultInfo = Layout(doc, _info);
        }
        catch (Exception ex)
        {
            ResultInfo = new AutoLayoutSheetResult
            {
                Success = false,
                Message = $"Error during automatic sheet layout: {ex.Message}"
            };
        }
        finally
        {
            TaskCompleted = true;
            _resetEvent.Set();
        }
    }

    public string GetName() => "Auto Layout Sheet";

    public static AutoLayoutSheetResult Layout(Document doc, AutoLayoutSheetInfo info)
    {
        if (doc == null)
            throw new ArgumentNullException(nameof(doc));
        if (info == null)
            throw new ArgumentNullException(nameof(info));
        if (info.Items == null || info.Items.Count == 0)
            throw new ArgumentException("At least one view or schedule item is required.");

        var stopwatch = Stopwatch.StartNew();
        var warnings = new List<string>();
        var result = new AutoLayoutSheetResult();

        using (var tx = new Transaction(doc, "Auto Layout Sheet"))
        {
            tx.Start();

            var sheet = ResolveOrCreateSheet(doc, info, result, warnings);
            var outline = sheet.Outline
                ?? throw new InvalidOperationException($"Sheet '{sheet.SheetNumber}' has no outline.");

            var usable = new RectFt(
                outline.Min.U + MmToFeet(info.MarginLeft),
                outline.Min.V + MmToFeet(info.MarginBottom + info.TitleBlockReserveBottom),
                outline.Max.U - MmToFeet(info.MarginRight),
                outline.Max.V - MmToFeet(info.MarginTop));

            if (usable.Width <= 0 || usable.Height <= 0)
                throw new InvalidOperationException(
                    "Usable layout area is empty; reduce margins or the title block reserve.");

            result.UsableWidth = Math.Round(FeetToMm(usable.Width), 2);
            result.UsableHeight = Math.Round(FeetToMm(usable.Height), 2);

            var obstacles = info.AvoidExisting
                ? CollectExistingOutlines(doc, sheet)
                : new List<RectFt>();

            var pending = CreateAndMeasure(doc, sheet, usable, info, warnings);

            var ordered = OrderPending(pending, info.Order, warnings);
            var packer = new ShelfPacker(usable, MmToFeet(Math.Max(info.Spacing, 0)), obstacles);

            foreach (var element in ordered)
            {
                if (!element.Item.Placed)
                    continue;

                var target = packer.Place(element.WidthFt, element.HeightFt);
                if (target == null)
                {
                    SkipElement(doc, element, "Does not fit into the remaining usable area.");
                    continue;
                }

                MoveToTarget(doc, element, target);
                element.Item.X = Math.Round(FeetToMm(target.MinX - outline.Min.U), 2);
                element.Item.Y = Math.Round(FeetToMm(target.MinY - outline.Min.V), 2);
                if (element.PlacedElement != null)
                {
                    element.Item.ElementId = element.PlacedElement.Id.GetValue();
                    element.Item.ElementUniqueId = element.PlacedElement.UniqueId;
                }
            }

            tx.Commit();

            result.Items = pending.Select(element => element.Item).ToList();
        }

        stopwatch.Stop();

        result.PlacedCount = result.Items.Count(item => item.Placed);
        result.SkippedCount = result.Items.Count - result.PlacedCount;
        result.AllPlaced = result.SkippedCount == 0 && result.PlacedCount > 0;
        result.PartialSuccess = result.PlacedCount > 0 && result.SkippedCount > 0;
        result.Warnings = warnings;
        result.ExecutionTimeMs = stopwatch.ElapsedMilliseconds;

        // Soft skips (view not found) keep Success=true for the negative-test contract.
        // Hard skips (oversized / already on sheet without dependent / pack fail) with
        // zero placements set Success=false so acceptance does not treat empty sheets as OK.
        var hardSkip = result.Items.Any(item =>
            !item.Placed &&
            !string.IsNullOrEmpty(item.Warning) &&
            item.Warning.IndexOf("was not found", StringComparison.OrdinalIgnoreCase) < 0);

        result.Success = result.AllPlaced ||
                         result.PartialSuccess ||
                         (!hardSkip && result.PlacedCount == 0);

        if (result.PlacedCount == 0 && hardSkip)
        {
            result.Success = false;
            result.Message =
                $"Auto layout placed 0 of {result.Items.Count} items on sheet '{result.SheetNumber} - {result.SheetName}'. " +
                $"Usable area {result.UsableWidth:0.##}×{result.UsableHeight:0.##} mm.";
        }
        else
        {
            result.Message =
                $"Auto layout placed {result.PlacedCount} of {result.Items.Count} items on sheet '{result.SheetNumber} - {result.SheetName}'.";
            if (result.PartialSuccess)
                result.Message += " Partial success: some items were skipped.";
        }

        return result;
    }

    private sealed class RectFt
    {
        public RectFt(double minX, double minY, double maxX, double maxY)
        {
            MinX = minX;
            MinY = minY;
            MaxX = maxX;
            MaxY = maxY;
        }

        public double MinX { get; }
        public double MinY { get; }
        public double MaxX { get; }
        public double MaxY { get; }
        public double Width => MaxX - MinX;
        public double Height => MaxY - MinY;

        public bool Intersects(RectFt other)
        {
            return MinX < other.MaxX - GeometryTolerance &&
                   other.MinX < MaxX - GeometryTolerance &&
                   MinY < other.MaxY - GeometryTolerance &&
                   other.MinY < MaxY - GeometryTolerance;
        }
    }

    private sealed class PendingElement
    {
        public AutoLayoutPlacedItem Item { get; set; }
        public Element PlacedElement { get; set; }
        public bool IsSchedule { get; set; }
        public double WidthFt { get; set; }
        public double HeightFt { get; set; }
        public RectFt MeasuredRect { get; set; }
    }

    /// <summary>
    ///     Row-based (shelf) packing with obstacle avoidance. Rows fill left to right from the
    ///     top of the usable area; a blocked candidate shifts right past the obstacle, an
    ///     overflowing row wraps below the tallest element placed in it.
    /// </summary>
    private sealed class ShelfPacker
    {
        private readonly RectFt _usable;
        private readonly double _gap;
        private readonly List<RectFt> _occupied;

        private double _rowTopY;
        private double _rowX;
        private double _rowMaxHeight;

        public ShelfPacker(RectFt usable, double gap, List<RectFt> occupied)
        {
            _usable = usable;
            _gap = gap;
            _occupied = occupied;
            _rowTopY = usable.MaxY;
            _rowX = usable.MinX;
        }

        public RectFt Place(double width, double height)
        {
            if (width > _usable.Width + GeometryTolerance || height > _usable.Height + GeometryTolerance)
                return null;

            for (var step = 0; step < MaxPackingStepsPerItem; step++)
            {
                if (_rowX + width > _usable.MaxX + GeometryTolerance)
                {
                    if (!WrapRow(height))
                        return null;
                    continue;
                }

                if (_rowTopY - height < _usable.MinY - GeometryTolerance)
                    return null;

                var candidate = new RectFt(_rowX, _rowTopY - height, _rowX + width, _rowTopY);
                var blocking = _occupied.FirstOrDefault(rect => rect.Intersects(candidate));
                if (blocking != null)
                {
                    _rowX = blocking.MaxX + _gap;
                    continue;
                }

                _occupied.Add(candidate);
                _rowX = candidate.MaxX + _gap;
                _rowMaxHeight = Math.Max(_rowMaxHeight, height);
                return candidate;
            }

            return null;
        }

        private bool WrapRow(double nextHeight)
        {
            // Guarantee downward progress even when the row was fully blocked by obstacles.
            var stepDown = _rowMaxHeight > 0 ? _rowMaxHeight + _gap : Math.Max(_gap, MmToFeet(5));
            _rowTopY -= stepDown;
            _rowX = _usable.MinX;
            _rowMaxHeight = 0;
            return _rowTopY - nextHeight >= _usable.MinY - GeometryTolerance;
        }
    }

    private static List<PendingElement> CreateAndMeasure(
        Document doc,
        ViewSheet sheet,
        RectFt usable,
        AutoLayoutSheetInfo info,
        List<string> warnings)
    {
        var pending = new List<PendingElement>();
        var tempPoint = new XYZ((usable.MinX + usable.MaxX) / 2, (usable.MinY + usable.MaxY) / 2, 0);

        foreach (var itemInfo in info.Items)
        {
            var element = new PendingElement
            {
                Item = new AutoLayoutPlacedItem
                {
                    ViewId = itemInfo.ViewId,
                    ViewName = itemInfo.ViewName ?? string.Empty
                }
            };
            pending.Add(element);

            var view = ResolveView(doc, itemInfo);
            if (view == null)
            {
                MarkSkipped(element, BuildViewNotFoundMessage(itemInfo), warnings);
                continue;
            }

            element.Item.ViewId = view.Id.GetValue();
            element.Item.ViewName = view.Name;

            try
            {
                if (view is ViewSchedule schedule)
                {
                    element.IsSchedule = true;
                    element.Item.PlacementType = "schedule";

                    if (IsScheduleAlreadyPlaced(doc, sheet, schedule))
                    {
                        MarkSkipped(element, $"Schedule '{schedule.Name}' is already placed on this sheet.", warnings);
                        continue;
                    }

                    element.PlacedElement = ScheduleSheetInstance.Create(doc, sheet.Id, schedule.Id, tempPoint);
                }
                else
                {
                    element.Item.PlacementType = "viewport";

                    if (!Viewport.CanAddViewToSheet(doc, sheet.Id, view.Id))
                    {
                        var owningSheet = FindSheetContainingView(doc, view.Id);
                        var owningLabel = owningSheet != null
                            ? $"'{owningSheet.SheetNumber} - {owningSheet.Name}'"
                            : "another sheet";

                        if (info.CreateDependentViewIfNeeded)
                        {
                            var dependent = TryCreateDependentView(doc, view, warnings);
                            if (dependent != null && Viewport.CanAddViewToSheet(doc, sheet.Id, dependent.Id))
                            {
                                warnings.Add(
                                    $"View '{view.Name}' is already on sheet {owningLabel}; " +
                                    $"created dependent '{dependent.Name}' for placement.");
                                view = dependent;
                                element.Item.ViewId = view.Id.GetValue();
                                element.Item.ViewName = view.Name;
                            }
                            else
                            {
                                MarkSkipped(
                                    element,
                                    $"View '{element.Item.ViewName}' cannot be placed on this sheet " +
                                    $"(already on sheet {owningLabel} or unsupported view type; dependent view was not created).",
                                    warnings);
                                continue;
                            }
                        }
                        else
                        {
                            MarkSkipped(
                                element,
                                $"View '{view.Name}' cannot be placed on this sheet " +
                                $"(already on sheet {owningLabel}; set createDependentViewIfNeeded=true to duplicate as dependent).",
                                warnings);
                            continue;
                        }
                    }

                    element.PlacedElement = Viewport.Create(doc, sheet.Id, view.Id, tempPoint);
                }

                element.Item.Placed = true;
            }
            catch (Exception ex)
            {
                MarkSkipped(element, $"Failed to place '{view.Name}': {ex.Message}", warnings);
            }
        }

        doc.Regenerate();

        foreach (var element in pending)
        {
            if (!element.Item.Placed)
                continue;

            var measured = MeasureOnSheet(element.PlacedElement, sheet);
            if (measured == null || measured.Width <= 0 || measured.Height <= 0)
            {
                SkipElement(doc, element, "Element has no measurable extents on the sheet.");
                warnings.Add($"'{element.Item.ViewName}' has no measurable extents and was skipped.");
                continue;
            }

            element.MeasuredRect = measured;
            element.WidthFt = measured.Width;
            element.HeightFt = measured.Height;
            element.Item.Width = Math.Round(FeetToMm(measured.Width), 2);
            element.Item.Height = Math.Round(FeetToMm(measured.Height), 2);

            if (measured.Width > usable.Width + GeometryTolerance ||
                measured.Height > usable.Height + GeometryTolerance)
            {
                SkipElement(
                    doc,
                    element,
                    $"Element is larger ({element.Item.Width}×{element.Item.Height} mm) than the usable area.");
                warnings.Add(
                    $"'{element.Item.ViewName}' is larger ({element.Item.Width:0.##}×{element.Item.Height:0.##} mm) " +
                    $"than the usable area ({FeetToMm(usable.Width):0.##}×{FeetToMm(usable.Height):0.##} mm) and was skipped; " +
                    "reduce the view scale, filter the schedule, or use a bigger sheet.");
            }
        }

        return pending;
    }

    private static ViewSheet FindSheetContainingView(Document doc, ElementId viewId)
    {
        return new FilteredElementCollector(doc)
            .OfClass(typeof(Viewport))
            .Cast<Viewport>()
            .Where(viewport => viewport.ViewId == viewId)
            .Select(viewport => doc.GetElement(viewport.OwnerViewId) as ViewSheet)
            .FirstOrDefault(sheet => sheet != null);
    }

    private static View TryCreateDependentView(Document doc, View view, List<string> warnings)
    {
        try
        {
            var dependentId = view.Duplicate(ViewDuplicateOption.AsDependent);
            return doc.GetElement(dependentId) as View;
        }
        catch (Exception ex)
        {
            warnings.Add($"Failed to create dependent view for '{view.Name}': {ex.Message}");
            return null;
        }
    }

    private static List<PendingElement> OrderPending(
        List<PendingElement> pending,
        string order,
        List<string> warnings)
    {
        switch (order?.Trim().ToLowerInvariant())
        {
            case null:
            case "":
            case "input":
                return pending;
            case "heightdesc":
                return pending.OrderByDescending(element => element.HeightFt).ToList();
            case "areadesc":
                return pending.OrderByDescending(element => element.WidthFt * element.HeightFt).ToList();
            default:
                warnings.Add($"Unknown order '{order}'; input order is used.");
                return pending;
        }
    }

    private static void MoveToTarget(Document doc, PendingElement element, RectFt target)
    {
        if (element.PlacedElement is Viewport viewport)
        {
            viewport.SetBoxCenter(new XYZ(
                (target.MinX + target.MaxX) / 2,
                (target.MinY + target.MaxY) / 2,
                0));
            return;
        }

        ElementTransformUtils.MoveElement(
            doc,
            element.PlacedElement.Id,
            new XYZ(
                target.MinX - element.MeasuredRect.MinX,
                target.MinY - element.MeasuredRect.MinY,
                0));
    }

    private static void SkipElement(Document doc, PendingElement element, string reason)
    {
        if (element.PlacedElement != null)
        {
            doc.Delete(element.PlacedElement.Id);
            element.PlacedElement = null;
        }

        element.Item.Placed = false;
        if (string.IsNullOrEmpty(element.Item.Warning))
            element.Item.Warning = reason;
    }

    private static void MarkSkipped(PendingElement element, string reason, List<string> warnings)
    {
        element.Item.Placed = false;
        element.Item.Warning = reason;
        warnings.Add(reason);
    }

    private static RectFt MeasureOnSheet(Element element, ViewSheet sheet)
    {
        if (element is Viewport viewport)
        {
            var outline = viewport.GetBoxOutline();
            return new RectFt(
                outline.MinimumPoint.X,
                outline.MinimumPoint.Y,
                outline.MaximumPoint.X,
                outline.MaximumPoint.Y);
        }

        var bbox = element.get_BoundingBox(sheet);
        if (bbox == null)
            return null;

        return new RectFt(bbox.Min.X, bbox.Min.Y, bbox.Max.X, bbox.Max.Y);
    }

    private static List<RectFt> CollectExistingOutlines(Document doc, ViewSheet sheet)
    {
        var outlines = new List<RectFt>();

        foreach (Viewport viewport in new FilteredElementCollector(doc, sheet.Id).OfClass(typeof(Viewport)))
        {
            var box = viewport.GetBoxOutline();
            outlines.Add(new RectFt(
                box.MinimumPoint.X,
                box.MinimumPoint.Y,
                box.MaximumPoint.X,
                box.MaximumPoint.Y));
        }

        foreach (ScheduleSheetInstance instance in new FilteredElementCollector(doc, sheet.Id)
                     .OfClass(typeof(ScheduleSheetInstance)))
        {
            var bbox = instance.get_BoundingBox(sheet);
            if (bbox != null)
                outlines.Add(new RectFt(bbox.Min.X, bbox.Min.Y, bbox.Max.X, bbox.Max.Y));
        }

        return outlines;
    }

    private static bool IsScheduleAlreadyPlaced(Document doc, ViewSheet sheet, ViewSchedule schedule)
    {
        return new FilteredElementCollector(doc, sheet.Id)
            .OfClass(typeof(ScheduleSheetInstance))
            .Cast<ScheduleSheetInstance>()
            .Any(instance => instance.ScheduleId == schedule.Id);
    }

    private static View ResolveView(Document doc, AutoLayoutItemInfo itemInfo)
    {
        if (!string.IsNullOrWhiteSpace(itemInfo.ViewUniqueId))
        {
            if (doc.GetElement(itemInfo.ViewUniqueId.Trim()) is View byUniqueId && IsPlaceableView(byUniqueId))
                return byUniqueId;
        }

        if (itemInfo.ViewId > 0)
        {
            if (doc.GetElement(RevitMCPCommandSet.Utils.ElementIdExtensions.FromLong(itemInfo.ViewId)) is View byId && IsPlaceableView(byId))
                return byId;
        }

        if (!string.IsNullOrWhiteSpace(itemInfo.ViewName))
        {
            return new FilteredElementCollector(doc)
                .OfClass(typeof(View))
                .Cast<View>()
                .Where(IsPlaceableView)
                .FirstOrDefault(view =>
                    view.Name.Equals(itemInfo.ViewName.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        return null;
    }

    private static bool IsPlaceableView(View view)
    {
        return view != null && !view.IsTemplate && !(view is ViewSheet);
    }

    private static string BuildViewNotFoundMessage(AutoLayoutItemInfo itemInfo)
    {
        if (!string.IsNullOrWhiteSpace(itemInfo.ViewUniqueId))
            return $"View with uniqueId '{itemInfo.ViewUniqueId}' was not found.";
        if (itemInfo.ViewId > 0)
            return $"View with id {itemInfo.ViewId} was not found.";
        if (!string.IsNullOrWhiteSpace(itemInfo.ViewName))
            return $"View named '{itemInfo.ViewName}' was not found.";
        return "View identifier is required: provide viewId, viewUniqueId, or viewName.";
    }

    private static ViewSheet ResolveOrCreateSheet(
        Document doc,
        AutoLayoutSheetInfo info,
        AutoLayoutSheetResult result,
        List<string> warnings)
    {
        var sheet = FindSheet(doc, info);

        if (sheet == null)
        {
            if (!info.CreateSheetIfMissing)
                throw new InvalidOperationException(
                    "Target sheet was not found and createSheetIfMissing is false.");

            sheet = CreateSheet(doc, info, warnings);
            result.SheetCreated = true;
        }

        result.SheetId = sheet.Id.GetValue();
        result.SheetUniqueId = sheet.UniqueId;
        result.SheetNumber = sheet.SheetNumber;
        result.SheetName = sheet.Name;
        return sheet;
    }

    private static ViewSheet FindSheet(Document doc, AutoLayoutSheetInfo info)
    {
        if (!string.IsNullOrWhiteSpace(info.SheetUniqueId))
        {
            if (doc.GetElement(info.SheetUniqueId.Trim()) is ViewSheet byUniqueId)
                return byUniqueId;
        }

        if (info.SheetId > 0)
        {
            if (doc.GetElement(RevitMCPCommandSet.Utils.ElementIdExtensions.FromLong(info.SheetId)) is ViewSheet byId)
                return byId;
        }

        var sheets = new FilteredElementCollector(doc)
            .OfClass(typeof(ViewSheet))
            .Cast<ViewSheet>()
            .Where(candidate => !candidate.IsPlaceholder)
            .ToList();

        if (!string.IsNullOrWhiteSpace(info.SheetNumber))
        {
            var byNumber = sheets.FirstOrDefault(candidate =>
                candidate.SheetNumber.Equals(info.SheetNumber.Trim(), StringComparison.OrdinalIgnoreCase));
            if (byNumber != null)
                return byNumber;
        }

        if (!string.IsNullOrWhiteSpace(info.SheetName))
        {
            return sheets.FirstOrDefault(candidate =>
                candidate.Name.Equals(info.SheetName.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        return null;
    }

    private static ViewSheet CreateSheet(Document doc, AutoLayoutSheetInfo info, List<string> warnings)
    {
        var titleBlock = FindTitleBlock(doc, info.TitleBlockFamilyName, info.TitleBlockTypeName);

        ViewSheet sheet;
        if (titleBlock != null)
        {
            if (!titleBlock.IsActive)
            {
                titleBlock.Activate();
                doc.Regenerate();
            }

            sheet = ViewSheet.Create(doc, titleBlock.Id);
            warnings.Add($"Sheet was created with title block '{titleBlock.FamilyName} - {titleBlock.Name}'.");
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(info.TitleBlockFamilyName) ||
                !string.IsNullOrWhiteSpace(info.TitleBlockTypeName))
            {
                warnings.Add(
                    $"Title block '{info.TitleBlockFamilyName}' / '{info.TitleBlockTypeName}' was not found.");
            }

            sheet = ViewSheet.Create(doc, ElementId.InvalidElementId);
            warnings.Add("Sheet was created without a title block (none matched or loaded).");
        }

        if (!string.IsNullOrWhiteSpace(info.SheetName))
            sheet.Name = info.SheetName.Trim();

        if (!string.IsNullOrWhiteSpace(info.SheetNumber))
            sheet.SheetNumber = GetUniqueSheetNumber(doc, sheet, info.SheetNumber.Trim());

        return sheet;
    }

    private static FamilySymbol FindTitleBlock(Document doc, string familyName, string typeName)
    {
        FamilySymbol fallback = null;

        foreach (var symbol in new FilteredElementCollector(doc)
                     .OfCategory(BuiltInCategory.OST_TitleBlocks)
                     .OfClass(typeof(FamilySymbol))
                     .Cast<FamilySymbol>())
        {
            fallback ??= symbol;

            var familyMatches = string.IsNullOrWhiteSpace(familyName) ||
                                symbol.FamilyName.Equals(familyName.Trim(), StringComparison.OrdinalIgnoreCase);
            var typeMatches = string.IsNullOrWhiteSpace(typeName) ||
                              symbol.Name.Equals(typeName.Trim(), StringComparison.OrdinalIgnoreCase);

            if (familyMatches && typeMatches)
                return symbol;
        }

        // Requested title block not matched: any loaded one keeps the sheet usable.
        return fallback;
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

    private static double MmToFeet(double millimeters) => millimeters / MmPerFoot;
    private static double FeetToMm(double feet) => feet * MmPerFoot;
}
