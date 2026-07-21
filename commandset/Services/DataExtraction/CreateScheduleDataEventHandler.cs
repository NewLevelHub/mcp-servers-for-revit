using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCPCommandSet.Models.DataExtraction;
using RevitMCPCommandSet.Utils;
using RevitMCPSDK.API.Interfaces;

namespace RevitMCPCommandSet.Services.DataExtraction
{
    public class CreateScheduleDataEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
    {
        private ScheduleElementCategory _category;
        private string _typeNameFilter;
        private bool _createViewSchedule;
        private string _scheduleName;
        private bool _replaceExisting;
        private string _templateScheduleName;
        private string _templateId;
        private string _lastScheduleTemplateSource;

        public ScheduleExportResult ResultInfo { get; private set; } = new ScheduleExportResult();
        public bool TaskCompleted { get; private set; }
        private readonly ManualResetEvent _resetEvent = new ManualResetEvent(false);

        public void SetParameters(
            ScheduleElementCategory category,
            string typeNameFilter = null,
            bool createViewSchedule = false,
            string scheduleName = null,
            bool replaceExisting = false,
            string templateScheduleName = null,
            string templateId = null)
        {
            _category = category;
            _typeNameFilter = typeNameFilter;
            _createViewSchedule = createViewSchedule;
            _scheduleName = scheduleName;
            _replaceExisting = replaceExisting;
            _templateScheduleName = templateScheduleName;
            _templateId = templateId;
            _lastScheduleTemplateSource = null;
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
                var instanceRows = _category switch
                {
                    ScheduleElementCategory.Doors => CollectDoorRows(doc),
                    ScheduleElementCategory.Windows => CollectWindowRows(doc),
                    ScheduleElementCategory.Floors => CollectFloorRows(doc),
                    ScheduleElementCategory.CurtainWalls => CollectCurtainWallRows(doc, _typeNameFilter),
                    _ => new List<ScheduleInstanceRow>()
                };

                var instances = instanceRows
                    .Select(ToInstanceExport)
                    .OrderBy(i => i.Level)
                    .ThenBy(i => i.FamilyName)
                    .ThenBy(i => i.Type)
                    .ThenBy(i => i.Id)
                    .ToList();

                var groups = _category == ScheduleElementCategory.Floors
                    ? BuildFloorGroups(instanceRows)
                    : BuildDefaultGroups(instanceRows);

                var totalUnmarked = instanceRows.Count(r => string.IsNullOrWhiteSpace(r.Mark));
                double? totalAreaM2 = _category == ScheduleElementCategory.Floors
                    ? Math.Round(instanceRows.Sum(r => r.AreaM2 ?? 0), 2)
                    : (double?)null;

                var message = _category == ScheduleElementCategory.Floors
                    ? $"Successfully exported floor finish экспликация: {instanceRows.Count} floors, {totalAreaM2:0.##} m² in {groups.Count} type/level groups ({totalUnmarked} without mark)"
                    : $"Successfully exported {instanceRows.Count} {_category.ToString().ToLowerInvariant()} in {groups.Count} groups ({totalUnmarked} without mark)";

                ViewSchedule createdSchedule = null;
                if (_createViewSchedule)
                {
                    createdSchedule = CreateOrReplaceElementSchedule(
                        doc,
                        _category,
                        _scheduleName,
                        _replaceExisting,
                        _templateScheduleName,
                        _templateId,
                        warnings);
                    message += $" Created Revit ViewSchedule '{createdSchedule.Name}'.";
                }

                ResultInfo = new ScheduleExportResult
                {
                    Category = _category.ToString().ToLowerInvariant(),
                    TotalCount = instanceRows.Count,
                    UnmarkedCount = totalUnmarked,
                    TotalAreaM2 = totalAreaM2,
                    Instances = instances,
                    Groups = groups,
                    CreatedViewSchedule = createdSchedule != null,
                    ScheduleId = createdSchedule != null ? GetElementIdValue(createdSchedule.Id) : null,
                    ScheduleUniqueId = createdSchedule?.UniqueId,
                    ScheduleName = createdSchedule?.Name,
                    ScheduleTemplateSource = _lastScheduleTemplateSource,
                    Warnings = warnings,
                    Success = true,
                    Message = message
                };
            }
            catch (Exception ex)
            {
                ResultInfo = new ScheduleExportResult
                {
                    Category = _category.ToString().ToLowerInvariant(),
                    Warnings = warnings,
                    Success = false,
                    Message = $"Error exporting schedule data: {ex.Message}"
                };
            }
            finally
            {
                TaskCompleted = true;
                _resetEvent.Set();
            }
        }

