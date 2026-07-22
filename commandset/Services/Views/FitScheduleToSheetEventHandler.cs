using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCPCommandSet.Models.Views;
using RevitMCPSDK.API.Interfaces;

namespace RevitMCPCommandSet.Services.Views;

/// <summary>
/// Makes an existing ViewSchedule fit within a target width on the sheet by
/// applying a cascade of non-destructive strategies (REV-68):
///   1. Hide optional columns listed by the caller.
///   2. Narrow all visible columns proportionally.
///   3. Add a Level filter so only one level's rows are shown.
/// The handler never widens columns and never removes already-hidden fields.
/// </summary>
public class FitScheduleToSheetEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
{
    private const double FeetToMm = 304.8;
    /// <summary>A3 working zone width (297 – 20 margin). Used when sheetId is absent and maxWidthMm = 0.</summary>
    private const double DefaultMaxWidthMm = 277;

    private FitScheduleToSheetInfo _info;
    private readonly ManualResetEvent _resetEvent = new ManualResetEvent(false);

    public FitScheduleToSheetResult ResultInfo { get; private set; } = new();
    public bool TaskCompleted { get; private set; }

    public void SetParameters(FitScheduleToSheetInfo info)
    {
        _info = info ?? throw new ArgumentNullException(nameof(info));
        TaskCompleted = false;
        _resetEvent.Reset();
    }

    public bool WaitForCompletion(int timeoutMilliseconds = 30000)
    {
            // Do not Reset here - SetParameters/Prepare already Reset before Raise.
            return _resetEvent.WaitOne(timeoutMilliseconds);
    }

    public void Execute(UIApplication app)
    {
        var warnings = new List<string>();
        var appliedStrategies = new List<string>();

        try
        {
            var doc = app.ActiveUIDocument.Document;
            var schedule = ResolveSchedule(doc, _info, warnings);
            if (schedule == null)
            {
                ResultInfo = FailResult(BuildNotFoundMessage(_info), 0, warnings, appliedStrategies);
                return;
            }

            var targetWidth = ResolveTargetWidth(doc, _info, warnings);

            using (var tx = new Transaction(doc, "Fit Schedule to Sheet"))
            {
                tx.Start();

                // Strategy 1: hide optional columns.
                if (_info.AllowHideColumns)
                    ApplyHideColumns(schedule, _info, appliedStrategies, warnings);

                // Measure after hide.
                var widthAfterHide = ComputeScheduleWidthMm(schedule);

                // Strategy 2: narrow columns proportionally.
                if (_info.AllowNarrowColumns && widthAfterHide > targetWidth)
                    ApplyNarrowColumns(schedule, targetWidth, _info.MinColumnWidthMm, appliedStrategies, warnings);

                // Strategy 3: add a level filter.
                if (_info.AllowLevelFilter &&
                    ComputeScheduleWidthMm(schedule) > targetWidth &&
                    (!string.IsNullOrWhiteSpace(_info.LevelName) || _info.LevelId.HasValue))
                {
                    ApplyLevelFilter(doc, schedule, _info, appliedStrategies, warnings);
                }

                tx.Commit();
            }

            var finalWidth = ComputeScheduleWidthMm(schedule);
            ResultInfo = new FitScheduleToSheetResult
            {
                Success = true,
                Message = finalWidth <= targetWidth
                    ? $"Schedule '{schedule.Name}' fits ({finalWidth:0.#} mm ≤ {targetWidth:0.#} mm)."
                    : $"Schedule '{schedule.Name}' still {finalWidth:0.#} mm > {targetWidth:0.#} mm target; manual split may be needed.",
                ScheduleId = GetIdValue(schedule.Id),
                ScheduleName = schedule.Name,
                FinalWidthMm = Math.Round(finalWidth, 2),
                TargetWidthMm = Math.Round(targetWidth, 2),
                Fits = finalWidth <= targetWidth,
                AppliedStrategies = appliedStrategies,
                Warnings = warnings
            };
        }
        catch (Exception ex)
        {
            ResultInfo = FailResult($"fit_schedule_to_sheet failed: {ex.Message}", 0, warnings, appliedStrategies);
        }
        finally
        {
            TaskCompleted = true;
            _resetEvent.Set();
        }
    }

    public string GetName() => "Fit Schedule to Sheet";

    // ── Target width ──────────────────────────────────────────────────────────

    private static double ResolveTargetWidth(
        Document doc,
        FitScheduleToSheetInfo info,
        List<string> warnings)
    {
        if (info.MaxWidthMm > 0)
            return info.MaxWidthMm;

        if (info.SheetId.HasValue)
        {
            var sheetWidth = TryGetSheetWorkingWidthMm(doc, info.SheetId.Value, warnings);
            if (sheetWidth > 0) return sheetWidth;
        }

        warnings.Add($"Could not infer sheet width; using default {DefaultMaxWidthMm} mm (A3 working zone).");
        return DefaultMaxWidthMm;
    }

