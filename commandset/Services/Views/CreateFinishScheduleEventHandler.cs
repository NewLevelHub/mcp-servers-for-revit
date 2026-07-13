using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCPCommandSet.Models.Views;
using RevitMCPCommandSet.Services.DataExtraction;
using RevitMCPCommandSet.Utils;
using RevitMCPSDK.API.Interfaces;

namespace RevitMCPCommandSet.Services.Views;

public class CreateFinishScheduleEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
{
    public const string RoleKey = "key";
    public const string RoleData = "data";
    public const string RoleOutput = "output";

    private FinishScheduleCreationInfo _info;
    private readonly ManualResetEvent _resetEvent = new ManualResetEvent(false);

    public FinishScheduleCreationResult ResultInfo { get; private set; } = new FinishScheduleCreationResult();
    public bool TaskCompleted { get; private set; }

    public void SetParameters(FinishScheduleCreationInfo info)
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
        var stopwatch = Stopwatch.StartNew();
        var warnings = new List<string>();

        try
        {
            var doc = app.ActiveUIDocument.Document;

            var exportHandler = new ExportRoomFinishDataEventHandler();
            exportHandler.SetParameters(
                _info.IncludeUnplacedRooms,
                _info.IncludeNotEnclosedRooms,
                includeMaterials: false);
            exportHandler.Execute(app);

            var exportResult = exportHandler.ResultInfo
                ?? throw new InvalidOperationException("Room finish export returned no result.");

            if (!exportResult.Success)
                throw new InvalidOperationException(exportResult.Message ?? "Room finish export failed.");

            var totalRooms = exportResult.TotalRooms;
            var missingRooms = exportResult.RoomsWithMissingFinishes;
            var missingRatio = totalRooms > 0 ? (double)missingRooms / totalRooms : 0;

            var threshold = _info.MissingFinishWarningThreshold > 0
                ? _info.MissingFinishWarningThreshold
                : 0.30;

            if (totalRooms > 0 && missingRatio > threshold)
            {
                warnings.Add(
                    $"{missingRooms} of {totalRooms} rooms ({missingRatio:P0}) lack finish data — exceeds {threshold:P0} threshold.");
            }

            if (exportResult.Warnings.Count > 0)
                warnings.AddRange(exportResult.Warnings);

            FinishScheduleCreationResult result = null;

            // An explicit single templateId without chain hints keeps the pre-REV-38 behavior.
            var singleTemplateRequested =
                !string.IsNullOrWhiteSpace(_info.TemplateId) &&
                string.IsNullOrWhiteSpace(_info.KeyScheduleTemplateName) &&
                string.IsNullOrWhiteSpace(_info.DataScheduleTemplateName) &&
                (_info.OutputScheduleTemplateNames == null || _info.OutputScheduleTemplateNames.Count == 0);

            if (_info.ReproduceTemplateChain && !singleTemplateRequested)
            {
                var chainTemplates = DiscoverChainTemplates(doc, _info, warnings);
                if (chainTemplates.Count > 0)
                {
                    var chain = ReproduceChain(doc, chainTemplates, _info, warnings);
                    result = BuildChainResult(doc, chain);
                }
                else
                {
                    warnings.Add(
                        "No finish schedule chain templates (key/data/output) were found in the project; falling back to a single schedule.");
                }
            }

            if (result == null)
                result = CreateSingleSchedule(app, doc, warnings);

            stopwatch.Stop();

            result.TemplateId = _info.TemplateId ?? string.Empty;
            result.TotalRooms = totalRooms;
            result.RoomsWithMissingFinishes = missingRooms;
            result.MissingFinishRatio = Math.Round(missingRatio, 4);
            result.ExecutionTimeMs = stopwatch.ElapsedMilliseconds;
            result.Warnings = warnings;
            ResultInfo = result;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            ResultInfo = new FinishScheduleCreationResult
            {
                Success = false,
                Message = $"Error creating finish schedule: {ex.Message}",
                ExecutionTimeMs = stopwatch.ElapsedMilliseconds,
                Warnings = warnings
            };
        }
        finally
        {
            TaskCompleted = true;
            _resetEvent.Set();
        }
    }

    public string GetName() => "Create Finish Schedule";

    /// <summary>
    ///     One resolved template stage of the finish schedule chain
    /// </summary>
    public sealed class FinishChainTemplate
    {
        public string Role { get; set; }
        public ViewSchedule Schedule { get; set; }
    }

    /// <summary>
    ///     Resolve the chain templates: explicit names first, then ADSK_RU naming patterns
    ///     (В_Отделка-помещения-01_Стили_Ключевая → …-02_Заполнение данных → О_АР_Ведомость отделки помещений_*).
    ///     Returns stages ordered key → data → outputs; missing stages are reported as warnings.
    /// </summary>
    public static List<FinishChainTemplate> DiscoverChainTemplates(
        Document doc,
        FinishScheduleCreationInfo info,
        List<string> warnings)
    {
        var schedules = new FilteredElementCollector(doc)
            .OfClass(typeof(ViewSchedule))
            .Cast<ViewSchedule>()
            .Where(schedule => !schedule.IsTemplate)
            .ToList();

        var chain = new List<FinishChainTemplate>();

        var key = ResolveStageTemplate(
            schedules,
            info.KeyScheduleTemplateName,
            candidate => candidate.Definition.IsKeySchedule && ContainsFinishToken(candidate.Name),
            candidate => NameContains(candidate, "ключев") || NameContains(candidate, "01"),
            "key",
            warnings);
        if (key != null)
            chain.Add(new FinishChainTemplate { Role = RoleKey, Schedule = key });

        var data = ResolveStageTemplate(
            schedules,
            info.DataScheduleTemplateName,
            candidate => !candidate.Definition.IsKeySchedule &&
                         ContainsFinishToken(candidate.Name) &&
                         (NameContains(candidate, "заполнен") || NameContains(candidate, "02")),
            candidate => true,
            "data",
            warnings);
        if (data != null)
            chain.Add(new FinishChainTemplate { Role = RoleData, Schedule = data });

        var outputs = ResolveOutputTemplates(schedules, info, key, data, warnings);
        foreach (var output in outputs)
            chain.Add(new FinishChainTemplate { Role = RoleOutput, Schedule = output });

        if (chain.Count > 0 && chain.All(item => item.Role != RoleKey))
            warnings.Add("Key (styles) schedule template was not found; the chain is reproduced without it.");
        if (chain.Count > 0 && chain.All(item => item.Role != RoleData))
            warnings.Add("Data-filling schedule template was not found; the chain is reproduced without it.");
        if (chain.Count > 0 && chain.All(item => item.Role != RoleOutput))
            warnings.Add("Output finish list template was not found; the chain is reproduced without it.");

        return chain;
    }

    /// <summary>
    ///     Duplicate the chain stages in template order. The key schedule is reused by default:
    ///     its duplicate would define a new key parameter and break the link to the data schedule.
    /// </summary>
    public static List<FinishScheduleChainItem> ReproduceChain(
        Document doc,
        IReadOnlyList<FinishChainTemplate> templates,
        FinishScheduleCreationInfo info,
        List<string> warnings)
    {
        var items = new List<FinishScheduleChainItem>();
        var baseName = string.IsNullOrWhiteSpace(info.Name) ? "Ведомость отделки помещений" : info.Name.Trim();
        var outputCount = templates.Count(template => template.Role == RoleOutput);
        var outputIndex = 0;

        using (var tx = new Transaction(doc, "Create Finish Schedule Chain"))
        {
            tx.Start();

            foreach (var template in templates)
            {
                if (template.Role == RoleOutput)
                    outputIndex++;

                if (template.Role == RoleKey && !info.DuplicateKeySchedule)
                {
                    items.Add(CreateChainItem(template, template.Schedule, reused: true));
                    continue;
                }

                try
                {
                    var duplicateId = template.Schedule.Duplicate(ViewDuplicateOption.Duplicate);
                    var duplicate = doc.GetElement(duplicateId) as ViewSchedule
                        ?? throw new InvalidOperationException("Duplicate returned no schedule view.");

                    duplicate.Name = GetUniqueScheduleName(
                        doc,
                        BuildStageName(baseName, template, outputIndex, outputCount));

                    if (template.Role == RoleKey)
                    {
                        warnings.Add(
                            $"Key schedule '{template.Schedule.Name}' was duplicated; the duplicate defines its own key parameter and is not linked to the data schedule.");
                    }
                    else
                    {
                        SyncRoomPhase(doc, duplicate, warnings);
                    }

                    items.Add(CreateChainItem(template, duplicate, reused: false));
                }
                catch (Exception ex)
                {
                    warnings.Add(
                        $"Failed to duplicate {template.Role} schedule '{template.Schedule.Name}': {ex.Message}; the template itself is referenced instead.");
                    items.Add(CreateChainItem(template, template.Schedule, reused: true));
                }
            }

            tx.Commit();
        }

        return items;
    }

    private static FinishScheduleCreationResult BuildChainResult(
        Document doc,
        List<FinishScheduleChainItem> chain)
    {
        var primary = chain.FirstOrDefault(item => item.Role == RoleOutput)
            ?? chain.FirstOrDefault(item => item.Role == RoleData)
            ?? chain.First();

        var primarySchedule = doc.GetElement(primary.ScheduleUniqueId) as ViewSchedule;
        var stages = string.Join(" → ", chain.Select(item => $"{item.Role}: '{item.ScheduleName}'"));

        return new FinishScheduleCreationResult
        {
            Success = true,
            Message = $"Successfully reproduced finish schedule chain ({stages}).",
            ChainReproduced = true,
            Chain = chain,
            ScheduleId = primary.ScheduleId,
            ScheduleUniqueId = primary.ScheduleUniqueId,
            ScheduleName = primary.ScheduleName,
            FieldCount = primarySchedule?.Definition.GetFieldCount() ?? 0
        };
    }

    private FinishScheduleCreationResult CreateSingleSchedule(
        UIApplication app,
        Document doc,
        List<string> warnings)
    {
        var scheduleInfo = BuildScheduleCreationInfo(doc, _info, warnings);
        var scheduleHandler = new CreateScheduleEventHandler();
        scheduleHandler.SetParameters(scheduleInfo);
        scheduleHandler.Execute(app);

        var scheduleResult = scheduleHandler.ResultInfo;
        if (scheduleResult == null || !scheduleResult.Success)
        {
            throw new InvalidOperationException(
                scheduleResult?.Message ?? "Failed to create room finish schedule.");
        }

        if (scheduleResult.Warnings.Count > 0)
            warnings.AddRange(scheduleResult.Warnings);

        return new FinishScheduleCreationResult
        {
            Success = true,
            Message = $"Successfully created finish schedule '{scheduleResult.ScheduleName}'",
            ScheduleId = scheduleResult.ScheduleId,
            ScheduleUniqueId = scheduleResult.ScheduleUniqueId,
            ScheduleName = scheduleResult.ScheduleName,
            FieldCount = scheduleResult.FieldCount
        };
    }

    private static FinishScheduleChainItem CreateChainItem(
        FinishChainTemplate template,
        ViewSchedule schedule,
        bool reused)
    {
        return new FinishScheduleChainItem
        {
            Role = template.Role,
            TemplateId = template.Schedule.Id.GetValue(),
            TemplateName = template.Schedule.Name,
            ScheduleId = schedule.Id.GetValue(),
            ScheduleUniqueId = schedule.UniqueId,
            ScheduleName = schedule.Name,
            ReusedExisting = reused
        };
    }

    private static string BuildStageName(
        string baseName,
        FinishChainTemplate template,
        int outputIndex,
        int outputCount)
    {
        switch (template.Role)
        {
            case RoleKey:
                return $"{baseName}_01_Стили_Ключевая";
            case RoleData:
                return $"{baseName}_02_Заполнение данных";
            default:
                if (outputCount <= 1)
                    return baseName;

                var tail = ExtractNameTail(template.Schedule.Name);
                return string.IsNullOrEmpty(tail)
                    ? $"{baseName} ({outputIndex})"
                    : $"{baseName}_{tail}";
        }
    }

    /// <summary>
    ///     Short distinguishing suffix of a template name, e.g. 'Имя' from
    ///     'О_АР_Ведомость отделки помещений_Имя'
    /// </summary>
    private static string ExtractNameTail(string templateName)
    {
        var index = templateName?.LastIndexOf('_') ?? -1;
        if (index < 0 || index >= templateName.Length - 1)
            return string.Empty;

        var tail = templateName.Substring(index + 1).Trim();
        return tail.Length <= 16 ? tail : string.Empty;
    }

    private static ViewSchedule ResolveStageTemplate(
        IReadOnlyList<ViewSchedule> schedules,
        string explicitName,
        Func<ViewSchedule, bool> matches,
        Func<ViewSchedule, bool> prefers,
        string roleLabel,
        List<string> warnings)
    {
        if (!string.IsNullOrWhiteSpace(explicitName))
        {
            var byName = schedules.FirstOrDefault(schedule =>
                schedule.Name.Equals(explicitName.Trim(), StringComparison.OrdinalIgnoreCase));
            if (byName != null)
                return byName;

            warnings.Add(
                $"Requested {roleLabel} schedule template '{explicitName}' was not found; trying auto-discovery.");
        }

        var candidates = schedules.Where(matches).OrderBy(schedule => schedule.Name).ToList();
        if (candidates.Count == 0)
            return null;

        return candidates.FirstOrDefault(prefers) ?? candidates[0];
    }

    private static List<ViewSchedule> ResolveOutputTemplates(
        IReadOnlyList<ViewSchedule> schedules,
        FinishScheduleCreationInfo info,
        ViewSchedule key,
        ViewSchedule data,
        List<string> warnings)
    {
        var outputs = new List<ViewSchedule>();

        if (info.OutputScheduleTemplateNames != null && info.OutputScheduleTemplateNames.Count > 0)
        {
            foreach (var name in info.OutputScheduleTemplateNames)
            {
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                var byName = schedules.FirstOrDefault(schedule =>
                    schedule.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));
                if (byName != null)
                    outputs.Add(byName);
                else
                    warnings.Add($"Requested output schedule template '{name}' was not found.");
            }

            if (outputs.Count > 0)
                return outputs;
        }

        return schedules
            .Where(schedule => !schedule.Definition.IsKeySchedule &&
                               NameContains(schedule, "ведомость") &&
                               ContainsFinishToken(schedule.Name) &&
                               schedule.Id != (key?.Id ?? ElementId.InvalidElementId) &&
                               schedule.Id != (data?.Id ?? ElementId.InvalidElementId))
            .OrderBy(schedule => schedule.Name)
            .ToList();
    }

    private static bool ContainsFinishToken(string name)
    {
        return name?.IndexOf("отделк", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool NameContains(ViewSchedule schedule, string token)
    {
        return schedule.Name.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static void SyncRoomPhase(Document doc, ViewSchedule schedule, List<string> warnings)
    {
        try
        {
            if (schedule.Definition.CategoryId.GetValue() != (long)(int)BuiltInCategory.OST_Rooms)
                return;

            var visibleRooms = new FilteredElementCollector(doc, schedule.Id)
                .OfCategory(BuiltInCategory.OST_Rooms)
                .WhereElementIsNotElementType()
                .GetElementCount();
            if (visibleRooms > 0)
                return;

            var room = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Rooms)
                .WhereElementIsNotElementType()
                .FirstOrDefault(element => element.get_Parameter(BuiltInParameter.ROOM_AREA)?.AsDouble() > 0);

            var roomPhaseId = room?.get_Parameter(BuiltInParameter.ROOM_PHASE)?.AsElementId();
            if (roomPhaseId == null || roomPhaseId == ElementId.InvalidElementId)
                return;

            var phaseParam = schedule.get_Parameter(BuiltInParameter.VIEW_PHASE);
            if (phaseParam != null && !phaseParam.IsReadOnly)
            {
                phaseParam.Set(roomPhaseId);
                var phaseName = doc.GetElement(roomPhaseId)?.Name ?? roomPhaseId.ToString();
                warnings.Add(
                    $"Schedule '{schedule.Name}' view phase set to '{phaseName}' so placed rooms appear in it.");
            }
        }
        catch (Exception ex)
        {
            warnings.Add($"Failed to sync view phase for '{schedule.Name}': {ex.Message}");
        }
    }

    private static ScheduleCreationInfo BuildScheduleCreationInfo(
        Document doc,
        FinishScheduleCreationInfo info,
        List<string> warnings)
    {
        var template = ResolveTemplateSchedule(doc, info.TemplateId);
        var hasTemplate = template != null;
        var scheduleInfo = new ScheduleCreationInfo
        {
            Name = string.IsNullOrWhiteSpace(info.Name) ? "Room Finish Schedule" : info.Name.Trim(),
            Type = string.IsNullOrWhiteSpace(info.Type) ? "Regular" : info.Type.Trim(),
            CategoryName = "Rooms",
            TemplateId = info.TemplateId ?? string.Empty,
            ClearExistingFilters = false,
            ClearExistingSorts = !hasTemplate,
            ClearExistingGroups = !hasTemplate,
            ShowTitle = true,
            ShowHeaders = true,
            ShowGridLines = true,
            SortFields = new List<ScheduleSortInfo>
            {
                new ScheduleSortInfo { FieldName = "Number", SortOrder = "Ascending" }
            }
        };

        if (hasTemplate)
        {
            warnings.Add($"Using project schedule template '{template.Name}'.");
            scheduleInfo.Fields = new List<ScheduleFieldInfo>();
            scheduleInfo.SortFields = new List<ScheduleSortInfo>();
            return scheduleInfo;
        }

        if (!string.IsNullOrWhiteSpace(info.TemplateId))
            warnings.Add($"Template '{info.TemplateId}' was not found; created schedule with default finish columns.");

        scheduleInfo.Fields = new List<ScheduleFieldInfo>
        {
            new() { ParameterName = "Number", FieldType = "Instance", Heading = "№" },
            new() { ParameterName = "Name", FieldType = "Instance", Heading = "Помещение" },
            new() { ParameterName = "Level", FieldType = "Instance", Heading = "Уровень" },
            new() { ParameterName = "Area", FieldType = "Instance", Heading = "Площадь" },
            new() { ParameterName = "Floor Finish", FieldType = "Instance", Heading = "Пол" },
            new() { ParameterName = "Wall Finish", FieldType = "Instance", Heading = "Стены" },
            new() { ParameterName = "Ceiling Finish", FieldType = "Instance", Heading = "Потолок" }
        };

        return scheduleInfo;
    }

    private static ViewSchedule ResolveTemplateSchedule(Document doc, string templateId)
    {
        if (string.IsNullOrWhiteSpace(templateId))
            return null;

        Element element = doc.GetElement(templateId);
        if (element == null && int.TryParse(templateId, out var numericId))
            element = doc.GetElement(new ElementId(numericId));

        return element as ViewSchedule;
    }

    private static string GetUniqueScheduleName(Document doc, string requestedName)
    {
        var name = requestedName;
        var suffix = 1;
        while (ScheduleNameExists(doc, name))
            name = $"{requestedName} ({suffix++})";

        return name;
    }

    private static bool ScheduleNameExists(Document doc, string name)
    {
        return new FilteredElementCollector(doc)
            .OfClass(typeof(ViewSchedule))
            .Cast<ViewSchedule>()
            .Any(schedule => schedule.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }
}
