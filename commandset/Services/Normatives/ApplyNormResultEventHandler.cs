using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCPCommandSet.Models.Normatives;
using RevitMCPCommandSet.Utils;
using RevitMCPSDK.API.Interfaces;

namespace RevitMCPCommandSet.Services.Normatives
{
    public class ApplyNormResultEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
    {
        public const string ActionSetParameter = "set_parameter";
        public const string ActionSetMark = "set_mark";
        public const string ActionHighlight = "highlight";
        public const string ActionCreateSchedule = "create_schedule";

        public static readonly string[] SupportedActions =
        {
            ActionSetParameter, ActionSetMark, ActionHighlight, ActionCreateSchedule
        };

        private List<NormResultElement> _elements = new();
        private List<string> _actions = new();
        private string _normDocument = string.Empty;
        private string _normClause = string.Empty;
        private string _parameterName = "Comments";
        private string _valueTemplate = string.Empty;
        private string _markPrefix = "НК-";
        private string _scheduleName = string.Empty;
        private bool _preview = true;
        private bool _overwrite;
        private int[] _highlightColor = { 255, 0, 0 };

        public ApplyNormResultResult ResultInfo { get; private set; } = new();
        public bool TaskCompleted { get; private set; }
        private readonly ManualResetEvent _resetEvent = new(false);

        public void SetParameters(
            List<NormResultElement> elements,
            List<string> actions,
            string normDocument,
            string normClause,
            string parameterName,
            string valueTemplate,
            string markPrefix,
            string scheduleName,
            bool preview,
            bool overwrite,
            int[] highlightColor)
        {
            _elements = elements ?? new List<NormResultElement>();
            _actions = (actions ?? new List<string>())
                .Select(action => (action ?? string.Empty).Trim().ToLowerInvariant())
                .Where(action => SupportedActions.Contains(action))
                .Distinct()
                .ToList();
            _normDocument = normDocument ?? string.Empty;
            _normClause = normClause ?? string.Empty;
            _parameterName = string.IsNullOrWhiteSpace(parameterName) ? "Comments" : parameterName;
            _valueTemplate = valueTemplate ?? string.Empty;
            _markPrefix = string.IsNullOrWhiteSpace(markPrefix) ? "НК-" : markPrefix;
            _scheduleName = scheduleName ?? string.Empty;
            _preview = preview;
            _overwrite = overwrite;
            if (highlightColor is { Length: 3 })
            {
                _highlightColor = highlightColor;
            }
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
                if (_elements.Count == 0)
                {
                    ResultInfo = Fail("No elements provided.");
                    return;
                }
                if (_actions.Count == 0)
                {
                    ResultInfo = Fail(
                        $"No supported actions provided. Supported: {string.Join(", ", SupportedActions)}.");
                    return;
                }

                var doc = app.ActiveUIDocument.Document;
                var warnings = new List<string>();
                var changes = new List<NormResultChange>();
                var highlightIds = new List<ElementId>();
                var resolved = new List<(Element element, NormResultElement input)>();

                foreach (var input in _elements)
                {
                    var element = doc.GetElement(RevitMCPCommandSet.Utils.ElementIdExtensions.FromLong(input.ElementId));
                    if (element == null)
                    {
                        warnings.Add($"Element {input.ElementId} was not found and was skipped.");
                        continue;
                    }
                    resolved.Add((element, input));
                    highlightIds.Add(element.Id);
                }

                // Plan phase — identical for preview and write, so the preview
                // shows exactly what a subsequent write would do.
                int markCounter = 1;
                foreach (var (element, input) in resolved)
                {
                    if (_actions.Contains(ActionSetParameter))
                    {
                        var value = ComposeValue(_valueTemplate, _normDocument, _normClause, input.Note);
                        changes.Add(PlanParameterChange(
                            element, _parameterName, value, _overwrite, ActionSetParameter));
                    }

                    if (_actions.Contains(ActionSetMark))
                    {
                        var mark = $"{_markPrefix}{markCounter}";
                        var change = PlanParameterChange(
                            element, "Mark", mark, _overwrite, ActionSetMark);
                        changes.Add(change);
                        if (change.Status != NormResultChangeStatus.Skipped)
                        {
                            markCounter++;
                        }
                    }
                }

                int highlightedCount = 0;
                var schedules = new List<CreatedScheduleInfo>();

                if (!_preview)
                {
                    using var transaction = new Transaction(doc, "Apply Norm Result");
                    transaction.Start();
                    SuppressWarnings(transaction);

                    try
                    {
                        foreach (var change in changes.Where(
                                     c => c.Status == NormResultChangeStatus.Planned))
                        {
                            var element = doc.GetElement(RevitMCPCommandSet.Utils.ElementIdExtensions.FromLong(change.ElementId));
                            var parameter = ResolveParameter(element, change.ParameterName);
                            parameter.Set(change.NewValue);
                            change.Status = NormResultChangeStatus.Applied;
                        }

                        if (_actions.Contains(ActionHighlight))
                        {
                            highlightedCount = HighlightElements(app, doc, highlightIds);
                        }

                        if (_actions.Contains(ActionCreateSchedule))
                        {
                            schedules = CreateViolationSchedules(doc, resolved, warnings);
                        }

                        transaction.Commit();
                    }
                    catch
                    {
                        transaction.RollBack();
                        throw;
                    }
                }
                else if (_actions.Contains(ActionHighlight))
                {
                    highlightedCount = highlightIds.Count; // would be highlighted
                }

                int applied = changes.Count(c => c.Status == NormResultChangeStatus.Applied);
                int skipped = changes.Count(c => c.Status == NormResultChangeStatus.Skipped);

                ResultInfo = new ApplyNormResultResult
                {
                    Success = true,
                    Preview = _preview,
                    Message = _preview
                        ? $"Preview: {changes.Count(c => c.Status == NormResultChangeStatus.Planned)} changes planned, {skipped} skipped. Re-run with preview=false to write."
                        : $"Applied {applied} changes, skipped {skipped}.",
                    Actions = _actions,
                    TotalElements = resolved.Count,
                    AppliedCount = applied,
                    SkippedCount = skipped,
                    HighlightedCount = highlightedCount,
                    Changes = changes,
                    Schedules = schedules,
                    Warnings = warnings
                };
            }
            catch (Exception ex)
            {
                ResultInfo = Fail($"Failed to apply norm result (all changes rolled back): {ex.Message}");
            }
            finally
            {
                TaskCompleted = true;
                _resetEvent.Set();
            }
        }

