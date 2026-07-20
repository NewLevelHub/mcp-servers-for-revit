using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCPCommandSet.Models.Views;
using RevitMCPCommandSet.Utils;
using RevitMCPSDK.API.Interfaces;

namespace RevitMCPCommandSet.Services.Views
{
    /// <summary>
    /// Create floor экспликация from seeded «Короткий блок» recipe (columns),
    /// optionally split by discovered finish-floor levels / typified ranges (REV-49 B).
    /// </summary>
    public class CreateFloorExplicationEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
    {
        private FloorExplicationCreationInfo _info;
        private readonly ManualResetEvent _resetEvent = new ManualResetEvent(false);

        public FloorExplicationCreationResult ResultInfo { get; private set; } = new FloorExplicationCreationResult();
        public bool TaskCompleted { get; private set; }

        public void SetParameters(FloorExplicationCreationInfo info)
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
                var useDuplicate = ShouldDuplicateTemplate(_info);

                if (useDuplicate)
                {
                    ResultInfo = CreateFromTemplateDuplicate(app, doc, warnings, stopwatch);
                    return;
                }

                var split = _info.SplitByLevelGroups;
                if (split)
                {
                    ResultInfo = CreateSplitByLevelGroups(app, doc, warnings, stopwatch);
                    return;
                }