    private static double TryGetSheetWorkingWidthMm(Document doc, long sheetId, List<string> warnings)
    {
        try
        {
#if REVIT2024_OR_GREATER
            var sheet = doc.GetElement(new ElementId(sheetId)) as ViewSheet;
#else
            var sheet = doc.GetElement(new ElementId((int)sheetId)) as ViewSheet;
#endif
            if (sheet == null) { warnings.Add($"Sheet {sheetId} not found."); return 0; }

            // Largest title block bounding box approximates the paper rectangle.
            var titleBlocks = new FilteredElementCollector(doc, sheet.Id)
                .OfCategory(BuiltInCategory.OST_TitleBlocks)
                .WhereElementIsNotElementType()
                .Cast<FamilyInstance>()
                .ToList();

            if (titleBlocks.Count == 0) { warnings.Add("No title blocks on sheet; cannot infer width."); return 0; }

            var maxArea = titleBlocks
                .Select(tb => tb.get_BoundingBox(sheet))
                .Where(bb => bb != null)
                .OrderByDescending(bb => (bb.Max.X - bb.Min.X) * (bb.Max.Y - bb.Min.Y))
                .FirstOrDefault();

            if (maxArea == null) { warnings.Add("Title block has no bounding box."); return 0; }

            // Working width = paper width − 20 mm left margin − ~185 mm stamp (right side).
            var paperWidthMm = (maxArea.Max.X - maxArea.Min.X) * FeetToMm;
            const double estimatedStampMm = 185;
            const double leftMarginMm = 20;
            var workingWidthMm = paperWidthMm - leftMarginMm - estimatedStampMm;
            return Math.Max(100, workingWidthMm);
        }
        catch (Exception ex)
        {
            warnings.Add($"Sheet width inference error: {ex.Message}");
            return 0;
        }
    }

    // ── Strategy 1: hide optional columns ────────────────────────────────────

    private static void ApplyHideColumns(
        ViewSchedule schedule,
        FitScheduleToSheetInfo info,
        List<string> applied,
        List<string> warnings)
    {
        var def = schedule.Definition;
        var hiddenCount = 0;

        var optionalSet = new HashSet<string>(
            info.OptionalColumns.Select(c => c.Trim()),
            StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < def.GetFieldCount(); i++)
        {
            var field = def.GetField(def.GetFieldId(i));
            if (field.IsHidden) continue;

            var shouldHide = optionalSet.Count > 0
                ? optionalSet.Contains(field.GetName())
                : !field.IsCalculatedField; // default: any non-calculated visible field is optional

            if (shouldHide)
            {
                field.IsHidden = true;
                hiddenCount++;
            }
        }

        if (hiddenCount > 0)
            applied.Add($"Hidden {hiddenCount} column(s): {string.Join(", ", info.OptionalColumns.Count > 0 ? info.OptionalColumns : new List<string> { "(auto)" })}.");
        else
            warnings.Add("HideColumns: no columns matched for hiding.");
    }

    // ── Strategy 2: narrow columns proportionally ─────────────────────────────

    private static void ApplyNarrowColumns(
        ViewSchedule schedule,
        double targetWidthMm,
        double minColumnWidthMm,
        List<string> applied,
        List<string> warnings)
    {
        var def = schedule.Definition;
        var currentWidth = ComputeScheduleWidthMm(schedule);
        if (currentWidth <= 0 || currentWidth <= targetWidthMm) return;

        var ratio = targetWidthMm / currentWidth;
        var narrowedCount = 0;

        for (var i = 0; i < def.GetFieldCount(); i++)
        {
            var field = def.GetField(def.GetFieldId(i));
            if (field.IsHidden) continue;

            var currentMm = field.GridColumnWidth * FeetToMm;
            var newMm = Math.Max(minColumnWidthMm, Math.Round(currentMm * ratio, 1));
            if (newMm < currentMm)
            {
                field.GridColumnWidth = newMm / FeetToMm;
                narrowedCount++;
            }
        }

        if (narrowedCount > 0)
            applied.Add($"Narrowed {narrowedCount} column(s) by ratio {ratio:0.##} to fit {targetWidthMm:0.#} mm.");
        else
            warnings.Add("NarrowColumns: no columns could be narrowed further.");
    }

    // ── Strategy 3: level filter ──────────────────────────────────────────────