        public string GetName() => "Apply Norm Result";

        /// <summary>
        /// Builds the parameter value. Template placeholders: {document}, {clause}, {note}.
        /// </summary>
        public static string ComposeValue(string template, string document, string clause, string note)
        {
            if (!string.IsNullOrWhiteSpace(template))
            {
                return template
                    .Replace("{document}", document ?? string.Empty)
                    .Replace("{clause}", clause ?? string.Empty)
                    .Replace("{note}", note ?? string.Empty)
                    .Trim();
            }

            var header = $"Нарушение {document} {clause}".Trim();
            return string.IsNullOrWhiteSpace(note) ? header : $"{header} — {note}";
        }

        /// <summary>
        /// Overwrite protection: an existing different value is only replaced
        /// when overwrite is explicitly requested.
        /// </summary>
        public static bool ShouldSkipWrite(string oldValue, string newValue, bool overwrite)
        {
            if (string.IsNullOrWhiteSpace(oldValue))
                return false;
            if (string.Equals(oldValue, newValue, StringComparison.Ordinal))
                return false;
            return !overwrite;
        }

        public static NormResultChange PlanParameterChange(
            Element element,
            string parameterName,
            string newValue,
            bool overwrite,
            string action)
        {
            var change = new NormResultChange
            {
                ElementId = element.Id.GetValue(),
                ElementName = element.Name ?? string.Empty,
                Category = element.Category?.Name ?? string.Empty,
                Action = action,
                ParameterName = parameterName,
                NewValue = newValue
            };

            Parameter parameter;
            try
            {
                parameter = ResolveParameter(element, parameterName);
            }
            catch (Exception ex)
            {
                change.Status = NormResultChangeStatus.Skipped;
                change.SkipReason = ex.Message;
                return change;
            }

            change.ParameterName = parameter.Definition.Name;
            change.OldValue = parameter.HasValue ? parameter.AsString() ?? string.Empty : string.Empty;

            if (parameter.StorageType != StorageType.String)
            {
                change.Status = NormResultChangeStatus.Skipped;
                change.SkipReason =
                    $"Parameter '{change.ParameterName}' is not a text parameter ({parameter.StorageType}).";
                return change;
            }
            if (parameter.IsReadOnly)
            {
                change.Status = NormResultChangeStatus.Skipped;
                change.SkipReason = $"Parameter '{change.ParameterName}' is read-only.";
                return change;
            }
            if (ShouldSkipWrite(change.OldValue, newValue, overwrite))
            {
                change.Status = NormResultChangeStatus.Skipped;
                change.SkipReason =
                    $"Existing value would be overwritten; pass overwrite=true to replace '{change.OldValue}'.";
                return change;
            }

            change.Status = NormResultChangeStatus.Planned;
            return change;
        }