                ResultInfo = CreateSingleFromRecipe(app, doc, warnings, stopwatch);
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                ResultInfo = new FloorExplicationCreationResult
                {
                    Success = false,
                    Message = $"Error creating floor экспликация: {ex.Message}",
                    Warnings = warnings,
                    ExecutionTimeMs = stopwatch.ElapsedMilliseconds
                };
            }
            finally
            {
                TaskCompleted = true;
                _resetEvent.Set();
            }
        }

        public string GetName() => "Create Floor Explication";

        private FloorExplicationCreationResult CreateSplitByLevelGroups(
            UIApplication app,
            Document doc,
            List<string> warnings,
            Stopwatch stopwatch)
        {
            var levels = GetProjectLevels(doc);
            var allLevelIds = levels.Select(l => GetElementIdValue(l.Id)).ToList();
            var created = new List<FloorExplicationCreatedItem>();
            var pendingSchedules = new List<(FloorExplicationLevelGroup Group, List<Level> Levels, ScheduleCreationResult Schedule)>();

            var discovered = FloorExplicationLevelDiscoverer.Discover(doc);
            warnings.Add(
                "Built from seeded recipe (columns). Level groups discovered from (полы)* " +
                "by type + same XY footprint; consecutive storeys merge as типовой range. " +
                "All schedules place on one sheet. Area uses Totals.");

            foreach (var discoveredGroup in discovered)
            {
                var matched = discoveredGroup.Levels;
                if (matched == null || matched.Count == 0)
                    continue;

                var group = new FloorExplicationLevelGroup
                {
                    Key = discoveredGroup.Key,
                    Title = discoveredGroup.Title,
                    ScheduleNameSuffix = discoveredGroup.Key
                };

                if (discoveredGroup.IsTypicalRange)
                {
                    warnings.Add(
                        $"Typified range '{discoveredGroup.Title}': same finish type(s) and plan " +
                        $"on {string.Join(", ", matched.Select(l => l.Name))}.");
                }

                var includeIds = matched.Select(l => GetElementIdValue(l.Id)).ToList();
                var scheduleInfo = FloorExplicationScheduleRecipe.Build(group.Title, includeIds, allLevelIds);

                var scheduleResult = RunCreateSchedule(app, scheduleInfo);
                if (scheduleResult == null || !scheduleResult.Success)
                {
                    warnings.Add(
                        $"Failed group '{group.Title}': {scheduleResult?.Message ?? "unknown error"}");
                    continue;
                }

                if (scheduleResult.Warnings != null)
                    warnings.AddRange(scheduleResult.Warnings);

                pendingSchedules.Add((group, matched, scheduleResult));
            }

            if (pendingSchedules.Count == 0)
            {
                warnings.Add(
                    "No levels with explicit finish floors ((полы)*); " +
                    "creating a single экспликация schedule for all finish floors.");

                var scheduleInfo = FloorExplicationScheduleRecipe.Build(
                    string.IsNullOrWhiteSpace(_info.Name)
                        ? "Экспликация полов"
                        : _info.Name.Trim());
                var scheduleResult = RunCreateSchedule(app, scheduleInfo);
                if (scheduleResult == null || !scheduleResult.Success)
                {
                    throw new InvalidOperationException(
                        scheduleResult?.Message ?? "Failed to create floor экспликация schedule.");
                }

                if (scheduleResult.Warnings != null)
                    warnings.AddRange(scheduleResult.Warnings);

                return FinishWithOptionalSheet(
                    app,
                    doc,
                    scheduleResult,
                    FloorExplicationScheduleRecipe.RecipeId,
                    templateName: "",
                    sheetNameOverride: string.IsNullOrWhiteSpace(_info.SheetName)
                        ? "Экспликация полов"
                        : _info.SheetName,
                    warnings,
                    stopwatch);
            }

            long? sheetId = null;
            string sheetUniqueId = null;
            string sheetNumber = null;
            string sheetName = null;
            var placementBySchedule = new Dictionary<long, SheetRef>();

            if (_info.PlaceOnSheet)
            {
                try
                {
                    if (_info.AutoLayout)
                    {
                        var primary = LayoutAcrossSheets(
                            app, doc, pendingSchedules, placementBySchedule, warnings);
                        if (primary != null)
                        {
                            sheetId = primary.SheetId;
                            sheetUniqueId = primary.SheetUniqueId;
                            sheetNumber = primary.SheetNumber;
                            sheetName = primary.SheetName;
                        }
                    }
                    else
                    {
                        var sheetResult = CreateSheet(
                            app,
                            doc,
                            sheetNumberOverride: string.IsNullOrWhiteSpace(_info.SheetNumber)
                                ? null
                                : _info.SheetNumber.Trim(),
                            sheetNameOverride: string.IsNullOrWhiteSpace(_info.SheetName)
                                ? "Экспликация полов"
                                : _info.SheetName.Trim(),
                            warnings);

                        sheetId = sheetResult.SheetId;
                        sheetUniqueId = sheetResult.SheetUniqueId;
                        sheetNumber = sheetResult.SheetNumber;
                        sheetName = sheetResult.SheetName;

                        // Legacy fallback: stack schedules top→bottom on one sheet.
                        var y = Math.Max(_info.PositionY, 40);
                        const double stepMm = 90;
                        var primaryRef = SheetRef.From(sheetResult);
                        for (var i = 0; i < pendingSchedules.Count; i++)
                        {
                            var item = pendingSchedules[i];
                            var placeY = y + (pendingSchedules.Count - 1 - i) * stepMm;
                            PlaceScheduleOnSheet(
                                app,
                                item.Schedule,
                                sheetResult,
                                warnings,
                                positionYOverride: placeY);
                            placementBySchedule[item.Schedule.ScheduleId] = primaryRef;
                        }
                    }
                }
                catch (Exception sheetEx)
                {
                    warnings.Add(
                        $"Schedules were created, but sheet placement failed: {sheetEx.Message}");
                }
            }

            foreach (var item in pendingSchedules)
            {
                placementBySchedule.TryGetValue(item.Schedule.ScheduleId, out var placed);
                created.Add(new FloorExplicationCreatedItem
                {
                    GroupKey = item.Group.Key,
                    Title = item.Group.Title,
                    LevelNames = item.Levels.Select(l => l.Name).ToList(),
                    ScheduleId = item.Schedule.ScheduleId,
                    ScheduleUniqueId = item.Schedule.ScheduleUniqueId,
                    ScheduleName = item.Schedule.ScheduleName,
                    SheetId = placed?.SheetId ?? sheetId,
                    SheetUniqueId = placed?.SheetUniqueId ?? sheetUniqueId,
                    SheetNumber = placed?.SheetNumber ?? sheetNumber,
                    SheetName = placed?.SheetName ?? sheetName
                });
            }

            stopwatch.Stop();
            var first = created[0];
            return new FloorExplicationCreationResult
            {
                Success = true,
                Message =
                    $"Created {created.Count} floor экспликация schedule(s) on " +
                    (sheetId.HasValue ? $"sheet '{sheetNumber}'." : "no sheet."),
                ScheduleId = first.ScheduleId,
                ScheduleUniqueId = first.ScheduleUniqueId,
                ScheduleName = first.ScheduleName,
                Source = FloorExplicationScheduleRecipe.RecipeId,
                SheetId = sheetId,
                SheetUniqueId = sheetUniqueId,
                SheetNumber = sheetNumber,
                SheetName = sheetName,
                Created = created,
                Warnings = warnings,
                ExecutionTimeMs = stopwatch.ElapsedMilliseconds
            };
        }

        private FloorExplicationCreationResult CreateSingleFromRecipe(
            UIApplication app,
            Document doc,
            List<string> warnings,
            Stopwatch stopwatch)
        {
            warnings.Add(
                "Built from seeded recipe (single schedule, all levels). " +
                "Area column uses Totals.");

            var scheduleInfo = FloorExplicationScheduleRecipe.Build(_info.Name);
            var scheduleResult = RunCreateSchedule(app, scheduleInfo);
            if (scheduleResult == null || !scheduleResult.Success)
            {
                throw new InvalidOperationException(
                    scheduleResult?.Message ?? "Failed to create floor экспликация schedule.");
            }

            if (scheduleResult.Warnings != null)
                warnings.AddRange(scheduleResult.Warnings);

            return FinishWithOptionalSheet(
                app,
                doc,
                scheduleResult,
                FloorExplicationScheduleRecipe.RecipeId,
                templateName: "",
                sheetNameOverride: string.IsNullOrWhiteSpace(_info.SheetName)
                    ? "Экспликация полов"
                    : _info.SheetName,
                warnings,
                stopwatch);
        }

        private FloorExplicationCreationResult CreateFromTemplateDuplicate(
            UIApplication app,
            Document doc,
            List<string> warnings,
            Stopwatch stopwatch)
        {
            var template = ResolveTemplate(doc, _info, warnings);
            if (template == null)
            {
                throw new InvalidOperationException(
                    "useRecipe=false but no floor экспликация template found. " +
                    "Pass templateId/templateName, or leave useRecipe=true to build from the seeded recipe.");
            }

            var scheduleInfo = new ScheduleCreationInfo
            {
                Name = string.IsNullOrWhiteSpace(_info.Name)
                    ? FloorExplicationScheduleRecipe.DefaultScheduleName
                    : _info.Name.Trim(),
                CategoryName = "Floors",
                TemplateId = template.UniqueId,
                Type = "Regular"
            };

            var scheduleResult = RunCreateSchedule(app, scheduleInfo);
            if (scheduleResult == null || !scheduleResult.Success)
            {
                throw new InvalidOperationException(
                    scheduleResult?.Message ?? "Failed to duplicate floor экспликация schedule.");
            }

            if (scheduleResult.Warnings != null)
                warnings.AddRange(scheduleResult.Warnings);

            return FinishWithOptionalSheet(
                app,
                doc,
                scheduleResult,
                sourceLabel: template.Name,
                templateName: template.Name,
                sheetNameOverride: string.IsNullOrWhiteSpace(_info.SheetName)
                    ? "Экспликация полов"
                    : _info.SheetName,
                warnings,
                stopwatch);
        }

        private FloorExplicationCreationResult FinishWithOptionalSheet(
            UIApplication app,
            Document doc,
            ScheduleCreationResult scheduleResult,
            string sourceLabel,
            string templateName,
            string sheetNameOverride,
            List<string> warnings,
            Stopwatch stopwatch)
        {
            long? sheetId = null;
            string sheetUniqueId = null;
            string sheetNumber = null;
            string sheetName = null;

            if (_info.PlaceOnSheet)
            {
                try
                {
                    var sheetResult = CreateSheet(
                        app,
                        doc,
                        sheetNumberOverride: string.IsNullOrWhiteSpace(_info.SheetNumber)
                            ? null
                            : _info.SheetNumber.Trim(),
                        sheetNameOverride: sheetNameOverride,
                        warnings);

                    sheetId = sheetResult.SheetId;
                    sheetUniqueId = sheetResult.SheetUniqueId;
                    sheetNumber = sheetResult.SheetNumber;
                    sheetName = sheetResult.SheetName;

                    if (_info.AutoLayout)
                    {
                        var layoutInfo = new AutoLayoutSheetInfo
                        {
                            SheetId = sheetResult.SheetId,
                            CreateSheetIfMissing = false,
                            AvoidExisting = false,
                            Order = "heightDesc",
                            Spacing = 8,
                            MarginLeft = 20,
                            TitleBlockReserveBottom = 60,
                            Items = new List<AutoLayoutItemInfo>
                            {
                                new AutoLayoutItemInfo
                                {
                                    ViewId = scheduleResult.ScheduleId,
                                    ViewUniqueId = scheduleResult.ScheduleUniqueId
                                }
                            }
                        };

                        var layoutResult = AutoLayoutSheetEventHandler.Layout(doc, layoutInfo);
                        if (layoutResult?.Warnings != null)
                            warnings.AddRange(layoutResult.Warnings);
                    }
                    else
                    {
                        PlaceScheduleOnSheet(app, scheduleResult, sheetResult, warnings);
                    }
                }
                catch (Exception sheetEx)
                {
                    warnings.Add(
                        $"Schedule was created, but sheet placement failed: {sheetEx.Message}. " +
                        "Use create_sheet + place_view_on_sheet manually.");
                }
            }

            stopwatch.Stop();
            var item = new FloorExplicationCreatedItem
            {
                GroupKey = "all",
                Title = sheetNameOverride ?? scheduleResult.ScheduleName,
                ScheduleId = scheduleResult.ScheduleId,
                ScheduleUniqueId = scheduleResult.ScheduleUniqueId,
                ScheduleName = scheduleResult.ScheduleName,
                SheetId = sheetId,
                SheetUniqueId = sheetUniqueId,
                SheetNumber = sheetNumber,
                SheetName = sheetName
            };

            return new FloorExplicationCreationResult
            {
                Success = true,
                Message = sheetId.HasValue
                    ? $"Created floor экспликация '{scheduleResult.ScheduleName}' on sheet '{sheetNumber}'."
                    : $"Created floor экспликация schedule '{scheduleResult.ScheduleName}'.",
                ScheduleId = scheduleResult.ScheduleId,
                ScheduleUniqueId = scheduleResult.ScheduleUniqueId,
                ScheduleName = scheduleResult.ScheduleName,
                Source = sourceLabel,
                TemplateName = templateName ?? "",
                SheetId = sheetId,
                SheetUniqueId = sheetUniqueId,
                SheetNumber = sheetNumber,
                SheetName = sheetName,
                Created = new List<FloorExplicationCreatedItem> { item },
                Warnings = warnings,
                ExecutionTimeMs = stopwatch.ElapsedMilliseconds
            };
        }

        private static ScheduleCreationResult RunCreateSchedule(
            UIApplication app,
            ScheduleCreationInfo scheduleInfo)
        {
            var scheduleHandler = new CreateScheduleEventHandler();
            scheduleHandler.SetParameters(scheduleInfo);
            scheduleHandler.Execute(app);
            return scheduleHandler.ResultInfo;
        }

        private static bool ShouldDuplicateTemplate(FloorExplicationCreationInfo info)
        {
            if (!info.UseRecipe)
                return true;

            return !string.IsNullOrWhiteSpace(info.TemplateId)
                   || !string.IsNullOrWhiteSpace(info.TemplateName);
        }

        private static string BuildScheduleName(string baseName, string suffix)
        {
            var root = string.IsNullOrWhiteSpace(baseName)
                ? FloorExplicationScheduleRecipe.DefaultScheduleName
                : baseName.Trim();
            return $"{root}_{suffix}";
        }

        private static List<Level> GetProjectLevels(Document doc)
        {
            return new FilteredElementCollector(doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .OrderBy(l => l.Elevation)
                .ToList();
        }

        private static ViewSchedule ResolveTemplate(
            Document doc,
            FloorExplicationCreationInfo info,
            List<string> warnings)
        {
            var schedules = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSchedule))
                .Cast<ViewSchedule>()
                .Where(s => !s.IsTemplate)
                .ToList();

            if (!string.IsNullOrWhiteSpace(info.TemplateId))
            {
                var byId = ResolveById(doc, info.TemplateId.Trim());
                if (byId != null)
                    return byId;

                warnings.Add($"templateId '{info.TemplateId}' was not found; trying name discovery.");
            }

            if (!string.IsNullOrWhiteSpace(info.TemplateName))
            {
                var name = info.TemplateName.Trim();
                var exact = schedules.FirstOrDefault(s =>
                    s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (exact != null)
                    return exact;

                var partial = schedules.FirstOrDefault(s =>
                    s.Name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0);
                if (partial != null)
                    return partial;

                warnings.Add($"templateName '{info.TemplateName}' was not found; trying auto discovery.");
            }

            if (!info.DiscoverTemplate)
                return null;

            var floorsCategory = Category.GetCategory(doc, BuiltInCategory.OST_Floors);
            var floorSchedules = schedules
                .Where(s => floorsCategory != null && s.Definition.CategoryId == floorsCategory.Id)
                .ToList();

            var preferred = floorSchedules.FirstOrDefault(s => NameLooksLikeFloorExplication(s.Name));
            if (preferred != null)
                return preferred;

            preferred = floorSchedules.FirstOrDefault(s => !IsKeyOrStyleScheduleName(s.Name));
            if (preferred != null)
            {
                warnings.Add(
                    $"No name match for экспликация; using Floors schedule '{preferred.Name}' as template.");
                return preferred;
            }

            return null;
        }

        private static ViewSchedule ResolveById(Document doc, string templateId)
        {
            var byUnique = doc.GetElement(templateId) as ViewSchedule;
            if (byUnique != null)
                return byUnique;

            if (long.TryParse(templateId, out var idValue))
                return doc.GetElement(Utils.ElementIdExtensions.FromLong(idValue)) as ViewSchedule;

            return null;
        }

        private static bool NameLooksLikeFloorExplication(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return false;

            var n = name.ToLowerInvariant();
            if (IsKeyOrStyleScheduleName(n))
                return false;

            return n.Contains("экспликац")
                   || n.Contains("explicat")
                   || (n.Contains("ведомост") && n.Contains("пол"))
                   || (n.Contains("спецификац") && n.Contains("пол"));
        }

        private static bool IsKeyOrStyleScheduleName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return false;

            var n = name.ToLowerInvariant();
            return n.Contains("ключев")
                   || n.Contains("стил")
                   || n.Contains("key schedule")
                   || n.Contains("keynote");
        }

        private SheetCreationResult CreateSheet(
            UIApplication app,
            Document doc,
            string sheetNumberOverride,
            string sheetNameOverride,
            List<string> warnings)
        {
            var sheetNumber = string.IsNullOrWhiteSpace(sheetNumberOverride)
                ? GetNextExplicationSheetNumber(doc)
                : sheetNumberOverride.Trim();

            var sheetInfo = new SheetCreationInfo
            {
                SheetNumber = sheetNumber,
                SheetName = string.IsNullOrWhiteSpace(sheetNameOverride)
                    ? "Экспликация полов"
                    : sheetNameOverride.Trim(),
                TitleBlockFamilyName = string.IsNullOrWhiteSpace(_info.TitleBlockFamilyName)
                    ? "ADSK_ОсновнаяНадпись"
                    : _info.TitleBlockFamilyName.Trim(),
                TitleBlockTypeName = string.IsNullOrWhiteSpace(_info.TitleBlockTypeName)
                    ? "Форма 3"
                    : _info.TitleBlockTypeName.Trim()
            };

            var sheetHandler = new CreateSheetEventHandler();
            sheetHandler.SetParameters(sheetInfo);
            sheetHandler.Execute(app);

            var sheetResult = sheetHandler.ResultInfo;
            if (sheetResult == null || !sheetResult.Success)
            {
                throw new InvalidOperationException(
                    sheetResult?.Message ?? "Failed to create sheet for floor экспликация.");
            }

            if (sheetResult.Warnings != null && sheetResult.Warnings.Count > 0)
                warnings.AddRange(sheetResult.Warnings);

            ApplySheetFormat(doc, sheetResult.SheetId, _info.SheetFormat, warnings);

            return sheetResult;
        }

        /// <summary>
        /// Set the paper format (A0..A4) on the sheet's title block instance. ADSK
        /// «ОсновнаяНадпись» drives its frame size from the integer «Формат А» parameter
        /// (2 = A2). Default A3 fits only one ~234 mm schedule column, so экспликация columns
        /// overlapped; A2 fits two columns like the reference RD sheet.
        /// </summary>
        private static void ApplySheetFormat(
            Document doc,
            long sheetId,
            string format,
            List<string> warnings)
        {
            if (string.IsNullOrWhiteSpace(format))
                return;

            var digits = new string(format.Where(char.IsDigit).ToArray());
            if (!int.TryParse(digits, out var formatNumber))
            {
                warnings.Add($"Unrecognized sheetFormat '{format}'; sheet left with default format.");
                return;
            }

            var sheet = doc.GetElement(
                RevitMCPCommandSet.Utils.ElementIdExtensions.FromLong(sheetId)) as ViewSheet;
            if (sheet == null)
                return;

            var titleBlock = new FilteredElementCollector(doc, sheet.Id)
                .OfCategory(BuiltInCategory.OST_TitleBlocks)
                .WhereElementIsNotElementType()
                .FirstElement();

            if (titleBlock == null)
            {
                warnings.Add("Title block not found on the explication sheet; format left as default.");
                return;
            }

            try
            {
                using (var tx = new Transaction(doc, "Set explication sheet format"))
                {
                    tx.Start();
                    var formatParam = titleBlock.LookupParameter("Формат А");
                    if (formatParam != null && !formatParam.IsReadOnly)
                    {
                        formatParam.Set(formatNumber);
                    }
                    else
                    {
                        warnings.Add(
                            "Parameter 'Формат А' not found or read-only on the title block; " +
                            "sheet format left as default. Set the sheet format manually if needed.");
                    }

                    tx.Commit();
                }

                doc.Regenerate();
            }
            catch (Exception ex)
            {
                warnings.Add($"Failed to set sheet format to {format}: {ex.Message}");
            }
        }

        private void PlaceScheduleOnSheet(
            UIApplication app,
            ScheduleCreationResult scheduleResult,
            SheetCreationResult sheetResult,
            List<string> warnings,
            double? positionYOverride = null)
        {
            var placement = new ViewportCreationInfo
            {
                SheetId = (int)sheetResult.SheetId,
                SheetUniqueId = sheetResult.SheetUniqueId,
                ViewId = (int)scheduleResult.ScheduleId,
                ViewUniqueId = scheduleResult.ScheduleUniqueId,
                PositionX = _info.PositionX,
                PositionY = positionYOverride ?? _info.PositionY
            };

            var placeHandler = new PlaceViewOnSheetEventHandler();
            placeHandler.SetParameters(placement);
            placeHandler.Execute(app);

            var placeResult = placeHandler.ResultInfo;
            if (placeResult == null || !placeResult.Success)
            {
                throw new InvalidOperationException(
                    placeResult?.Message ?? "Failed to place floor экспликация on sheet.");
            }

            if (placeResult.Warnings != null && placeResult.Warnings.Count > 0)
                warnings.AddRange(placeResult.Warnings);
        }

        private static string GetNextExplicationSheetNumber(Document doc)
        {
            var existing = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSheet))
                .Cast<ViewSheet>()
                .Select(s => s.SheetNumber)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            for (var i = 1; i < 1000; i++)
            {
                var candidate = $"ЭП-{i:D2}";
                if (!existing.Contains(candidate))
                    return candidate;
            }

            return $"ЭП-{DateTime.Now:HHmmss}";
        }

        /// <summary>
        /// Reference to a sheet a schedule was placed on (may differ per schedule when the
        /// content overflows onto additional ЭП sheets).
        /// </summary>
        private sealed class SheetRef
        {
            public long SheetId { get; set; }
            public string SheetUniqueId { get; set; }
            public string SheetNumber { get; set; }
            public string SheetName { get; set; }

            public static SheetRef From(SheetCreationResult result) => new SheetRef
            {
                SheetId = result.SheetId,
                SheetUniqueId = result.SheetUniqueId,
                SheetNumber = result.SheetNumber,
                SheetName = result.SheetName
            };
        }

        /// <summary>
        /// Pack the created schedules onto A2 sheet(s) with the shelf auto-layout engine
        /// (columns, title-block aware, no overlap). Schedules that do not fit spill onto a new
        /// ЭП-0N sheet. Returns the primary (first) sheet; fills <paramref name="placementBySchedule"/>
        /// with the sheet each schedule landed on.
        /// </summary>
        private SheetRef LayoutAcrossSheets(
            UIApplication app,
            Document doc,
            List<(FloorExplicationLevelGroup Group, List<Level> Levels, ScheduleCreationResult Schedule)> pending,
            Dictionary<long, SheetRef> placementBySchedule,
            List<string> warnings)
        {
            var remaining = pending.Select(p => p.Schedule).ToList();
            SheetRef primary = null;
            var guard = 0;

            while (remaining.Count > 0 && guard < 12)
            {
                guard++;

                var sheetResult = CreateSheet(
                    app,
                    doc,
                    sheetNumberOverride: guard == 1 && !string.IsNullOrWhiteSpace(_info.SheetNumber)
                        ? _info.SheetNumber.Trim()
                        : null,
                    sheetNameOverride: string.IsNullOrWhiteSpace(_info.SheetName)
                        ? "Экспликация полов"
                        : _info.SheetName.Trim(),
                    warnings);

                var sheetRef = SheetRef.From(sheetResult);
                primary ??= sheetRef;

                var layoutInfo = new AutoLayoutSheetInfo
                {
                    SheetId = sheetResult.SheetId,
                    CreateSheetIfMissing = false,
                    AvoidExisting = false,
                    Order = "heightDesc",
                    Spacing = 8,
                    MarginLeft = 20,
                    TitleBlockReserveBottom = 60,
                    Items = remaining
                        .Select(s => new AutoLayoutItemInfo
                        {
                            ViewId = s.ScheduleId,
                            ViewUniqueId = s.ScheduleUniqueId
                        })
                        .ToList()
                };

                var layoutResult = AutoLayoutSheetEventHandler.Layout(doc, layoutInfo);
                if (layoutResult?.Warnings != null)
                    warnings.AddRange(layoutResult.Warnings);

                var placedIds = new HashSet<long>();
                if (layoutResult?.Items != null)
                {
                    foreach (var placedItem in layoutResult.Items)
                    {
                        if (!placedItem.Placed)
                            continue;

                        placementBySchedule[placedItem.ViewId] = sheetRef;
                        placedIds.Add(placedItem.ViewId);
                    }
                }

                var next = remaining.Where(s => !placedIds.Contains(s.ScheduleId)).ToList();

                if (placedIds.Count == 0)
                {
                    warnings.Add(
                        $"{next.Count} экспликация schedule(s) do not fit on a fresh {_info.SheetFormat} sheet " +
                        "(taller than the sheet). Split the schedule into segments (Revit «Разбить таблицу») " +
                        "or use a larger format; leaving them unplaced.");

                    if (!ReferenceEquals(sheetRef, primary))
                        DeleteSheetSafe(doc, sheetResult.SheetId, warnings);

                    break;
                }

                remaining = next;
            }

            return primary;
        }

        private static void DeleteSheetSafe(Document doc, long sheetId, List<string> warnings)
        {
            try
            {
                using (var tx = new Transaction(doc, "Remove empty explication sheet"))
                {
                    tx.Start();
                    doc.Delete(RevitMCPCommandSet.Utils.ElementIdExtensions.FromLong(sheetId));
                    tx.Commit();
                }
            }
            catch (Exception ex)
            {
                warnings.Add($"Failed to remove empty overflow sheet: {ex.Message}");
            }
        }

        private static long GetElementIdValue(ElementId id)
        {
#if REVIT2024_OR_GREATER
            return id.Value;
#else
            return id.IntegerValue;
#endif
        }
    }
}
