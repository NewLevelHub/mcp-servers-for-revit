using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCPCommandSet.Models.Views;
using RevitMCPCommandSet.Utils;
using RevitMCPSDK.API.Interfaces;

namespace RevitMCPCommandSet.Services.Views;

public class PlaceViewOnSheetEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
{
    private ViewportCreationInfo _placementInfo;
    private readonly ManualResetEvent _resetEvent = new ManualResetEvent(false);

    public ViewportPlacementResult ResultInfo { get; private set; } = new ViewportPlacementResult();
    public bool TaskCompleted { get; private set; }

    public void SetParameters(ViewportCreationInfo placementInfo)
    {
        _placementInfo = placementInfo ?? throw new ArgumentNullException(nameof(placementInfo));
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
            var sheet = ResolveViewSheet(doc, _placementInfo);
            var view = ResolveView(doc, _placementInfo);

            Element placedElement;
            string placementType;

            using (var tx = new Transaction(doc, "Place View On Sheet"))
            {
                tx.Start();

                if (view is ViewSchedule scheduleView)
                {
                    placedElement = PlaceSchedule(doc, sheet, scheduleView, _placementInfo, warnings);
                    placementType = "schedule";
                }
                else
                {
                    placedElement = PlaceViewport(doc, sheet, view, _placementInfo, warnings);
                    placementType = "viewport";
                }

                tx.Commit();
            }

            ResultInfo = new ViewportPlacementResult
            {
                Success = true,
                Message = $"Successfully placed '{view.Name}' on sheet '{sheet.SheetNumber}'",
                PlacementType = placementType,
                ElementId = GetElementIdValue(placedElement.Id),
                ElementUniqueId = placedElement.UniqueId,
                SheetId = GetElementIdValue(sheet.Id),
                ViewId = GetElementIdValue(view.Id),
                PositionX = _placementInfo.PositionX,
                PositionY = _placementInfo.PositionY,
                Warnings = warnings
            };
        }
        catch (Exception ex)
        {
            ResultInfo = new ViewportPlacementResult
            {
                Success = false,
                Message = $"Error placing view on sheet: {ex.Message}",
                PositionX = _placementInfo?.PositionX ?? 0,
                PositionY = _placementInfo?.PositionY ?? 0,
                Warnings = warnings
            };
        }
        finally
        {
            TaskCompleted = true;
            _resetEvent.Set();
        }
    }

    private static Element PlaceSchedule(
        Document doc,
        ViewSheet sheet,
        ViewSchedule scheduleView,
        ViewportCreationInfo info,
        List<string> warnings)
    {
        var alreadyPlaced = new FilteredElementCollector(doc, sheet.Id)
            .OfClass(typeof(ScheduleSheetInstance))
            .Cast<ScheduleSheetInstance>()
            .Any(instance => instance.ScheduleId == scheduleView.Id);

        if (alreadyPlaced)
            warnings.Add($"Schedule '{scheduleView.Name}' is already placed on this sheet.");

        var frame = SheetFrameGeometry.Resolve(doc, sheet);
        WarnWhenFrameIsGuessed(frame, sheet, warnings);

        var requestedLowerLeft = GetRequestedLowerLeftPoint(frame, info);
        var instance = ScheduleSheetInstance.Create(
            doc,
            sheet.Id,
            scheduleView.Id,
            new XYZ(requestedLowerLeft.X, requestedLowerLeft.Y, 0));
        doc.Regenerate();
        FitScheduleIntoFrame(doc, sheet, frame, instance, scheduleView, requestedLowerLeft, warnings);

        if (Math.Abs(info.Rotation) > double.Epsilon)
            warnings.Add("Schedule rotation is not supported via API and was ignored.");

        return instance;
    }

    private static Viewport PlaceViewport(
        Document doc,
        ViewSheet sheet,
        View view,
        ViewportCreationInfo info,
        List<string> warnings)
    {
        var existingViewport = new FilteredElementCollector(doc, sheet.Id)
            .OfClass(typeof(Viewport))
            .Cast<Viewport>()
            .Any(viewport => viewport.ViewId == view.Id);

        if (existingViewport)
            warnings.Add($"View '{view.Name}' is already placed on this sheet.");

        var frame = SheetFrameGeometry.Resolve(doc, sheet);
        WarnWhenFrameIsGuessed(frame, sheet, warnings);

        Viewport viewport;
        try
        {
            viewport = Viewport.Create(
                doc,
                sheet.Id,
                view.Id,
                new XYZ((frame.MinX + frame.MaxX) / 2.0, (frame.MinY + frame.MaxY) / 2.0, 0));
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"View '{view.Name}' cannot be placed on sheet '{sheet.SheetNumber}': {ex.Message}",
                ex);
        }

        if (info.ViewportTypeId > 0)
        {
            try
            {
                viewport.ChangeTypeId(new ElementId(info.ViewportTypeId));
            }
            catch (Exception ex)
            {
                warnings.Add($"Failed to apply viewport type: {ex.Message}");
            }
        }

        if (info.DisplayTitle.HasValue)
            ApplyViewportDisplayTitle(viewport, info.DisplayTitle.Value, warnings);

        if (info.ScaleOverride > 0)
            ApplyViewportScale(view, info.ScaleOverride, warnings);

        if (!string.IsNullOrWhiteSpace(info.LabelText))
            warnings.Add("Custom viewport label text is not supported and was ignored.");

        if (Math.Abs(info.Rotation) > double.Epsilon)
            warnings.Add("Viewport rotation is not supported in this command and was ignored.");

        doc.Regenerate();
        FitViewportIntoFrame(viewport, frame, GetRequestedLowerLeftPoint(frame, info), warnings);

        return viewport;
    }

    private static void FitViewportIntoFrame(
        Viewport viewport,
        SheetFrameGeometry frame,
        XYZ requestedLowerLeft,
        List<string> warnings)
    {
        var outline = viewport.GetBoxOutline();
        var width = outline.MaximumPoint.X - outline.MinimumPoint.X;
        var height = outline.MaximumPoint.Y - outline.MinimumPoint.Y;

        var target = frame.FitInside(width, height, requestedLowerLeft.X, requestedLowerLeft.Y);
        viewport.SetBoxCenter(new XYZ(target.X + width / 2.0, target.Y + height / 2.0, 0));

        if (frame.ExceedsPrintable(width, height))
        {
            warnings.Add(
                $"Viewport is {SheetFrameGeometry.FeetToMm(width):F0}×{SheetFrameGeometry.FeetToMm(height):F0} mm " +
                "and does not fit the printable field " +
                $"{PrintableWidthMm(frame):F0}×{PrintableHeightMm(frame):F0} mm — reduce the view scale or use a larger sheet.");
        }

        WarnWhenMoved(frame, "Viewport", requestedLowerLeft, target, warnings);
    }

    /// <summary>
    /// <see cref="ScheduleSheetInstance.Create"/> treats the origin as the table's TOP-left and
    /// the table grows downwards, so the requested point never matches what the instance ends up
    /// occupying. Measure the placed instance and move it so its lower-left lands where the caller
    /// asked — inside the frame and clear of the stamp.
    /// </summary>
    private static void FitScheduleIntoFrame(
        Document doc,
        ViewSheet sheet,
        SheetFrameGeometry frame,
        ScheduleSheetInstance instance,
        ViewSchedule scheduleView,
        XYZ requestedLowerLeft,
        List<string> warnings)
    {
        var bbox = instance.get_BoundingBox(sheet);
        if (bbox == null)
            return;

        var width = bbox.Max.X - bbox.Min.X;
        var height = bbox.Max.Y - bbox.Min.Y;
        var target = frame.FitInside(width, height, requestedLowerLeft.X, requestedLowerLeft.Y);

        ElementTransformUtils.MoveElement(
            doc,
            instance.Id,
            new XYZ(target.X - bbox.Min.X, target.Y - bbox.Min.Y, 0));

        WarnWhenScheduleExceedsFrame(frame, width, height, scheduleView, warnings);
        WarnWhenMoved(frame, "Schedule", requestedLowerLeft, target, warnings);
    }

    /// <summary>
    /// A table wider or taller than the printable field cannot be rescued by moving it —
    /// fitting only pins the overflow to a corner. Say so, so the caller narrows the schedule
    /// or picks a larger format instead of shipping a sheet with rows past the frame.
    /// </summary>
    private static void WarnWhenScheduleExceedsFrame(
        SheetFrameGeometry frame,
        double width,
        double height,
        ViewSchedule scheduleView,
        List<string> warnings)
    {
        if (height > PrintableHeightFt(frame) + 1e-9)
        {
            warnings.Add(
                $"Schedule '{scheduleView.Name}' is {SheetFrameGeometry.FeetToMm(height):F0} mm tall but the printable " +
                $"field is only {PrintableHeightMm(frame):F0} mm — rows will run past the frame. " +
                "Split the schedule («Разбить таблицу») or use a larger sheet format.");
        }

        if (width > PrintableWidthFt(frame) + 1e-9)
        {
            warnings.Add(
                $"Schedule '{scheduleView.Name}' is {SheetFrameGeometry.FeetToMm(width):F0} mm wide but the printable " +
                $"field is only {PrintableWidthMm(frame):F0} mm — call fit_schedule_to_sheet to narrow it.");
        }
    }

    private static void WarnWhenFrameIsGuessed(
        SheetFrameGeometry frame,
        ViewSheet sheet,
        List<string> warnings)
    {
        if (frame.FromTitleBlock)
            return;

        warnings.Add(
            $"Sheet '{sheet.SheetNumber}' has no title block, so the frame was taken from the sheet outline; " +
            "positions may not match the printed border.");
    }

    private static void WarnWhenMoved(
        SheetFrameGeometry frame,
        string elementType,
        XYZ requestedLowerLeft,
        XYZ finalLowerLeft,
        List<string> warnings)
    {
        if (Math.Abs(requestedLowerLeft.X - finalLowerLeft.X) <= 1e-9 &&
            Math.Abs(requestedLowerLeft.Y - finalLowerLeft.Y) <= 1e-9)
        {
            return;
        }

        var requestedX = SheetFrameGeometry.FeetToMm(requestedLowerLeft.X - frame.MinX);
        var requestedY = SheetFrameGeometry.FeetToMm(requestedLowerLeft.Y - frame.MinY);
        var finalX = SheetFrameGeometry.FeetToMm(finalLowerLeft.X - frame.MinX);
        var finalY = SheetFrameGeometry.FeetToMm(finalLowerLeft.Y - frame.MinY);
        warnings.Add(
            $"{elementType} was moved inside the frame and clear of the stamp: " +
            $"requested=({requestedX:F1}, {requestedY:F1}) mm, actual=({finalX:F1}, {finalY:F1}) mm.");
    }

    /// <summary>Lower-left of the requested box, measured from the paper frame corner.</summary>
    private static XYZ GetRequestedLowerLeftPoint(SheetFrameGeometry frame, ViewportCreationInfo info)
    {
        return new XYZ(
            frame.MinX + MmToFeet(info.PositionX),
            frame.MinY + MmToFeet(info.PositionY),
            0);
    }

    private static double PrintableWidthFt(SheetFrameGeometry frame) =>
        frame.PrintableMaxX - frame.PrintableMinX;

    private static double PrintableHeightFt(SheetFrameGeometry frame) =>
        frame.PrintableMaxY - frame.PrintableMinY;

    private static double PrintableWidthMm(SheetFrameGeometry frame) =>
        SheetFrameGeometry.FeetToMm(PrintableWidthFt(frame));

    private static double PrintableHeightMm(SheetFrameGeometry frame) =>
        SheetFrameGeometry.FeetToMm(PrintableHeightFt(frame));

    private static void ApplyViewportDisplayTitle(Viewport viewport, bool displayTitle, List<string> warnings)
    {
        try
        {
            var param = viewport.get_Parameter(BuiltInParameter.VIEWPORT_ATTR_SHOW_LABEL);
            if (param != null && !param.IsReadOnly)
                param.Set(displayTitle ? 1 : 0);
        }
        catch (Exception ex)
        {
            warnings.Add($"Failed to set viewport title visibility: {ex.Message}");
        }
    }

    private static void ApplyViewportScale(View view, int scaleOverride, List<string> warnings)
    {
        try
        {
            if (view.Scale != scaleOverride)
                view.Scale = scaleOverride;
        }
        catch (Exception ex)
        {
            warnings.Add($"Failed to override view scale: {ex.Message}");
        }
    }

    private static ViewSheet ResolveViewSheet(Document doc, ViewportCreationInfo info)
    {
        if (!string.IsNullOrWhiteSpace(info.SheetUniqueId))
        {
            var sheet = doc.GetElement(info.SheetUniqueId) as ViewSheet;
            if (sheet != null)
                return sheet;
        }

        if (info.SheetId > 0)
        {
            var sheet = doc.GetElement(new ElementId(info.SheetId)) as ViewSheet;
            if (sheet != null)
                return sheet;
        }

        throw new ArgumentException("A valid sheetId or sheetUniqueId is required.");
    }

    private static View ResolveView(Document doc, ViewportCreationInfo info)
    {
        if (!string.IsNullOrWhiteSpace(info.ViewUniqueId))
        {
            var view = doc.GetElement(info.ViewUniqueId) as View;
            if (view != null)
                return view;
        }

        if (info.ViewId > 0)
        {
            var view = doc.GetElement(new ElementId(info.ViewId)) as View;
            if (view != null)
                return view;
        }

        throw new ArgumentException("A valid viewId or viewUniqueId is required.");
    }

    private static double MmToFeet(double millimeters) => SheetFrameGeometry.MmToFeet(millimeters);

    private static long GetElementIdValue(ElementId elementId)
    {
#if REVIT2024_OR_GREATER
        return elementId.Value;
#else
        return elementId.IntegerValue;
#endif
    }

    public string GetName() => "Place View On Sheet";
}
