using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCPCommandSet.Models.Views;
using RevitMCPSDK.API.Interfaces;

namespace RevitMCPCommandSet.Services.Views;

/// <summary>
/// Edits an existing ViewSchedule in-place: field widths/visibility, filters,
/// sorts, groups, and display flags.  Does not create new fields — use
/// create_schedule for that.  (REV-68)
/// </summary>
public class ConfigureScheduleEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
{
    private const double FeetToMm = 304.8;

    private ConfigureScheduleInfo _info;
    private readonly ManualResetEvent _resetEvent = new ManualResetEvent(false);

    public ConfigureScheduleResult ResultInfo { get; private set; } = new();
    public bool TaskCompleted { get; private set; }

    public void SetParameters(ConfigureScheduleInfo info)
    {
        _info = info ?? throw new ArgumentNullException(nameof(info));
        TaskCompleted = false;
        _resetEvent.Reset();
    }

    public bool WaitForCompletion(int timeoutMilliseconds = 30000)
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
            var schedule = ResolveSchedule(doc, _info, warnings);
            if (schedule == null)
            {
                ResultInfo = new ConfigureScheduleResult
                {
                    Success = false,
                    Message = BuildNotFoundMessage(_info),
                    Warnings = warnings
                };
                return;
            }

            using (var tx = new Transaction(doc, "Configure Schedule"))
            {
                tx.Start();
                ApplyDisplayOptions(schedule, _info, warnings);
                ApplyFieldMutations(doc, schedule, _info, warnings);
                ApplyFilterMutations(doc, schedule, _info, warnings);
                ApplySortMutations(doc, schedule, _info, warnings);
                tx.Commit();
            }