        private static Parameter ResolveParameter(Element element, string parameterName)
        {
            var byName = ElementParameterHelper.FindParameter(element, parameterName);
            if (byName != null)
                return byName;

            // Localization-independent fallbacks: on a Russian Revit the visible
            // names are "Комментарии" / "Марка", so agents passing the English
            // aliases still hit the built-in parameters.
            var key = parameterName.Trim().ToLowerInvariant();
            var builtIn = key switch
            {
                "comments" or "комментарии" => BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS,
                "mark" or "марка" => BuiltInParameter.ALL_MODEL_MARK,
                "number" or "номер" => BuiltInParameter.ROOM_NUMBER,
                _ => BuiltInParameter.INVALID
            };

            var parameter = builtIn == BuiltInParameter.INVALID
                ? null
                : element.get_Parameter(builtIn);

            // Rooms have no ALL_MODEL_MARK — their visible identity is Number.
            if (parameter == null && (key is "mark" or "марка"))
            {
                parameter = element.get_Parameter(BuiltInParameter.ROOM_NUMBER);
            }

            return parameter
                ?? throw new ArgumentException(
                    $"Parameter '{parameterName}' was not found on element {element.Id.GetValue()}.");
        }

        /// <summary>
        /// Override Graphics in View → By Element → Projection Lines color.
        /// For rooms: colors visible Room Tags (name/area), same as UI on a selected марка помещения.
        /// For other elements: projection/cut line color on the element itself.
        /// </summary>
        private int HighlightElements(UIApplication app, Document doc, List<ElementId> elementIds)
        {
            var uidoc = app.ActiveUIDocument;
            var activeView = uidoc.ActiveView;

            var roomOrTagIds = new List<ElementId>();
            var otherIds = new List<ElementId>();

            foreach (var id in elementIds)
            {
                var element = doc.GetElement(id);
                if (element == null)
                {
                    continue;
                }

                if (element is Autodesk.Revit.DB.Architecture.Room
                    || element is Autodesk.Revit.DB.Architecture.RoomTag)
                {
                    roomOrTagIds.Add(id);
                }
                else
                {
                    otherIds.Add(id);
                }
            }

            int highlighted = 0;
            if (roomOrTagIds.Count > 0)
            {
                highlighted += ElementGraphicOverrides.HighlightRoomsAndTags(
                    activeView,
                    doc,
                    roomOrTagIds,
                    _highlightColor);
            }

            if (otherIds.Count > 0)
            {
                // Same UI path: Projection Lines color only (no solid room fill).
                highlighted += ElementGraphicOverrides.ApplyTagColorToView(
                    activeView,
                    otherIds,
                    _highlightColor);
            }

            if (elementIds.Count > 0)
            {
                uidoc.Selection.SetElementIds(elementIds);
                uidoc.ShowElements(elementIds);
            }

            return highlighted;
        }