    private static void ApplyLevelFilter(
        Document doc,
        ViewSchedule schedule,
        FitScheduleToSheetInfo info,
        List<string> applied,
        List<string> warnings)
    {
        var def = schedule.Definition;

        // Find the Level field in the schedule.
        ScheduleFieldId levelFieldId = null;
        for (var i = 0; i < def.GetFieldCount(); i++)
        {
            var f = def.GetField(def.GetFieldId(i));
            var name = f.GetName();
            if (name.Equals("Level", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Уровень", StringComparison.OrdinalIgnoreCase))
            {
                levelFieldId = def.GetFieldId(i);
                break;
            }
        }

        if (levelFieldId == null)
        {
            warnings.Add("LevelFilter: no 'Level' / 'Уровень' field found in schedule; strategy skipped.");
            return;
        }

        // Resolve the level element id.
        ElementId levelElemId = null;
        if (info.LevelId.HasValue)
        {
#if REVIT2024_OR_GREATER
            levelElemId = new ElementId(info.LevelId.Value);
#else
            levelElemId = new ElementId((int)info.LevelId.Value);
#endif
        }
        else if (!string.IsNullOrWhiteSpace(info.LevelName))
        {
            var level = new FilteredElementCollector(doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .FirstOrDefault(l => l.Name.Equals(info.LevelName.Trim(), StringComparison.OrdinalIgnoreCase));

            if (level == null)
            {
                warnings.Add($"LevelFilter: level '{info.LevelName}' not found; strategy skipped.");
                return;
            }

            levelElemId = level.Id;
        }

        if (levelElemId == null) { warnings.Add("LevelFilter: no level resolved; strategy skipped."); return; }

        try
        {
            def.AddFilter(new ScheduleFilter(levelFieldId, ScheduleFilterType.Equal, levelElemId));
            var levelLabel = info.LevelName ?? info.LevelId.ToString();
            applied.Add($"Added Level filter = '{levelLabel}'.");
        }
        catch (Exception ex)
        {
            warnings.Add($"LevelFilter: could not add filter: {ex.Message}");
        }
    }

    // ── Schedule width calculation ────────────────────────────────────────────

    private static double ComputeScheduleWidthMm(ViewSchedule schedule)
    {
        var def = schedule.Definition;
        double totalFeet = 0;

        for (var i = 0; i < def.GetFieldCount(); i++)
        {
            var field = def.GetField(def.GetFieldId(i));
            if (!field.IsHidden)
                totalFeet += field.GridColumnWidth;
        }

        return totalFeet * FeetToMm;
    }

    // ── Schedule resolver ─────────────────────────────────────────────────────

    private static ViewSchedule ResolveSchedule(
        Document doc,
        FitScheduleToSheetInfo info,
        List<string> warnings)
    {
        if (!string.IsNullOrWhiteSpace(info.ScheduleUniqueId))
        {
            var byUid = doc.GetElement(info.ScheduleUniqueId.Trim()) as ViewSchedule;
            if (byUid != null) return byUid;
            warnings.Add($"UniqueId '{info.ScheduleUniqueId}' not found.");
        }

        if (info.ScheduleId.HasValue)
        {
#if REVIT2024_OR_GREATER
            var byId = doc.GetElement(new ElementId(info.ScheduleId.Value)) as ViewSchedule;
#else
            var byId = doc.GetElement(new ElementId((int)info.ScheduleId.Value)) as ViewSchedule;
#endif
            if (byId != null) return byId;
            warnings.Add($"ScheduleId {info.ScheduleId.Value} not found.");
        }

        if (!string.IsNullOrWhiteSpace(info.ScheduleName))
        {
            return new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSchedule))
                .Cast<ViewSchedule>()
                .FirstOrDefault(s =>
                    s.Name.Equals(info.ScheduleName.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        return null;
    }

    private static string BuildNotFoundMessage(FitScheduleToSheetInfo info)
    {
        if (!string.IsNullOrWhiteSpace(info.ScheduleUniqueId))
            return $"Schedule with uniqueId '{info.ScheduleUniqueId}' was not found.";
        if (info.ScheduleId.HasValue)
            return $"Schedule with id {info.ScheduleId.Value} was not found.";
        if (!string.IsNullOrWhiteSpace(info.ScheduleName))
            return $"Schedule named '{info.ScheduleName}' was not found.";
        return "Provide scheduleId, scheduleUniqueId, or scheduleName.";
    }

    private static FitScheduleToSheetResult FailResult(
        string message,
        long id,
        List<string> warnings,
        List<string> applied) =>
        new FitScheduleToSheetResult
        {
            Success = false,
            Message = message,
            ScheduleId = id,
            AppliedStrategies = applied,
            Warnings = warnings
        };

    private static long GetIdValue(ElementId id)
    {
#if REVIT2024_OR_GREATER
        return id.Value;
#else
        return id.IntegerValue;
#endif
    }
}
