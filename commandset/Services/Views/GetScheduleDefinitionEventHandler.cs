using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCPCommandSet.Models.Views;
using RevitMCPSDK.API.Interfaces;

namespace RevitMCPCommandSet.Services.Views;

public class GetScheduleDefinitionEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
{
    private const double FeetToMillimeters = 304.8;

    private long? _scheduleId;
    private string _scheduleUniqueId;
    private string _scheduleName;

    public ScheduleDefinitionResult ResultInfo { get; private set; } = new ScheduleDefinitionResult();
    public bool TaskCompleted { get; private set; }
    private readonly ManualResetEvent _resetEvent = new ManualResetEvent(false);

    public void SetParameters(long? scheduleId = null, string scheduleUniqueId = null, string scheduleName = null)
    {
        _scheduleId = scheduleId;
        _scheduleUniqueId = scheduleUniqueId;
        _scheduleName = scheduleName;
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
            ResultInfo = Compute(doc, _scheduleId, _scheduleUniqueId, _scheduleName);
        }
        catch (Exception ex)
        {
            ResultInfo = new ScheduleDefinitionResult
            {
                Success = false,
                Message = $"Error reading schedule definition: {ex.Message}",
                ScheduleName = _scheduleName ?? string.Empty
            };
        }
        finally
        {
            TaskCompleted = true;
            _resetEvent.Set();
        }
    }

    public static ScheduleDefinitionResult Compute(
        Document doc,
        long? scheduleId = null,
        string scheduleUniqueId = null,
        string scheduleName = null)
    {
        if (doc == null)
            throw new ArgumentNullException(nameof(doc));

        var schedule = ResolveSchedule(doc, scheduleId, scheduleUniqueId, scheduleName);
        if (schedule == null)
        {
            var hint = BuildNotFoundMessage(scheduleId, scheduleUniqueId, scheduleName);
            return new ScheduleDefinitionResult
            {
                Success = false,
                Message = hint,
                ScheduleName = scheduleName ?? string.Empty
            };
        }

        var definition = schedule.Definition;
        var category = Category.GetCategory(doc, definition.CategoryId);

        return new ScheduleDefinitionResult
        {
            Success = true,
            Message = $"Successfully read schedule definition for '{schedule.Name}'.",
            ScheduleId = GetElementIdValue(schedule.Id),
            ScheduleUniqueId = schedule.UniqueId,
            ScheduleName = schedule.Name,
            IsTemplate = schedule.IsTemplate,
            CategoryId = (int)GetElementIdValue(definition.CategoryId),
            CategoryName = category?.Name ?? string.Empty,
            Type = ResolveScheduleType(definition),
            ShowTitle = definition.ShowTitle,
            ShowHeaders = definition.ShowHeaders,
            ShowGridLines = definition.ShowGridLines,
            Fields = ReadFields(doc, definition),
            Filters = ReadFilters(doc, definition),
            SortFields = ReadSortFields(doc, definition),
            GroupFields = ReadGroupFields(doc, definition)
        };
    }

    public string GetName() => "Get Schedule Definition";

    private static string BuildNotFoundMessage(long? scheduleId, string scheduleUniqueId, string scheduleName)
    {
        if (!string.IsNullOrWhiteSpace(scheduleUniqueId))
            return $"Schedule with uniqueId '{scheduleUniqueId}' was not found.";

        if (scheduleId.HasValue)
            return $"Schedule with id {scheduleId.Value} was not found.";

        if (!string.IsNullOrWhiteSpace(scheduleName))
            return $"Schedule named '{scheduleName}' was not found.";

        return "Schedule identifier is required. Provide scheduleId, scheduleUniqueId, or scheduleName.";
    }

    private static ViewSchedule ResolveSchedule(
        Document doc,
        long? scheduleId,
        string scheduleUniqueId,
        string scheduleName)
    {
        if (!string.IsNullOrWhiteSpace(scheduleUniqueId))
        {
            var byUniqueId = doc.GetElement(scheduleUniqueId.Trim()) as ViewSchedule;
            if (byUniqueId != null)
                return byUniqueId;
        }

        if (scheduleId.HasValue)
        {
#if REVIT2024_OR_GREATER
            var byId = doc.GetElement(new ElementId(scheduleId.Value)) as ViewSchedule;
#else
            var byId = doc.GetElement(new ElementId((int)scheduleId.Value)) as ViewSchedule;
#endif
            if (byId != null)
                return byId;
        }

        if (!string.IsNullOrWhiteSpace(scheduleName))
        {
            return new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSchedule))
                .Cast<ViewSchedule>()
                .FirstOrDefault(schedule =>
                    schedule.Name.Equals(scheduleName.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        return null;
    }

    private static List<ScheduleDefinitionFieldInfo> ReadFields(Document doc, ScheduleDefinition definition)
    {
        var fields = new List<ScheduleDefinitionFieldInfo>();
        var fieldCount = definition.GetFieldCount();

        for (var index = 0; index < fieldCount; index++)
        {
            var fieldId = definition.GetFieldId(index);
            var field = definition.GetField(fieldId);

            fields.Add(new ScheduleDefinitionFieldInfo
            {
                FieldIndex = index,
                ParameterId = (int)GetElementIdValue(field.ParameterId),
                ParameterName = field.GetName(),
                FieldType = FormatFieldType(field.FieldType),
                Heading = field.ColumnHeading ?? string.Empty,
                IsCalculatedField = field.IsCalculatedField,
                Formula = ReadFormula(field),
                Width = Math.Round(field.GridColumnWidth * FeetToMillimeters, 2),
                IsHidden = field.IsHidden,
                HorizontalAlignment = FormatHorizontalAlignment(field.HorizontalAlignment)
            });
        }

        return fields;
    }

    private static List<ScheduleFilterInfo> ReadFilters(Document doc, ScheduleDefinition definition)
    {
        var filters = new List<ScheduleFilterInfo>();
        var filterCount = definition.GetFilterCount();

        for (var index = 0; index < filterCount; index++)
        {
            var filter = definition.GetFilter(index);
            var fieldIndex = ResolveFieldIndex(definition, filter.FieldId);
            var fieldName = definition.GetField(filter.FieldId).GetName();

            filters.Add(new ScheduleFilterInfo
            {
                FieldName = fieldName,
                FieldIndex = fieldIndex,
                FilterType = FormatFilterType(filter.FilterType),
                FilterValue = ReadFilterValue(filter)
            });
        }

        return filters;
    }

    private static List<ScheduleSortInfo> ReadSortFields(Document doc, ScheduleDefinition definition)
    {
        var sortFields = new List<ScheduleSortInfo>();
        var sortGroupCount = definition.GetSortGroupFieldCount();

        for (var index = 0; index < sortGroupCount; index++)
        {
            var sortGroupField = definition.GetSortGroupField(index);
            if (IsGroupSortGroupField(sortGroupField))
                continue;

            var field = definition.GetField(sortGroupField.FieldId);
            sortFields.Add(new ScheduleSortInfo
            {
                FieldName = field.GetName(),
                FieldIndex = ResolveFieldIndex(definition, sortGroupField.FieldId),
                SortOrder = FormatSortOrder(sortGroupField.SortOrder)
            });
        }

        return sortFields;
    }

    private static List<ScheduleGroupInfo> ReadGroupFields(Document doc, ScheduleDefinition definition)
    {
        var groupFields = new List<ScheduleGroupInfo>();
        var sortGroupCount = definition.GetSortGroupFieldCount();

        for (var index = 0; index < sortGroupCount; index++)
        {
            var sortGroupField = definition.GetSortGroupField(index);
            if (!IsGroupSortGroupField(sortGroupField))
                continue;

            var field = definition.GetField(sortGroupField.FieldId);
            groupFields.Add(new ScheduleGroupInfo
            {
                FieldName = field.GetName(),
                FieldIndex = ResolveFieldIndex(definition, sortGroupField.FieldId),
                SortOrder = FormatSortOrder(sortGroupField.SortOrder),
                ShowHeader = sortGroupField.ShowHeader,
                ShowFooter = sortGroupField.ShowFooter,
                ShowBlankLine = sortGroupField.ShowBlankLine
            });
        }

        return groupFields;
    }

    private static int ResolveFieldIndex(ScheduleDefinition definition, ScheduleFieldId fieldId)
    {
        for (var index = 0; index < definition.GetFieldCount(); index++)
        {
            if (definition.GetFieldId(index) == fieldId)
                return index;
        }

        return -1;
    }

    private static string ReadFormula(ScheduleField field)
    {
        if (!field.IsCalculatedField)
            return string.Empty;

        try
        {
            // GetFormula is not present in every Revit API assembly covered by this
            // multi-target command set. Resolve it at runtime where supported.
            var getFormula = typeof(ScheduleField).GetMethod("GetFormula", Type.EmptyTypes);
            return getFormula?.Invoke(field, null) as string ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static bool IsGroupSortGroupField(ScheduleSortGroupField sortGroupField)
    {
        return sortGroupField.ShowHeader || sortGroupField.ShowFooter || sortGroupField.ShowBlankLine;
    }

    private static string ReadFilterValue(ScheduleFilter filter)
    {
        try
        {
            return filter.GetStringValue() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string ResolveScheduleType(ScheduleDefinition definition)
    {
        if (definition.IsKeySchedule)
            return "KeySchedule";

        if (definition.IsMaterialTakeoff)
            return "MaterialTakeoff";

        return "Regular";
    }

    private static string FormatFieldType(ScheduleFieldType fieldType)
    {
        return fieldType switch
        {
            ScheduleFieldType.ElementType => "Type",
            ScheduleFieldType.Count => "Count",
            ScheduleFieldType.Formula => "Formula",
            _ => "Instance"
        };
    }

    private static string FormatHorizontalAlignment(ScheduleHorizontalAlignment alignment)
    {
        return alignment switch
        {
            ScheduleHorizontalAlignment.Center => "Center",
            ScheduleHorizontalAlignment.Right => "Right",
            _ => "Left"
        };
    }

    private static string FormatFilterType(ScheduleFilterType filterType)
    {
        return filterType switch
        {
            ScheduleFilterType.NotEqual => "NotEqual",
            ScheduleFilterType.GreaterThan => "GreaterThan",
            ScheduleFilterType.GreaterThanOrEqual => "GreaterThanOrEqual",
            ScheduleFilterType.LessThan => "LessThan",
            ScheduleFilterType.LessThanOrEqual => "LessThanOrEqual",
            ScheduleFilterType.Contains => "Contains",
            ScheduleFilterType.NotContains => "NotContains",
            ScheduleFilterType.BeginsWith => "BeginsWith",
            ScheduleFilterType.EndsWith => "EndsWith",
            _ => "Equal"
        };
    }

    private static string FormatSortOrder(ScheduleSortOrder sortOrder)
    {
        return sortOrder == ScheduleSortOrder.Descending ? "Descending" : "Ascending";
    }

    private static long GetElementIdValue(ElementId elementId)
    {
#if REVIT2024_OR_GREATER
        return elementId.Value;
#else
        return elementId.IntegerValue;
#endif
    }
}