        public string GetName() => "Create Schedule Data";

        private static readonly string[] DefaultDoorScheduleTemplateNames =
        {
            "О_АР_Спецификация элементов заполнения дверных проемов поэтжная",
            "Спецификация элементов заполнения дверных проемов поэтжная",
        };

        private ViewSchedule CreateOrReplaceElementSchedule(
            Document doc,
            ScheduleElementCategory category,
            string requestedName,
            bool replaceExisting,
            string templateScheduleName,
            string templateId,
            List<string> warnings)
        {
            if (category != ScheduleElementCategory.Doors && category != ScheduleElementCategory.Windows)
                throw new InvalidOperationException("createViewSchedule is currently supported for Doors and Windows.");

            var builtInCategory = category == ScheduleElementCategory.Doors
                ? BuiltInCategory.OST_Doors
                : BuiltInCategory.OST_Windows;
            var categoryElement = Category.GetCategory(doc, builtInCategory)
                ?? throw new InvalidOperationException($"Revit category {builtInCategory} was not found.");

            var defaultName = category == ScheduleElementCategory.Doors
                ? "Спецификация дверей"
                : "Спецификация окон";
            var baseName = string.IsNullOrWhiteSpace(requestedName) ? defaultName : requestedName.Trim();

            using (var tx = new Transaction(doc, $"Create {category} ViewSchedule"))
            {
                tx.Start();

                var existing = FindScheduleByName(doc, baseName, categoryElement.Id);
                if (existing != null && replaceExisting)
                    doc.Delete(existing.Id);

                if (existing != null && !replaceExisting)
                {
                    tx.Commit();
                    return existing;
                }

                ViewSchedule schedule;
                if (category == ScheduleElementCategory.Doors)
                {
                    var template = ResolveDoorScheduleTemplate(doc, templateId, templateScheduleName);
                    if (template != null)
                    {
                        schedule = DuplicateScheduleFromTemplate(doc, template, baseName);
                        _lastScheduleTemplateSource = template.Name;
                        warnings.Add($"Duplicated RD door schedule template '{template.Name}'.");
                    }
                    else
                    {
                        schedule = ViewSchedule.CreateSchedule(doc, categoryElement.Id);
                        schedule.Name = GetUniqueScheduleName(doc, baseName);
                        ConfigureRdDoorSchedule(doc, schedule, warnings);
                        _lastScheduleTemplateSource = "built-in-rd-layout";
                        warnings.Add(
                            "RD door schedule template not found in project; created schedule with RD column layout.");
                    }
                }
                else
                {
                    schedule = ViewSchedule.CreateSchedule(doc, categoryElement.Id);
                    schedule.Name = GetUniqueScheduleName(doc, baseName);
                    ConfigureElementSchedule(doc, schedule, category, warnings);
                }

                tx.Commit();
                return schedule;
            }
        }

        private static ViewSchedule DuplicateScheduleFromTemplate(
            Document doc,
            ViewSchedule template,
            string requestedName)
        {
            var duplicatedId = template.Duplicate(ViewDuplicateOption.Duplicate);
            var schedule = doc.GetElement(duplicatedId) as ViewSchedule
                ?? throw new InvalidOperationException("Failed to duplicate schedule template.");
            schedule.Name = GetUniqueScheduleName(doc, requestedName);
            return schedule;
        }

        private static ViewSchedule ResolveDoorScheduleTemplate(
            Document doc,
            string templateId,
            string templateScheduleName)
        {
            if (!string.IsNullOrWhiteSpace(templateId))
            {
                var byId = ResolveScheduleByIdOrUniqueId(doc, templateId.Trim());
                if (byId != null && IsDoorSchedule(byId))
                    return byId;
            }

            if (!string.IsNullOrWhiteSpace(templateScheduleName))
            {
                var byName = FindDoorScheduleByName(doc, templateScheduleName.Trim());
                if (byName != null)
                    return byName;
            }

            foreach (var candidate in DefaultDoorScheduleTemplateNames)
            {
                var found = FindDoorScheduleByName(doc, candidate);
                if (found != null)
                    return found;
            }

            return FindDoorScheduleTemplateByKeywords(doc);
        }

