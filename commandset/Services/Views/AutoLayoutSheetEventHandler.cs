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
            // Do not Reset here - SetParameters/Prepare already Reset before Raise.
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

            var allTitleBlocks = CollectTitleBlockOutlines(doc, sheet);
            // Prefer the drawn title-block frame as paper — sheet.Outline can extend past the A3.
            var paper = ResolvePaperRect(outline, allTitleBlocks, warnings);
            var stampObstacles = FilterCornerTitleBlocks(paper, allTitleBlocks);
            var usable = ComputeUsableArea(paper, info, stampObstacles, warnings);

            if (usable.Width <= 0 || usable.Height <= 0)
                throw new InvalidOperationException(
                    "Usable layout area is empty; reduce margins or the title block reserve.");

            result.UsableWidth = Math.Round(FeetToMm(usable.Width), 2);
            result.UsableHeight = Math.Round(FeetToMm(usable.Height), 2);

            var obstacles = info.AvoidExisting
                ? CollectExistingOutlines(doc, sheet)
                : new List<RectFt>();
            obstacles.AddRange(stampObstacles);

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
                element.Item.X = Math.Round(FeetToMm(target.MinX - paper.MinX), 2);
                element.Item.Y = Math.Round(FeetToMm(target.MinY - paper.MinY), 2);
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

            if (FitsUsable(measured, usable))
            {
                if (element.PlacedElement is Viewport vp &&
                    doc.GetElement(vp.ViewId) is View fittedView)
                {
                    element.Item.AppliedScale = fittedView.Scale;
                }

                continue;
            }

            if (!element.IsSchedule &&
                info.AutoFitScale &&
                element.PlacedElement is Viewport viewport &&
                TryFitViewportByScaling(doc, sheet, viewport, usable, element, info, warnings))
            {
                continue;
            }

            SkipElement(
                doc,
                element,
                $"Element is larger ({element.Item.Width}×{element.Item.Height} mm) than the usable area.");
            warnings.Add(
                $"'{element.Item.ViewName}' is larger ({element.Item.Width:0.##}×{element.Item.Height:0.##} mm) " +
                $"than the usable area ({FeetToMm(usable.Width):0.##}×{FeetToMm(usable.Height):0.##} mm) and was skipped; " +
                (element.IsSchedule
                    ? "filter / split the schedule, remove columns, or use a bigger sheet (schedules cannot be scaled)."
                    : "autoFitScale could not shrink the viewport enough; raise maxViewScale or use a bigger sheet."));
        }

        return pending;
    }

    private static bool FitsUsable(RectFt measured, RectFt usable)
    {
        return measured.Width <= usable.Width + GeometryTolerance &&
               measured.Height <= usable.Height + GeometryTolerance;
    }

    /// <summary>
    ///     Increase view.Scale (draw smaller) until the viewport box fits the usable rectangle.
    ///     Dependent views that refuse a scale change are duplicated as independent copies.
    /// </summary>
    private static bool TryFitViewportByScaling(
        Document doc,
        ViewSheet sheet,
        Viewport viewport,
        RectFt usable,
        PendingElement element,
        AutoLayoutSheetInfo info,
        List<string> warnings)
    {
        var view = doc.GetElement(viewport.ViewId) as View;
        if (view == null)
            return false;

        var originalScale = view.Scale;
        if (originalScale <= 0)
            return false;

        var maxScale = info.MaxViewScale > 0 ? info.MaxViewScale : 500;
        if (maxScale < originalScale)
            maxScale = originalScale;

        view = EnsureScaleableView(doc, sheet, viewport, view, element, warnings);
        if (view == null)
            return false;

        originalScale = view.Scale;
        var tried = new HashSet<int> { originalScale };

        foreach (var scale in EnumerateFitScales(originalScale, maxScale))
        {
            if (!tried.Add(scale))
                continue;

            try
            {
                view.Scale = scale;
            }
            catch (Exception ex)
            {
                warnings.Add($"Cannot set scale 1:{scale} on '{view.Name}': {ex.Message}");
                continue;
            }

            doc.Regenerate();
            var measured = MeasureOnSheet(element.PlacedElement, sheet);
            if (measured == null || measured.Width <= 0 || measured.Height <= 0)
                continue;

            element.MeasuredRect = measured;
            element.WidthFt = measured.Width;
            element.HeightFt = measured.Height;
            element.Item.Width = Math.Round(FeetToMm(measured.Width), 2);
            element.Item.Height = Math.Round(FeetToMm(measured.Height), 2);

            if (!FitsUsable(measured, usable))
                continue;

            element.Item.AppliedScale = scale;
            if (scale != originalScale)
            {
                warnings.Add(
                    $"Reduced scale of '{view.Name}' from 1:{originalScale} to 1:{scale} to fit the usable area.");
            }

            return true;
        }

        try
        {
            view.Scale = originalScale;
            doc.Regenerate();
        }
        catch
        {
            // Keep last attempted scale if restore fails.
        }

        return false;
    }

    private static View EnsureScaleableView(
        Document doc,
        ViewSheet sheet,
        Viewport viewport,
        View view,
        PendingElement element,
        List<string> warnings)
    {
        // Dependent views often share scale with the primary; if Scale cannot change, use an independent duplicate.
        try
        {
            var probe = Math.Min(view.Scale * 2, Math.Max(view.Scale + 1, 2));
            var before = view.Scale;
            view.Scale = probe;
            doc.Regenerate();
            if (view.Scale == probe)
            {
                view.Scale = before;
                doc.Regenerate();
                return view;
            }

            view.Scale = before;
            doc.Regenerate();
        }
        catch
        {
            // Fall through to duplicate.
        }

        try
        {
            var copyId = view.Duplicate(ViewDuplicateOption.Duplicate);
            var copy = doc.GetElement(copyId) as View;
            if (copy == null || !Viewport.CanAddViewToSheet(doc, sheet.Id, copy.Id))
                return view;

            var center = viewport.GetBoxCenter();
            doc.Delete(viewport.Id);
            element.PlacedElement = Viewport.Create(doc, sheet.Id, copy.Id, center);
            element.Item.ViewId = copy.Id.GetValue();
            element.Item.ViewName = copy.Name;
            warnings.Add(
                $"View '{view.Name}' could not change scale as dependent; placed independent copy '{copy.Name}'.");
            doc.Regenerate();
            return copy;
        }
        catch (Exception ex)
        {
            warnings.Add($"Failed to create scaleable copy of '{view.Name}': {ex.Message}");
            return view;
        }
    }

    /// <summary>
    ///     Candidate scale denominators: standard set above current, plus progressive multiples.
    /// </summary>
    public static IEnumerable<int> EnumerateFitScales(int currentScale, int maxScale)
    {
        var standards = new[] { 1, 2, 5, 10, 20, 25, 50, 100, 200, 250, 500, 1000 };
        foreach (var scale in standards)
        {
            if (scale > currentScale && scale <= maxScale)
                yield return scale;
        }

        for (var factor = 2; factor <= 20; factor++)
        {
            var scale = currentScale * factor;
            if (scale > maxScale)
                break;
            if (scale > currentScale)
                yield return scale;
        }
    }

    /// <summary>
    ///     Computes bottom/right title-block reserves (mm) from sheet outline and title-block
    ///     rectangles in sheet coordinates (feet). Bottom uses at least
    ///     <paramref name="titleBlockReserveBottomMm"/>; right grows when a title block sits
    ///     on the right half (Форма 3 / основная надпись).
    /// </summary>
    public static void ResolveTitleBlockReserves(
        double outlineMinU,
        double outlineMinV,
        double outlineMaxU,
        double outlineMaxV,
        IEnumerable<(double MinX, double MinY, double MaxX, double MaxY)> titleBlocks,
        double titleBlockReserveBottomMm,
        out double reserveBottomMm,
        out double reserveRightMm)
    {
        reserveBottomMm = Math.Max(0, titleBlockReserveBottomMm);
        reserveRightMm = 0.0;

        if (titleBlocks == null)
            return;

        var sheetMidU = (outlineMinU + outlineMaxU) / 2.0;
        foreach (var tb in titleBlocks)
        {
            var fromBottomMm = FeetToMm(tb.MaxY - outlineMinV);
            if (fromBottomMm > reserveBottomMm)
                reserveBottomMm = fromBottomMm;

            if (tb.MinX >= sheetMidU - GeometryTolerance)
            {
                var fromRightMm = FeetToMm(outlineMaxU - tb.MinX);
                if (fromRightMm > reserveRightMm)
                    reserveRightMm = fromRightMm;
            }
        }
    }

    private static RectFt ComputeUsableArea(
        RectFt paper,
        AutoLayoutSheetInfo info,
        List<RectFt> stampObstacles,
        List<string> warnings)
    {
        var paperWidthMm = FeetToMm(paper.Width);
        var paperHeightMm = FeetToMm(paper.Height);

        var measuredBlocks = stampObstacles
            .Select(rect => (rect.MinX, rect.MinY, rect.MaxX, rect.MaxY))
            .ToList();

        ResolveTitleBlockReserves(
            paper.MinX,
            paper.MinY,
            paper.MaxX,
            paper.MaxY,
            measuredBlocks,
            info.TitleBlockReserveBottom,
            out var reserveBottomMm,
            out var reserveRightMm);

        reserveRightMm = Math.Min(reserveRightMm, Math.Max(0, paperWidthMm * 0.45));
        reserveBottomMm = Math.Min(
            reserveBottomMm,
            Math.Max(info.TitleBlockReserveBottom, paperHeightMm * 0.40));

        if (reserveRightMm > 0.5 || reserveBottomMm > info.TitleBlockReserveBottom + 0.5)
        {
            warnings.Add(
                $"Usable area accounts for title block reserve bottom={reserveBottomMm:0.#} mm, right={reserveRightMm:0.#} mm.");
        }

        var usable = new RectFt(
            paper.MinX + MmToFeet(info.MarginLeft),
            paper.MinY + MmToFeet(info.MarginBottom + reserveBottomMm),
            paper.MaxX - MmToFeet(info.MarginRight + reserveRightMm),
            paper.MaxY - MmToFeet(info.MarginTop));

        if (usable.Width > 0 && usable.Height > 0)
            return usable;

        warnings.Add(
            "Title-block reserves emptied the usable area; falling back to margins + titleBlockReserveBottom only.");
        return new RectFt(
            paper.MinX + MmToFeet(info.MarginLeft),
            paper.MinY + MmToFeet(info.MarginBottom + Math.Max(0, info.TitleBlockReserveBottom)),
            paper.MaxX - MmToFeet(info.MarginRight),
            paper.MaxY - MmToFeet(info.MarginTop));
    }

    /// <summary>
    ///     Prefer the largest title-block bounding box as the printable paper frame.
    ///     sheet.Outline often extends left/below the drawn A3 for ADSK stamp families.
    /// </summary>
    private static RectFt ResolvePaperRect(
        BoundingBoxUV outline,
        List<RectFt> titleBlocks,
        List<string> warnings)
    {
        var outlineRect = new RectFt(outline.Min.U, outline.Min.V, outline.Max.U, outline.Max.V);
        var outlineArea = outlineRect.Width * outlineRect.Height;
        if (outlineArea <= GeometryTolerance || titleBlocks == null || titleBlocks.Count == 0)
            return outlineRect;

        RectFt best = null;
        var bestArea = 0.0;
        foreach (var tb in titleBlocks)
        {
            var area = tb.Width * tb.Height;
            if (area > bestArea)
            {
                bestArea = area;
                best = tb;
            }
        }

        if (best == null)
            return outlineRect;

        // Substantial paper-sized title block (A3/A2/…), not a tiny orphan bbox.
        if (bestArea >= outlineArea * 0.35 &&
            FeetToMm(best.Width) >= 150 &&
            FeetToMm(best.Height) >= 150)
        {
            warnings.Add(
                $"Paper frame taken from title block extents {FeetToMm(best.Width):0.#}×{FeetToMm(best.Height):0.#} mm.");
            return best;
        }

        return outlineRect;
    }

    private static List<RectFt> CollectTitleBlockOutlines(Document doc, ViewSheet sheet)
    {
        var outlines = new List<RectFt>();

        foreach (var element in new FilteredElementCollector(doc, sheet.Id)
                     .OfCategory(BuiltInCategory.OST_TitleBlocks)
                     .WhereElementIsNotElementType())
        {
            var bbox = element.get_BoundingBox(sheet);
            if (bbox == null)
                continue;

            outlines.Add(new RectFt(bbox.Min.X, bbox.Min.Y, bbox.Max.X, bbox.Max.Y));
        }

        return outlines;
    }

    private static List<RectFt> FilterCornerTitleBlocks(BoundingBoxUV outline, List<RectFt> titleBlocks)
    {
        return FilterCornerTitleBlocks(
            new RectFt(outline.Min.U, outline.Min.V, outline.Max.U, outline.Max.V),
            titleBlocks);
    }

    /// <summary>
    ///     Keep only corner/partial stamps. Drop near-full-sheet title blocks whose bbox
    ///     would block the entire printable field if used as packing obstacles.
    /// </summary>
    private static List<RectFt> FilterCornerTitleBlocks(RectFt paper, List<RectFt> titleBlocks)
    {
        var paperWidthMm = FeetToMm(paper.Width);
        var paperHeightMm = FeetToMm(paper.Height);

        return titleBlocks
            .Where(rect =>
            {
                var w = FeetToMm(rect.Width);
                var h = FeetToMm(rect.Height);
                return w < paperWidthMm * 0.85 || h < paperHeightMm * 0.85;
            })
            .ToList();
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
        var titleBlock = ResolveTitleBlockForNewSheet(doc, info, warnings);

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

    /// <summary>
    ///     ADSK «ОсновнаяНадпись» (Форма 3/5/6) draws the full ГОСТ frame plus the stamp — it is
    ///     the working sheet. ADSK_Титул is the cover page: it carries the project title text and
    ///     no working stamp, so a schedule or plan must never land on it. Prefer основная надпись
    ///     for anything this tool creates.
    /// </summary>
    private static FamilySymbol ResolveTitleBlockForNewSheet(
        Document doc,
        AutoLayoutSheetInfo info,
        List<string> warnings)
    {
        var requested = FindTitleBlock(doc, info.TitleBlockFamilyName, info.TitleBlockTypeName);
        if (requested != null && !IsCoverSheetTitleBlock(requested))
            return requested;

        var working = FindWorkingTitleBlock(doc);
        if (working == null)
            return requested;

        if (requested != null)
        {
            warnings.Add(
                $"'{requested.FamilyName} - {requested.Name}' is a cover-page title block; " +
                $"using working title block '{working.FamilyName} - {working.Name}' instead.");
        }

        return working;
    }

    /// <summary>ADSK_Титул / «Начальный вид» — cover pages, not working sheets.</summary>
    private static bool IsCoverSheetTitleBlock(FamilySymbol symbol)
    {
        if (symbol == null)
            return false;

        var family = symbol.FamilyName ?? string.Empty;
        return family.IndexOf("Титул", StringComparison.OrdinalIgnoreCase) >= 0
               || family.IndexOf("Начальный", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /// <summary>First «основная надпись» type, else any non-cover title block.</summary>
    private static FamilySymbol FindWorkingTitleBlock(Document doc)
    {
        var symbols = new FilteredElementCollector(doc)
            .OfCategory(BuiltInCategory.OST_TitleBlocks)
            .OfClass(typeof(FamilySymbol))
            .Cast<FamilySymbol>()
            .ToList();

        return symbols.FirstOrDefault(IsWorkingStampTitleBlock)
               ?? symbols.FirstOrDefault(symbol => !IsCoverSheetTitleBlock(symbol));
    }

    private static bool IsWorkingStampTitleBlock(FamilySymbol symbol)
    {
        var family = symbol?.FamilyName ?? string.Empty;
        return family.IndexOf("ОсновнаяНадпись", StringComparison.OrdinalIgnoreCase) >= 0
               || family.IndexOf("основная надпись", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static FamilySymbol FindTitleBlock(Document doc, string familyName, string typeName)
    {
        var symbols = new FilteredElementCollector(doc)
            .OfCategory(BuiltInCategory.OST_TitleBlocks)
            .OfClass(typeof(FamilySymbol))
            .Cast<FamilySymbol>()
            .ToList();

        // A bare type name like "А3А" exists in several families, and the cover-page one
        // must not win the race — search working stamps first.
        foreach (var symbol in symbols.OrderByDescending(IsWorkingStampTitleBlock))
        {
            var familyMatches = string.IsNullOrWhiteSpace(familyName) ||
                                symbol.FamilyName.Equals(familyName.Trim(), StringComparison.OrdinalIgnoreCase);
            var typeMatches = string.IsNullOrWhiteSpace(typeName) ||
                              symbol.Name.Equals(typeName.Trim(), StringComparison.OrdinalIgnoreCase);

            if (familyMatches && typeMatches)
                return symbol;
        }

        // Requested title block not matched: any loaded one keeps the sheet usable.
        return symbols.FirstOrDefault();
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