        private List<CreatedScheduleInfo> CreateViolationSchedules(
            Document doc,
            List<(Element element, NormResultElement input)> resolved,
            List<string> warnings)
        {
            var schedules = new List<CreatedScheduleInfo>();
            var byCategory = resolved
                .Where(pair => pair.element.Category != null)
                .GroupBy(pair => pair.element.Category.Id);

            foreach (var group in byCategory)
            {
                var category = group.First().element.Category;
                try
                {
                    var schedule = ViewSchedule.CreateSchedule(doc, category.Id);
                    var baseName = string.IsNullOrWhiteSpace(_scheduleName)
                        ? $"Нормоконтроль {_normDocument} {_normClause}".Trim()
                        : _scheduleName;
                    schedule.Name = GetUniqueScheduleName(doc, $"{baseName} — {category.Name}");

                    AddScheduleFields(doc, schedule, warnings);
                    schedules.Add(new CreatedScheduleInfo
                    {
                        Id = schedule.Id.GetValue(),
                        Name = schedule.Name,
                        Category = category.Name
                    });
                }
                catch (Exception ex)
                {
                    warnings.Add(
                        $"Failed to create schedule for category '{category.Name}': {ex.Message}");
                }
            }

            return schedules;
        }

        private void AddScheduleFields(Document doc, ViewSchedule schedule, List<string> warnings)
        {
            var definition = schedule.Definition;
            var preferredNames = new[]
            {
                "Марка", "Mark", "Номер", "Number", "Имя", "Name",
                "Уровень", "Level", "Площадь", "Area", _parameterName,
                "Комментарии", "Comments"
            };

            ScheduleFieldId statusFieldId = null;
            var added = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var name in preferredNames)
            {
                var schedulable = definition.GetSchedulableFields()
                    .FirstOrDefault(field =>
                        string.Equals(field.GetName(doc), name, StringComparison.OrdinalIgnoreCase));
                if (schedulable == null || added.Contains(schedulable.GetName(doc)))
                    continue;

                var field = definition.AddField(schedulable);
                added.Add(schedulable.GetName(doc));

                bool isStatusField =
                    string.Equals(schedulable.GetName(doc), _parameterName, StringComparison.OrdinalIgnoreCase)
                    || (_parameterName.ToLowerInvariant() is "comments" or "комментарии"
                        && schedulable.GetName(doc).ToLowerInvariant() is "comments" or "комментарии");
                if (isStatusField)
                {
                    statusFieldId = field.FieldId;
                }
            }

            // The schedule only isolates violations when the status parameter is
            // written by this run (or a previous one) — filter by the clause.
            if (statusFieldId != null &&
                _actions.Contains(ActionSetParameter) &&
                !string.IsNullOrWhiteSpace(_normClause))
            {
                definition.AddFilter(new ScheduleFilter(
                    statusFieldId, ScheduleFilterType.Contains, _normClause));
            }
            else
            {
                warnings.Add(
                    "Schedule was created without a violation filter (status parameter not in schedule or set_parameter action not requested).");
            }
        }

        private static string GetUniqueScheduleName(Document doc, string baseName)
        {
            var existing = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSchedule))
                .Cast<ViewSchedule>()
                .Select(view => view.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (!existing.Contains(baseName))
                return baseName;

            for (int i = 2; ; i++)
            {
                var candidate = $"{baseName} ({i})";
                if (!existing.Contains(candidate))
                    return candidate;
            }
        }

        private static void SuppressWarnings(Transaction transaction)
        {
            var options = transaction.GetFailureHandlingOptions();
            options.SetFailuresPreprocessor(new WarningSwallower());
            transaction.SetFailureHandlingOptions(options);
        }

        /// <summary>Swallows non-error warnings (e.g. duplicate mark values) during commit.</summary>
        private class WarningSwallower : IFailuresPreprocessor
        {
            public FailureProcessingResult PreprocessFailures(FailuresAccessor failuresAccessor)
            {
                foreach (var failure in failuresAccessor.GetFailureMessages())
                {
                    if (failure.GetSeverity() == FailureSeverity.Warning)
                    {
                        failuresAccessor.DeleteWarning(failure);
                    }
                }
                return FailureProcessingResult.Continue;
            }
        }

        private static ApplyNormResultResult Fail(string message) =>
            new() { Success = false, Message = message };
    }
}
