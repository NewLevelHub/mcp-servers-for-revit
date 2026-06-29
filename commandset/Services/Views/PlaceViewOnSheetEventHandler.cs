using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCPCommandSet.Models.Views;
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
        _resetEvent.Reset();
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

        var origin = new XYZ(MmToFeet(info.PositionX), MmToFeet(info.PositionY), 0);
        var instance = ScheduleSheetInstance.Create(doc, sheet.Id, scheduleView.Id, origin);

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

        var location = new XYZ(MmToFeet(info.PositionX), MmToFeet(info.PositionY), 0);
        Viewport viewport;
        try
        {
            viewport = Viewport.Create(doc, sheet.Id, view.Id, location);
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

        return viewport;
    }

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

    private static double MmToFeet(double millimeters) => millimeters / 304.8;

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
