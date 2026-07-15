using RevitMCPCommandSet.Models.Views;

namespace RevitMCPCommandSet.Utils
{
    /// <summary>
    /// Seeded recipe for floor экспликация ViewSchedule (REV-49 B).
    /// Columns match «Короткий блок» RD; Area uses Totals so IsItemized=false shows m² sums
    /// instead of «&lt;варианты&gt;». Optional level include set filters schedules by discovered groups.
    /// </summary>
    public static class FloorExplicationScheduleRecipe
    {
        public const string DefaultScheduleName = "О_АР_Экспликация полов";
        public const string RecipeId = "korotkiy-blok-rd-portable-v1";

        public static ScheduleCreationInfo Build(
            string scheduleName = null,
            IReadOnlyList<long> includeLevelIds = null,
            IReadOnlyList<long> allLevelIds = null)
        {
            var info = new ScheduleCreationInfo
            {
                Name = string.IsNullOrWhiteSpace(scheduleName) ? DefaultScheduleName : scheduleName.Trim(),
                CategoryName = "Floors",
                Type = "Regular",
                TemplateId = string.Empty,
                ShowTitle = true,
                ShowHeaders = true,
                ShowGridLines = true,
                IsItemized = false,
                ClearExistingFilters = true,
                ClearExistingSorts = true,
                ClearExistingGroups = true,
                Fields = new List<ScheduleFieldInfo>
                {
                    new ScheduleFieldInfo
                    {
                        ParameterName =
                            "Комментарии к типоразмеру|Type Comments|Помещение_Список номеров",
                        FieldType = "Type",
                        Heading = "Наименование помещения",
                        Width = 60,
                        HorizontalAlignment = "Left"
                    },
                    new ScheduleFieldInfo
                    {
                        ParameterName =
                            "Маркировка типоразмера|Type Mark|Тип отделки пола",
                        FieldType = "Type",
                        Heading = "Тип пола №",
                        Width = 25,
                        HorizontalAlignment = "Center"
                    },
                    new ScheduleFieldInfo
                    {
                        ParameterName =
                            "Изображение типоразмера|Type Image|Схема пола (нумерация элементов пола)",
                        FieldType = "Type",
                        Heading = "Схема пола",
                        Width = 40,
                        HorizontalAlignment = "Center"
                    },
                    new ScheduleFieldInfo
                    {
                        ParameterName =
                            "Описание|Description|Данные элементов пола",
                        FieldType = "Type",
                        Heading = "Состав пола и их толщина, мм",
                        Width = 80,
                        HorizontalAlignment = "Left"
                    },
                    new ScheduleFieldInfo
                    {
                        ParameterName = "Площадь|Area",
                        FieldType = "Instance",
                        Heading = "Площадь пола",
                        Width = 25,
                        HorizontalAlignment = "Center",
                        HasTotals = true
                    },
                    new ScheduleFieldInfo
                    {
                        ParameterId = -1010109, // ALL_MODEL_MODEL
                        ParameterName = "Группа модели|Model",
                        FieldType = "Type",
                        Heading = "Группа модели",
                        Width = 25,
                        IsHidden = true,
                        HorizontalAlignment = "Left"
                    },
                    new ScheduleFieldInfo
                    {
                        // Floor instance level — for per-RD-sheet filters.
                        ParameterName = "Уровень|Level",
                        FieldType = "Instance",
                        Heading = "Уровень",
                        Width = 25,
                        IsHidden = true,
                        HorizontalAlignment = "Left"
                    }
                },
                Filters = new List<ScheduleFilterInfo>
                {
                    new ScheduleFilterInfo
                    {
                        FieldName = "Группа модели|Model",
                        FilterType = "Contains",
                        FilterValue = "Пол"
                    }
                },
                SortFields = new List<ScheduleSortInfo>
                {
                    new ScheduleSortInfo
                    {
                        FieldName = "Маркировка типоразмера|Type Mark|Тип отделки пола",
                        SortOrder = "Ascending"
                    }
                }
            };

            ApplyLevelFilters(info, includeLevelIds, allLevelIds);
            return info;
        }

        /// <summary>
        /// Single level → Equal; several levels → NotEqual every other project level (Revit filters are AND-only).
        /// </summary>
        private static void ApplyLevelFilters(
            ScheduleCreationInfo info,
            IReadOnlyList<long> includeLevelIds,
            IReadOnlyList<long> allLevelIds)
        {
            if (includeLevelIds == null || includeLevelIds.Count == 0)
                return;

            if (includeLevelIds.Count == 1)
            {
                info.Filters.Add(new ScheduleFilterInfo
                {
                    FieldName = "Уровень|Level",
                    FilterType = "Equal",
                    FilterElementId = includeLevelIds[0]
                });
                return;
            }

            if (allLevelIds == null || allLevelIds.Count == 0)
                return;

            var include = new HashSet<long>(includeLevelIds);
            foreach (var levelId in allLevelIds)
            {
                if (include.Contains(levelId))
                    continue;

                info.Filters.Add(new ScheduleFilterInfo
                {
                    FieldName = "Уровень|Level",
                    FilterType = "NotEqual",
                    FilterElementId = levelId
                });
            }
        }
    }

    /// <summary>
    /// One экспликация block: title + levels (built by FloorExplicationLevelDiscoverer).
    /// </summary>
    public sealed class FloorExplicationLevelGroup
    {
        public string Key { get; set; }
        public string Title { get; set; }
        public string ScheduleNameSuffix { get; set; }
    }
}