        private static ViewSchedule ResolveScheduleByIdOrUniqueId(Document doc, string templateId)
        {
            var element = doc.GetElement(templateId);
            if (element == null && long.TryParse(templateId, out var numericId))
            {
#if REVIT2024_OR_GREATER
                element = doc.GetElement(new ElementId(numericId));
#else
                element = doc.GetElement(new ElementId((int)numericId));
#endif
            }

            return element as ViewSchedule;
        }

        private static bool IsDoorSchedule(ViewSchedule schedule)
        {
            if (schedule == null || schedule.IsTemplate)
                return false;

            var doorsCategory = Category.GetCategory(schedule.Document, BuiltInCategory.OST_Doors);
            return doorsCategory != null && schedule.Definition.CategoryId == doorsCategory.Id;
        }

        private static ViewSchedule FindDoorScheduleByName(Document doc, string name)
        {
            var doorsCategoryId = Category.GetCategory(doc, BuiltInCategory.OST_Doors)?.Id;
            if (doorsCategoryId == null)
                return null;

            var schedules = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSchedule))
                .Cast<ViewSchedule>()
                .Where(s => !s.IsTemplate && s.Definition.CategoryId == doorsCategoryId)
                .ToList();

            var exact = schedules.FirstOrDefault(s =>
                s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (exact != null)
                return exact;

            return schedules.FirstOrDefault(s =>
                s.Name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static bool NameContains(string value, string token) =>
            value?.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0;

        private static ViewSchedule FindDoorScheduleTemplateByKeywords(Document doc)
        {
            var doorsCategoryId = Category.GetCategory(doc, BuiltInCategory.OST_Doors)?.Id;
            if (doorsCategoryId == null)
                return null;

            return new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSchedule))
                .Cast<ViewSchedule>()
                .Where(s => !s.IsTemplate && s.Definition.CategoryId == doorsCategoryId)
                .FirstOrDefault(s =>
                {
                    var n = s.Name;
                    var isDoor = NameContains(n, "дверн") || NameContains(n, "door");
                    var isFloorMatrix = NameContains(n, "поэтж") || NameContains(n, "заполнения");
                    return isDoor && isFloorMatrix;
                });
        }

        /// <summary>
        /// RD-style door schedule when no project template exists: ADSK columns + floor matrix.
        /// Mirrors «О_АР_Спецификация элементов заполнения дверных проемов поэтжная».
        /// </summary>
        private static void ConfigureRdDoorSchedule(
            Document doc,
            ViewSchedule schedule,
            List<string> warnings)
        {
            var definition = schedule.Definition;
            definition.IsItemized = false;
            definition.ShowTitle = true;
            definition.ShowHeaders = true;
            definition.ShowGridLines = true;

            ClearScheduleDefinition(definition);

            AddField(doc, definition, warnings, "Description|Описание", ScheduleFieldType.ElementType, "Описание", hidden: true);
            AddField(doc, definition, warnings, "Level|Уровень", ScheduleFieldType.Instance, "Уровень", hidden: true);
            var familyTypeField = AddField(
                doc,
                definition,
                warnings,
                "Family and Type|Семейство и типоразмер|Типоразмер|Type",
                ScheduleFieldType.ElementType,
                "Семейство и типоразмер",
                hidden: true);

            AddField(
                doc,
                definition,
                warnings,
                "ADSK_Марка|Mark|Марка",
                ScheduleFieldType.Instance,
                "Поз.",
                widthMm: 15,
                alignment: ScheduleHorizontalAlignment.Center);
            AddField(doc, definition, warnings, "ADSK_Обозначение", ScheduleFieldType.Instance, "Обозначение", widthMm: 60);
            AddField(doc, definition, warnings, "ADSK_Наименование", ScheduleFieldType.Instance, "Наименование", widthMm: 65);

            var floorColumns = new (string param, string heading)[]
            {
                ("-1 этаж", "-1 этаж             -4,200"),
                ("01 этаж", "1 этаж \r\n0,000"),
                ("02 этаж", "2 этаж         +3,900"),
                ("03 этаж", "3-5 этаж  +6,900 -   +12,900 "),
                ("06 этаж", "6-9 этаж +15,900 -   +24,900 "),
                ("10 этаж", "10-16 этаж +27,900 - +45,900 "),
                ("12 этаж", "Тех.этаж, кровля"),
            };

            foreach (var (param, heading) in floorColumns)
            {
                AddField(
                    doc,
                    definition,
                    warnings,
                    param,
                    ScheduleFieldType.Instance,
                    heading,
                    widthMm: 20,
                    alignment: ScheduleHorizontalAlignment.Center);
            }

            AddField(
                doc,
                definition,
                warnings,
                "Count|Количество|Число",
                ScheduleFieldType.Count,
                "Общее количество",
                widthMm: 20,
                alignment: ScheduleHorizontalAlignment.Center);
            AddField(doc, definition, warnings, "ADSK_Масса_Текст", ScheduleFieldType.Instance, "Масса ед., кг", widthMm: 15);
            AddField(
                doc,
                definition,
                warnings,
                "Comments|Комментарии к типоразмеру|Type Comments",
                ScheduleFieldType.ElementType,
                "Примечание",
                widthMm: 54,
                alignment: ScheduleHorizontalAlignment.Center);

            ApplyDescriptionDoorFilter(definition, warnings);

            TryAddSortField(definition, warnings, "ADSK_Марка|Mark|Марка");
            if (familyTypeField != null)
                TryAddSortField(definition, warnings, "Family and Type|Семейство и типоразмер");

            if (familyTypeField != null)
                ApplyAccessoryFilters(definition, familyTypeField.FieldId, ScheduleElementCategory.Doors, warnings);
        }

        private static void ApplyDescriptionDoorFilter(ScheduleDefinition definition, List<string> warnings)
        {
            try
            {
                var descriptionFieldId = FindFieldIdByName(definition, "Описание");
                if (descriptionFieldId == null)
                {
                    warnings.Add("Description field not found; RD door filter was not applied.");
                    return;
                }

                definition.AddFilter(new ScheduleFilter(descriptionFieldId, ScheduleFilterType.BeginsWith, "Двер"));
            }
            catch (Exception ex)
            {
                warnings.Add($"Failed to add RD door description filter: {ex.Message}");
            }
        }

        private static void TryAddSortField(
            ScheduleDefinition definition,
            List<string> warnings,
            string aliases)
        {
            try
            {
                var fieldId = FindFieldIdByName(definition, aliases);
                if (fieldId == null)
                {
                    warnings.Add($"Sort field '{aliases}' was not found and was skipped.");
                    return;
                }

                definition.AddSortGroupField(new ScheduleSortGroupField(fieldId, ScheduleSortOrder.Ascending));
            }
            catch (Exception ex)
            {
                warnings.Add($"Failed to add sort for '{aliases}': {ex.Message}");
            }
        }

        private static ScheduleFieldId FindFieldIdByName(ScheduleDefinition definition, string aliases)
        {
            var aliasList = aliases
                .Split('|')
                .Select(alias => alias.Trim())
                .Where(alias => alias.Length > 0)
                .ToList();

            for (var i = 0; i < definition.GetFieldCount(); i++)
            {
                var field = definition.GetField(definition.GetFieldId(i));
                var name = field.GetName();
                if (aliasList.Any(alias => name.Equals(alias, StringComparison.OrdinalIgnoreCase)))
                    return definition.GetFieldId(i);
            }

            return null;
        }

        private static void ConfigureElementSchedule(
            Document doc,
            ViewSchedule schedule,
            ScheduleElementCategory category,
            List<string> warnings)
        {
            var definition = schedule.Definition;
            definition.IsItemized = true;
            definition.ShowTitle = true;
            definition.ShowHeaders = true;
            definition.ShowGridLines = true;

            ClearScheduleDefinition(definition);

            AddField(doc, definition, warnings, "Mark|Марка", ScheduleFieldType.Instance, "Марка");
            var familyTypeField = AddField(
                doc,
                definition,
                warnings,
                "Family and Type|Семейство и типоразмер|Типоразмер|Type",
                ScheduleFieldType.ElementType,
                "Тип");
            AddField(doc, definition, warnings, "Level|Уровень", ScheduleFieldType.Instance, "Уровень");
            AddField(doc, definition, warnings, "Count|Количество", ScheduleFieldType.Count, "Кол.");

            if (familyTypeField != null)
                ApplyAccessoryFilters(definition, familyTypeField.FieldId, category, warnings);
            else
                warnings.Add("Family/type field was not found; accessory filters were not applied.");
        }

        private static void ClearScheduleDefinition(ScheduleDefinition definition)
        {
            definition.ClearFilters();
            definition.ClearSortGroupFields();
            while (definition.GetFieldCount() > 0)
                definition.RemoveField(definition.GetFieldId(0));
        }

        private static ScheduleField AddField(
            Document doc,
            ScheduleDefinition definition,
            List<string> warnings,
            string aliases,
            ScheduleFieldType fieldType,
            string heading,
            bool hidden = false,
            double widthMm = 0,
            ScheduleHorizontalAlignment? alignment = null)
        {
            var schedulable = FindSchedulableField(doc, definition, aliases, fieldType);
            if (schedulable == null)
            {
                warnings.Add($"Schedule field '{aliases}' was not found and was skipped.");
                return null;
            }

            try
            {
                var field = definition.AddField(schedulable);
                if (!string.IsNullOrWhiteSpace(heading))
                    field.ColumnHeading = heading;
                field.IsHidden = hidden;
                if (widthMm > 0)
                    field.GridColumnWidth = widthMm / 304.8;
                if (alignment.HasValue)
                    field.HorizontalAlignment = alignment.Value;
                return field;
            }
            catch (Exception ex)
            {
                warnings.Add($"Failed to add schedule field '{aliases}': {ex.Message}");
                return null;
            }
        }

        private static SchedulableField FindSchedulableField(
            Document doc,
            ScheduleDefinition definition,
            string aliases,
            ScheduleFieldType fieldType)
        {
            var schedulableFields = definition.GetSchedulableFields();
            if (fieldType == ScheduleFieldType.Count)
            {
                var countField = schedulableFields.FirstOrDefault(field => field.FieldType == ScheduleFieldType.Count);
                if (countField != null)
                    return countField;
            }

            var aliasList = aliases
                .Split('|')
                .Select(alias => alias.Trim())
                .Where(alias => alias.Length > 0)
                .ToList();

            SchedulableField fallback = null;
            foreach (var field in schedulableFields)
            {
                var name = field.GetName(doc);
                if (string.IsNullOrWhiteSpace(name) ||
                    !aliasList.Any(alias => name.Equals(alias, StringComparison.OrdinalIgnoreCase)))
                    continue;

                if (field.FieldType == fieldType)
                    return field;

                fallback ??= field;
            }

            return fallback;
        }

        private static void ApplyAccessoryFilters(
            ScheduleDefinition definition,
            ScheduleFieldId familyTypeFieldId,
            ScheduleElementCategory category,
            List<string> warnings)
        {
            var tokens = category == ScheduleElementCategory.Doors
                ? new[] { "откос", "reveal", "slope", "accessor" }
                : new[] { "откос", "подокон", "sill", "reveal", "slope", "accessor" };

            foreach (var token in tokens)
            {
                try
                {
                    definition.AddFilter(new ScheduleFilter(familyTypeFieldId, ScheduleFilterType.NotContains, token));
                }
                catch (Exception ex)
                {
                    warnings.Add($"Failed to add accessory filter '{token}': {ex.Message}");
                }
            }
        }

        private static ViewSchedule FindScheduleByName(Document doc, string name, ElementId categoryId)
        {
            return new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSchedule))
                .Cast<ViewSchedule>()
                .FirstOrDefault(schedule =>
                    !schedule.IsTemplate &&
                    schedule.Definition.CategoryId == categoryId &&
                    schedule.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        }

        private static string GetUniqueScheduleName(Document doc, string baseName)
        {
            var existingNames = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSchedule))
                .Cast<ViewSchedule>()
                .Select(schedule => schedule.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (!existingNames.Contains(baseName))
                return baseName;

            var index = 1;
            string candidate;
            do
            {
                candidate = $"{baseName} ({index})";
                index++;
            } while (existingNames.Contains(candidate));

            return candidate;
        }

        private static List<ScheduleInstanceRow> CollectDoorRows(Document doc) =>
            CollectFamilyInstanceRows(doc, BuiltInCategory.OST_Doors, GetDoorSize, OpeningFillClassifier.IsSchedulableDoor);

        private static List<ScheduleInstanceRow> CollectWindowRows(Document doc) =>
            CollectFamilyInstanceRows(doc, BuiltInCategory.OST_Windows, GetWindowSize, OpeningFillClassifier.IsSchedulableWindow);

        /// <summary>
        /// Floor finish экспликация only: excludes slabs / ceiling insulation / facade (REV-49).
        /// </summary>
        private static List<ScheduleInstanceRow> CollectFloorRows(Document doc)
        {
            var rows = new List<ScheduleInstanceRow>();
            var floors = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Floors)
                .WhereElementIsNotElementType()
                .Cast<Floor>();

            foreach (var floor in floors)
            {
                var floorType = doc.GetElement(floor.GetTypeId()) as FloorType;
                var typeName = floorType?.Name ?? "";
                var familyName = floorType?.FamilyName ?? "";
                if (!FloorFinishClassifier.IsFloorFinish(typeName, familyName))
                    continue;

                var level = doc.GetElement(floor.LevelId) as Level;
                var areaM2 = GetFloorAreaM2(floor);
                rows.Add(new ScheduleInstanceRow
                {
                    ElementId = GetElementIdValue(floor.Id),
                    Mark = GetElementMark(floor),
                    FamilyName = familyName,
                    Type = typeName,
                    Size = areaM2 > 0 ? $"{Math.Round(areaM2, 2)} m²" : "",
                    AreaM2 = Math.Round(areaM2, 2),
                    Level = level?.Name ?? "No Level",
                    TypeId = GetElementIdValue(floor.GetTypeId()),
                    Layers = BuildFloorLayers(doc, floorType)
                });
            }

            return rows;
        }

        /// <summary>
        /// Curtain wall systems (витражи): one row per Wall element with WallKind.Curtain —
        /// panels and mullions are never counted. Optional type-name filter narrows to
        /// naming conventions such as '(витражи)*'.
        /// </summary>
        private static List<ScheduleInstanceRow> CollectCurtainWallRows(Document doc, string typeNameFilter)
        {
            var rows = new List<ScheduleInstanceRow>();
            var curtainWalls = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Walls)
                .WhereElementIsNotElementType()
                .OfClass(typeof(Wall))
                .Cast<Wall>()
                .Where(CurtainWallClassifier.IsCurtainWall)
                .Where(wall => CurtainWallClassifier.MatchesTypeFilter(wall, typeNameFilter));

            foreach (var wall in curtainWalls)
            {
                var wallType = wall.WallType;
                var level = doc.GetElement(wall.LevelId) as Level;
                rows.Add(new ScheduleInstanceRow
                {
                    ElementId = GetElementIdValue(wall.Id),
                    Mark = GetElementMark(wall),
                    FamilyName = wallType?.FamilyName ?? "",
                    Type = wallType?.Name ?? "",
                    Size = FormatCurtainWallSize(wall),
                    Level = level?.Name ?? "No Level",
                    TypeId = GetElementIdValue(wall.GetTypeId())
                });
            }

            return rows;
        }

        private static string FormatCurtainWallSize(Wall wall)
        {
            double lengthFeet = (wall.Location as LocationCurve)?.Curve?.Length ?? 0;
            double heightFeet = GetParameterDouble(wall, BuiltInParameter.WALL_USER_HEIGHT_PARAM);
            if (heightFeet <= 0)
            {
                var bbox = wall.get_BoundingBox(null);
                if (bbox != null)
                    heightFeet = bbox.Max.Z - bbox.Min.Z;
            }

            return FormatWidthHeightMm(lengthFeet, heightFeet, wall.WallType?.Name);
        }

        private static List<ScheduleInstanceRow> CollectFamilyInstanceRows(
            Document doc,
            BuiltInCategory category,
            Func<FamilyInstance, string> sizeResolver,
            Func<FamilyInstance, bool> includePredicate)
        {
            var rows = new List<ScheduleInstanceRow>();
            var instances = new FilteredElementCollector(doc)
                .OfCategory(category)
                .WhereElementIsNotElementType()
                .OfClass(typeof(FamilyInstance))
                .Cast<FamilyInstance>()
                .Where(includePredicate);

            foreach (var instance in instances)
            {
                var symbol = instance.Symbol;
                rows.Add(new ScheduleInstanceRow
                {
                    ElementId = GetElementIdValue(instance.Id),
                    Mark = GetElementMark(instance),
                    FamilyName = symbol?.FamilyName ?? "",
                    Type = symbol?.Name ?? "",
                    Size = sizeResolver(instance),
                    Level = GetInstanceLevelName(doc, instance),
                    TypeId = GetElementIdValue(symbol?.Id ?? ElementId.InvalidElementId)
                });
            }

            return rows;
        }

        private static List<ScheduleGroupRow> BuildDefaultGroups(List<ScheduleInstanceRow> instanceRows)
        {
            return instanceRows
                .GroupBy(r => new { r.TypeId, r.Level, r.Size, r.Type, r.FamilyName })
                .Select(g =>
                {
                    var elementIds = g.Select(x => x.ElementId).OrderBy(id => id).ToList();
                    var unmarkedCount = g.Count(x => string.IsNullOrWhiteSpace(x.Mark));
                    return new ScheduleGroupRow
                    {
                        TypeId = g.Key.TypeId,
                        FamilyName = g.Key.FamilyName,
                        Type = g.Key.Type,
                        Size = g.Key.Size,
                        Level = g.Key.Level,
                        Count = g.Count(),
                        UnmarkedCount = unmarkedCount,
                        ElementIds = elementIds,
                        Mark = BuildGroupMark(g.Select(x => x.Mark))
                    };
                })
                .OrderBy(g => g.Level)
                .ThenBy(g => g.FamilyName)
                .ThenBy(g => g.Type)
                .ToList();
        }

        /// <summary>
        /// Group floor finishes by type + level and sum areas (экспликация), not by per-instance size.
        /// </summary>
        private static List<ScheduleGroupRow> BuildFloorGroups(List<ScheduleInstanceRow> instanceRows)
        {
            return instanceRows
                .GroupBy(r => new { r.TypeId, r.Level, r.Type, r.FamilyName })
                .Select(g =>
                {
                    var areaM2 = Math.Round(g.Sum(x => x.AreaM2 ?? 0), 2);
                    var elementIds = g.Select(x => x.ElementId).OrderBy(id => id).ToList();
                    var unmarkedCount = g.Count(x => string.IsNullOrWhiteSpace(x.Mark));
                    var layers = g.Select(x => x.Layers).FirstOrDefault(l => l != null && l.Count > 0);
                    return new ScheduleGroupRow
                    {
                        TypeId = g.Key.TypeId,
                        FamilyName = g.Key.FamilyName,
                        Type = g.Key.Type,
                        Size = areaM2 > 0 ? $"{areaM2} m²" : "",
                        AreaM2 = areaM2,
                        Level = g.Key.Level,
                        Count = g.Count(),
                        UnmarkedCount = unmarkedCount,
                        ElementIds = elementIds,
                        Mark = BuildGroupMark(g.Select(x => x.Mark)),
                        Layers = layers
                    };
                })
                .OrderBy(g => g.Level)
                .ThenBy(g => g.Type)
                .ToList();
        }

        private static ScheduleInstanceExport ToInstanceExport(ScheduleInstanceRow row) =>
            new ScheduleInstanceExport
            {
                Id = row.ElementId,
                Mark = row.Mark,
                FamilyName = row.FamilyName,
                Type = row.Type,
                Size = row.Size,
                Level = row.Level,
                TypeId = row.TypeId,
                AreaM2 = row.AreaM2,
                Layers = row.Layers
            };

        private static string GetDoorSize(FamilyInstance instance)
        {
            double width = GetParameterDouble(instance, BuiltInParameter.DOOR_WIDTH);
            double height = GetParameterDouble(instance, BuiltInParameter.DOOR_HEIGHT);
            return FormatWidthHeightMm(width, height, instance.Symbol?.Name);
        }

        private static string GetWindowSize(FamilyInstance instance)
        {
            double width = GetParameterDouble(instance, BuiltInParameter.WINDOW_WIDTH);
            double height = GetParameterDouble(instance, BuiltInParameter.WINDOW_HEIGHT);
            if (width <= 0) width = GetParameterDouble(instance, BuiltInParameter.FAMILY_WIDTH_PARAM);
            if (height <= 0) height = GetParameterDouble(instance, BuiltInParameter.FAMILY_HEIGHT_PARAM);
            return FormatWidthHeightMm(width, height, instance.Symbol?.Name);
        }

        private static string FormatWidthHeightMm(double widthFeet, double heightFeet, string fallback)
        {
            if (widthFeet > 0 && heightFeet > 0)
            {
                int widthMm = (int)Math.Round(widthFeet * 304.8);
                int heightMm = (int)Math.Round(heightFeet * 304.8);
                return $"{widthMm} x {heightMm} mm";
            }

            return fallback ?? "";
        }

        private static double GetFloorAreaM2(Floor floor)
        {
            double areaInternal = floor.get_Parameter(BuiltInParameter.HOST_AREA_COMPUTED)?.AsDouble() ?? 0;
            return areaInternal > 0 ? RevitUnitConversion.ToSquareMeters(areaInternal) : 0;
        }

        private static List<FloorLayerExport> BuildFloorLayers(Document doc, FloorType floorType)
        {
            var structure = floorType?.GetCompoundStructure();
            if (structure == null)
                return null;

            var layers = new List<FloorLayerExport>();
            for (int i = 0; i < structure.LayerCount; i++)
            {
                var layer = structure.GetLayers()[i];
                var materialName = "";
                if (layer.MaterialId != null && layer.MaterialId != ElementId.InvalidElementId)
                {
                    var material = doc.GetElement(layer.MaterialId) as Material;
                    materialName = material?.Name ?? "";
                }

                layers.Add(new FloorLayerExport
                {
                    Function = layer.Function.ToString(),
                    Material = materialName,
                    ThicknessMm = Math.Round(RevitUnitConversion.ToMillimeters(layer.Width), 1)
                });
            }

            return layers.Count > 0 ? layers : null;
        }

        private static string GetInstanceLevelName(Document doc, FamilyInstance instance)
        {
            var levelId = instance.LevelId;
            if (levelId != null && levelId != ElementId.InvalidElementId)
            {
                return (doc.GetElement(levelId) as Level)?.Name ?? "No Level";
            }

            var host = instance.Host;
            if (host != null)
            {
                var hostLevelId = host.get_Parameter(BuiltInParameter.FAMILY_LEVEL_PARAM)?.AsElementId();
                if (hostLevelId != null && hostLevelId != ElementId.InvalidElementId)
                {
                    return (doc.GetElement(hostLevelId) as Level)?.Name ?? "No Level";
                }
            }

            return "No Level";
        }

        private static string GetParameterString(Element element, BuiltInParameter parameter)
        {
            return element.get_Parameter(parameter)?.AsString() ?? "";
        }

        /// <summary>
        /// Reads mark when present, but never excludes the element when mark is empty.
        /// </summary>
        private static string GetElementMark(Element element)
        {
            var candidates = new[]
            {
                GetParameterString(element, BuiltInParameter.ALL_MODEL_MARK),
                GetParameterString(element, BuiltInParameter.DOOR_NUMBER),
                element.LookupParameter("Марка")?.AsString() ?? "",
                element.LookupParameter("Mark")?.AsString() ?? ""
            };

            return candidates.FirstOrDefault(mark => !string.IsNullOrWhiteSpace(mark)) ?? "";
        }

        private static double GetParameterDouble(Element element, BuiltInParameter parameter)
        {
            var param = element.get_Parameter(parameter);
            return param?.AsDouble() ?? 0;
        }

        private static long GetElementIdValue(ElementId id)
        {
#if REVIT2024_OR_GREATER
            return id.Value;
#else
            return id.IntegerValue;
#endif
        }

        private static string BuildGroupMark(IEnumerable<string> marks)
        {
            var distinct = marks
                .Where(m => !string.IsNullOrWhiteSpace(m))
                .Distinct()
                .OrderBy(m => m)
                .ToList();

            return distinct.Count == 0 ? "" : string.Join(", ", distinct);
        }

        private sealed class ScheduleInstanceRow
        {
            public long ElementId { get; set; }
            public string Mark { get; set; } = "";
            public string FamilyName { get; set; } = "";
            public string Type { get; set; } = "";
            public string Size { get; set; } = "";
            public double? AreaM2 { get; set; }
            public string Level { get; set; } = "";
            public long TypeId { get; set; }
            public List<FloorLayerExport> Layers { get; set; }
        }
    }
}