            var def = schedule.Definition;
            ResultInfo = new ConfigureScheduleResult
            {
                Success = true,
                Message = $"Schedule '{schedule.Name}' configured successfully.",
                ScheduleId = GetIdValue(schedule.Id),
                ScheduleName = schedule.Name,
                FieldCount = def.GetFieldCount(),
                FilterCount = def.GetFilterCount(),
                Warnings = warnings
            };
        }
        catch (Exception ex)
        {
            ResultInfo = new ConfigureScheduleResult
            {
                Success = false,
                Message = $"configure_schedule failed: {ex.Message}",
                Warnings = warnings
            };
        }
        finally
        {
            TaskCompleted = true;
            _resetEvent.Set();
        }
    }

    public string GetName() => "Configure Schedule";

    // ── Resolvers ────────────────────────────────────────────────────────────

    private static ViewSchedule ResolveSchedule(
        Document doc,
        ConfigureScheduleInfo info,
        List<string> warnings)
    {
        if (!string.IsNullOrWhiteSpace(info.ScheduleUniqueId))
        {
            var byUid = doc.GetElement(info.ScheduleUniqueId.Trim()) as ViewSchedule;
            if (byUid != null) return byUid;
            warnings.Add($"UniqueId '{info.ScheduleUniqueId}' not found; falling back to id/name.");
        }

        if (info.ScheduleId.HasValue)
        {
#if REVIT2024_OR_GREATER
            var byId = doc.GetElement(new ElementId(info.ScheduleId.Value)) as ViewSchedule;
#else
            var byId = doc.GetElement(new ElementId((int)info.ScheduleId.Value)) as ViewSchedule;
#endif
            if (byId != null) return byId;
            warnings.Add($"ScheduleId {info.ScheduleId.Value} not found; falling back to name.");
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

    private static string BuildNotFoundMessage(ConfigureScheduleInfo info)
    {
        if (!string.IsNullOrWhiteSpace(info.ScheduleUniqueId))
            return $"Schedule with uniqueId '{info.ScheduleUniqueId}' was not found.";
        if (info.ScheduleId.HasValue)
            return $"Schedule with id {info.ScheduleId.Value} was not found.";
        if (!string.IsNullOrWhiteSpace(info.ScheduleName))
            return $"Schedule named '{info.ScheduleName}' was not found.";
        return "Provide scheduleId, scheduleUniqueId, or scheduleName.";
    }

    // ── Display options ───────────────────────────────────────────────────────

    private static void ApplyDisplayOptions(
        ViewSchedule schedule,
        ConfigureScheduleInfo info,
        List<string> warnings)
    {
        var def = schedule.Definition;
        try
        {
            if (info.ShowTitle.HasValue)
                def.ShowTitle = info.ShowTitle.Value;
            if (info.ShowHeaders.HasValue)
                def.ShowHeaders = info.ShowHeaders.Value;
            if (info.ShowGridLines.HasValue)
                def.ShowGridLines = info.ShowGridLines.Value;
            if (info.IsItemized.HasValue)
                def.IsItemized = info.IsItemized.Value;
        }
        catch (Exception ex)
        {
            warnings.Add($"Display option error: {ex.Message}");
        }
    }

    // ── Field mutations ───────────────────────────────────────────────────────

    private static void ApplyFieldMutations(
        Document doc,
        ViewSchedule schedule,
        ConfigureScheduleInfo info,
        List<string> warnings)
    {
        var def = schedule.Definition;

        // Width overrides
        foreach (var fw in info.FieldWidths)
        {
            var field = ResolveField(def, fw.FieldIndex, fw.ParameterName, warnings);
            if (field == null) continue;
            if (fw.WidthMm > 0)
                field.GridColumnWidth = fw.WidthMm / FeetToMm;
        }

        // Hide columns
        foreach (var name in info.HideFields)
        {
            var field = ResolveFieldByName(def, name, warnings);
            if (field != null) field.IsHidden = true;
        }

        // Show columns
        foreach (var name in info.ShowFields)
        {
            var field = ResolveFieldByName(def, name, warnings);
            if (field != null) field.IsHidden = false;
        }
    }

    private static ScheduleField ResolveField(
        ScheduleDefinition def,
        int fieldIndex,
        string parameterName,
        List<string> warnings)
    {
        if (fieldIndex >= 0 && fieldIndex < def.GetFieldCount())
            return def.GetField(def.GetFieldId(fieldIndex));

        return ResolveFieldByName(def, parameterName, warnings);
    }

    private static ScheduleField ResolveFieldByName(
        ScheduleDefinition def,
        string name,
        List<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;

        for (var i = 0; i < def.GetFieldCount(); i++)
        {
            var f = def.GetField(def.GetFieldId(i));
            if (f.GetName().Equals(name.Trim(), StringComparison.OrdinalIgnoreCase))
                return f;
        }

        warnings.Add($"Field '{name}' not found in schedule; skipped.");
        return null;
    }

    // ── Filter mutations ──────────────────────────────────────────────────────

    private static void ApplyFilterMutations(
        Document doc,
        ViewSchedule schedule,
        ConfigureScheduleInfo info,
        List<string> warnings)
    {
        if (!info.ClearExistingFilters && info.Filters.Count == 0)
            return;

        var def = schedule.Definition;
        if (info.ClearExistingFilters)
            def.ClearFilters();

        foreach (var fi in info.Filters)
        {
            try
            {
                var fieldId = ResolveScheduleFieldId(doc, def, fi.FieldName, fi.FieldIndex);
                if (fieldId == null)
                {
                    warnings.Add($"Filter field '{fi.FieldName}' not found; skipped.");
                    continue;
                }

                var filterType = ParseFilterType(fi.FilterType);
                if (fi.FilterElementId.HasValue && fi.FilterElementId.Value > 0)
                {
                    var eid = Utils.ElementIdExtensions.FromLong(fi.FilterElementId.Value);
                    def.AddFilter(new ScheduleFilter(fieldId, filterType, eid));
                }
                else
                {
                    def.AddFilter(new ScheduleFilter(fieldId, filterType, fi.FilterValue ?? string.Empty));
                }
            }
            catch (Exception ex)
            {
                warnings.Add($"Filter '{fi.FieldName}' error: {ex.Message}");
            }
        }
    }

    // ── Sort / group mutations ────────────────────────────────────────────────

    private static void ApplySortMutations(
        Document doc,
        ViewSchedule schedule,
        ConfigureScheduleInfo info,
        List<string> warnings)
    {
        var def = schedule.Definition;

        if (info.ClearExistingSorts || info.ClearExistingGroups)
            def.ClearSortGroupFields();

        foreach (var si in info.SortFields)
        {
            try
            {
                var fieldId = ResolveScheduleFieldId(doc, def, si.FieldName, si.FieldIndex);
                if (fieldId == null)
                {
                    warnings.Add($"Sort field '{si.FieldName}' not found; skipped.");
                    continue;
                }

                def.AddSortGroupField(
                    new ScheduleSortGroupField(fieldId, ParseSortOrder(si.SortOrder)));
            }
            catch (Exception ex)
            {
                warnings.Add($"Sort '{si.FieldName}' error: {ex.Message}");
            }
        }

        foreach (var gi in info.GroupFields)
        {
            try
            {
                var fieldId = ResolveScheduleFieldId(doc, def, gi.FieldName, gi.FieldIndex);
                if (fieldId == null)
                {
                    warnings.Add($"Group field '{gi.FieldName}' not found; skipped.");
                    continue;
                }

                def.AddSortGroupField(new ScheduleSortGroupField(fieldId, ParseSortOrder(gi.SortOrder))
                {
                    ShowHeader = gi.ShowHeader,
                    ShowFooter = gi.ShowFooter,
                    ShowBlankLine = gi.ShowBlankLine
                });
            }
            catch (Exception ex)
            {
                warnings.Add($"Group '{gi.FieldName}' error: {ex.Message}");
            }
        }
    }

    // ── Shared helpers ────────────────────────────────────────────────────────

    private static ScheduleFieldId ResolveScheduleFieldId(
        Document doc,
        ScheduleDefinition def,
        string fieldName,
        int fieldIndex)
    {
        if (fieldIndex >= 0 && fieldIndex < def.GetFieldCount())
            return def.GetFieldId(fieldIndex);

        if (!string.IsNullOrWhiteSpace(fieldName))
        {
            for (var i = 0; i < def.GetFieldCount(); i++)
            {
                var f = def.GetField(def.GetFieldId(i));
                if (f.GetName().Equals(fieldName.Trim(), StringComparison.OrdinalIgnoreCase))
                    return def.GetFieldId(i);
            }
        }

        return null;
    }

    private static ScheduleFilterType ParseFilterType(string v) =>
        v?.Trim().ToLowerInvariant() switch
        {
            "notequal" or "not_equals" or "notequals" => ScheduleFilterType.NotEqual,
            "greaterthan" or "greater_than"           => ScheduleFilterType.GreaterThan,
            "greaterthanorequal"                      => ScheduleFilterType.GreaterThanOrEqual,
            "lessthan" or "less_than"                 => ScheduleFilterType.LessThan,
            "lessthanorequal"                         => ScheduleFilterType.LessThanOrEqual,
            "contains"                                => ScheduleFilterType.Contains,
            "notcontains" or "not_contains"           => ScheduleFilterType.NotContains,
            "beginswith" or "begins_with"             => ScheduleFilterType.BeginsWith,
            "endswith" or "ends_with"                 => ScheduleFilterType.EndsWith,
            _                                         => ScheduleFilterType.Equal
        };

    private static ScheduleSortOrder ParseSortOrder(string v) =>
        v?.Trim().ToLowerInvariant() == "descending"
            ? ScheduleSortOrder.Descending
            : ScheduleSortOrder.Ascending;

    private static long GetIdValue(ElementId id)
    {
#if REVIT2024_OR_GREATER
        return id.Value;
#else
        return id.IntegerValue;
#endif
    }
}
