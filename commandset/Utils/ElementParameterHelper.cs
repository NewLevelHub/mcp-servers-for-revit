using Autodesk.Revit.DB;
using RevitMCPCommandSet.Models.Common;

namespace RevitMCPCommandSet.Utils;

public static class ElementParameterHelper
{
    /// <summary>
    ///     Russian ↔ English names for the parameters an architect actually types.
    ///     A Russian-language Revit shows "Марка", the API often stores "Mark", and a
    ///     lookup miss used to return a bare "not found" — the model then burned calls
    ///     guessing spellings (3 misses on "Марка" before it tried "Mark"). Groups are
    ///     bidirectional: any name in a row resolves to every other name in that row.
    /// </summary>
    private static readonly string[][] ParameterAliasGroups =
    {
        new[] { "Mark", "Марка" },
        new[] { "Type Mark", "Марка типа" },
        new[] { "Comments", "Комментарии", "Примечание" },
        new[] { "Type Comments", "Комментарии к типу" },
        new[] { "Description", "Описание" },
        new[] { "Manufacturer", "Изготовитель", "Производитель" },
        new[] { "Model", "Модель" },
        new[] { "Length", "Длина" },
        new[] { "Width", "Ширина" },
        new[] { "Height", "Высота" },
        new[] { "Thickness", "Толщина" },
        new[] { "Area", "Площадь" },
        new[] { "Volume", "Объем", "Объём" },
        new[] { "Perimeter", "Периметр" },
        new[] { "Level", "Уровень" },
        new[] { "Name", "Имя", "Наименование" },
        new[] { "Number", "Номер" },
        new[] { "Family", "Семейство" },
        new[] { "Family and Type", "Семейство и типоразмер" },
        new[] { "Type", "Тип", "Типоразмер" },
        new[] { "Type Name", "Имя типа" },
        new[] { "Material", "Материал" },
        new[] { "Phase Created", "Стадия возведения" },
        new[] { "Phase Demolished", "Стадия сноса" },
        new[] { "Base Constraint", "Зависимость снизу" },
        new[] { "Top Constraint", "Зависимость сверху" },
        new[] { "Base Offset", "Смещение снизу" },
        new[] { "Top Offset", "Смещение сверху" },
        new[] { "Unconnected Height", "Высота неприсоединенная", "Высота неприсоединённая" },
        new[] { "Sill Height", "Высота нижнего бруса", "Высота подоконника" },
        new[] { "Head Height", "Высота верхнего бруса" },
        new[] { "Room Bounding", "Граница помещения", "WALL_ATTR_ROOM_BOUNDING" },
        new[] { "Department", "Отдел", "Подразделение" },
        new[] { "Occupancy", "Назначение" },
        new[] { "Fire Rating", "Предел огнестойкости", "Огнестойкость" },
        new[] { "Cost", "Стоимость" },
        new[] { "Keynote", "Ключевая пометка" },
        new[] { "Assembly Code", "Код сборки" },
        new[] { "Image", "Изображение" },
        new[] { "Workset", "Рабочий набор" },
        new[] { "Elevation", "Отметка" },
    };

    private static readonly Dictionary<string, string[]> AliasLookup = BuildAliasLookup();

    private static Dictionary<string, string[]> BuildAliasLookup()
    {
        var map = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in ParameterAliasGroups)
        {
            foreach (var name in group)
                map[name] = group;
        }
        return map;
    }

    public static Parameter? FindParameter(Element element, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(parameterName))
            return null;

        foreach (var candidate in ExpandParameterAliases(parameterName))
        {
            var direct = element.LookupParameter(candidate);
            if (direct != null)
                return direct;

            var byName = element.Parameters
                .Cast<Parameter>()
                .FirstOrDefault(param =>
                    string.Equals(param.Definition.Name, candidate, StringComparison.OrdinalIgnoreCase));
            if (byName != null)
                return byName;
        }

        // Built-in: Room Bounding / Граница помещения on walls
        if (IsRoomBoundingAlias(parameterName) && element is Wall)
        {
            var bip = element.get_Parameter(BuiltInParameter.WALL_ATTR_ROOM_BOUNDING);
            if (bip != null)
                return bip;
        }

        return null;
    }

    /// <summary>
    ///     Why the lookup missed, plus the closest names this element actually has.
    ///     "Parameter not found" alone gave the model nothing to correct.
    /// </summary>
    public static string DescribeMissingParameter(Element element, string parameterName)
    {
        var available = element.Parameters
            .Cast<Parameter>()
            .Select(p => p.Definition?.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var suggestions = RankSuggestions(parameterName, available!).Take(5).ToList();
        var message = $"У элемента (id {element.Id.GetIntValue()}) нет параметра «{parameterName}»";

        if (suggestions.Count > 0)
            message += ". Похожие имена у этого элемента: " + string.Join(", ", suggestions);
        else if (available.Count > 0)
            message += $". Всего доступно параметров: {available.Count}";

        return message;
    }

    /// <summary>Available names ordered by closeness to <paramref name="wanted"/>.</summary>
    private static IEnumerable<string> RankSuggestions(string wanted, List<string> available)
    {
        var needle = wanted.Trim();
        var aliases = AliasLookup.TryGetValue(needle, out var group)
            ? new HashSet<string>(group, StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        return available
            .Select(name => new
            {
                Name = name,
                Score = aliases.Contains(name) ? 0
                    : name.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0 ? 1
                    : needle.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0 ? 2
                    : SharedPrefixLength(name, needle) >= 3 ? 3
                    : int.MaxValue,
            })
            .Where(x => x.Score != int.MaxValue)
            .OrderBy(x => x.Score)
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.Name);
    }

    private static int SharedPrefixLength(string a, string b)
    {
        var max = Math.Min(a.Length, b.Length);
        var i = 0;
        while (i < max && char.ToLowerInvariant(a[i]) == char.ToLowerInvariant(b[i]))
            i++;
        return i;
    }

    private static IEnumerable<string> ExpandParameterAliases(string parameterName)
    {
        yield return parameterName;

        var trimmed = parameterName.Trim();
        if (AliasLookup.TryGetValue(trimmed, out var group))
        {
            foreach (var alias in group)
            {
                if (!string.Equals(alias, trimmed, StringComparison.OrdinalIgnoreCase))
                    yield return alias;
            }
        }

        if (IsRoomBoundingAlias(parameterName))
        {
            yield return "Room Bounding";
            yield return "Граница помещения";
        }
    }

    private static bool IsRoomBoundingAlias(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;
        var n = name.Trim();
        return n.Equals("Room Bounding", StringComparison.OrdinalIgnoreCase)
            || n.Equals("Граница помещения", StringComparison.OrdinalIgnoreCase)
            || n.Equals("WALL_ATTR_ROOM_BOUNDING", StringComparison.OrdinalIgnoreCase)
            || (n.IndexOf("границ", StringComparison.OrdinalIgnoreCase) >= 0
                && n.IndexOf("помещен", StringComparison.OrdinalIgnoreCase) >= 0);
    }

    public static ElementParameterInfo ToParameterInfo(Parameter parameter, Document doc, bool slim = false)
    {
        var info = new ElementParameterInfo
        {
            Name = parameter.Definition.Name,
            StorageType = parameter.StorageType.ToString(),
            IsReadOnly = parameter.IsReadOnly,
            HasValue = parameter.HasValue,
            DisplayValue = parameter.HasValue ? parameter.AsValueString() ?? string.Empty : string.Empty,
        };

        if (slim)
            return info;

        info.UnitType = GetUnitTypeLabel(parameter);
        info.IsShared = parameter.IsShared;
        info.BuiltInParameter = GetBuiltInParameterName(parameter);
        info.RawValue = GetRawValue(parameter, doc);
        return info;
    }

    public static void SetParameterValue(Parameter parameter, object? value, Document doc)
    {
        if (parameter.IsReadOnly)
            throw new InvalidOperationException(
                $"Parameter '{parameter.Definition.Name}' is read-only and cannot be modified.");

        if (value == null)
            throw new ArgumentException(
                $"Value for parameter '{parameter.Definition.Name}' cannot be null.");

        switch (parameter.StorageType)
        {
            case StorageType.String:
                if (value is not string stringValue)
                    throw new ArgumentException(
                        $"Parameter '{parameter.Definition.Name}' expects a string value.");
                parameter.Set(stringValue);
                return;

            case StorageType.Integer:
                if (!TryConvertToInteger(value, out var intValue))
                    throw new ArgumentException(
                        $"Parameter '{parameter.Definition.Name}' expects an integer or yes/no value.");
                parameter.Set(intValue);
                return;

            case StorageType.Double:
                if (!TryConvertToDouble(value, out var doubleValue))
                    throw new ArgumentException(
                        $"Parameter '{parameter.Definition.Name}' expects a numeric value.");
                parameter.Set(doubleValue);
                return;

            case StorageType.ElementId:
                if (!TryConvertToElementId(value, out var elementId))
                    throw new ArgumentException(
                        $"Parameter '{parameter.Definition.Name}' expects an element id value.");
                parameter.Set(elementId);
                return;

            default:
                throw new ArgumentException(
                    $"Parameter '{parameter.Definition.Name}' has unsupported storage type '{parameter.StorageType}'.");
        }
    }

    private static string GetUnitTypeLabel(Parameter parameter)
    {
        if (parameter.StorageType != StorageType.Double)
            return string.Empty;

        try
        {
#if REVIT2022_OR_GREATER
            var unitTypeId = parameter.GetUnitTypeId();
            if (unitTypeId != null && unitTypeId != UnitTypeId.General)
                return LabelUtils.GetLabelForUnit(unitTypeId);
#endif
        }
        catch
        {
            // Fall back to empty label when unit metadata is unavailable.
        }

        return string.Empty;
    }

    private static string? GetBuiltInParameterName(Parameter parameter)
    {
        if (parameter.Definition is not InternalDefinition internalDefinition)
            return null;

        var builtIn = internalDefinition.BuiltInParameter;
        return builtIn == BuiltInParameter.INVALID ? null : builtIn.ToString();
    }

    private static object? GetRawValue(Parameter parameter, Document doc)
    {
        if (!parameter.HasValue)
            return null;

        switch (parameter.StorageType)
        {
            case StorageType.String:
                return parameter.AsString();

            case StorageType.Integer:
                if (IsYesNoParameter(parameter))
                    return parameter.AsInteger() == 1;
                return parameter.AsInteger();

            case StorageType.Double:
                return parameter.AsDouble();

            case StorageType.ElementId:
                var elementId = parameter.AsElementId();
                if (elementId == null || elementId == ElementId.InvalidElementId)
                    return null;

                var referencedElement = doc.GetElement(elementId);
                return new Dictionary<string, object?>
                {
                    ["id"] = elementId.GetValue(),
                    ["name"] = referencedElement?.Name
                };

            default:
                return parameter.AsValueString();
        }
    }

    private static bool IsYesNoParameter(Parameter parameter)
    {
        if (parameter.StorageType != StorageType.Integer)
            return false;

        if (parameter.Definition is not InternalDefinition internalDefinition)
            return false;

#if REVIT2023_OR_GREATER
        try
        {
            var dataType = internalDefinition.GetDataType();
            if (dataType != null && dataType.Equals(SpecTypeId.Boolean.YesNo))
                return true;
        }
        catch
        {
            // Ignore and fall back to built-in parameter checks.
        }
#endif

        return internalDefinition.BuiltInParameter is BuiltInParameter.IS_VISIBLE_PARAM
            or BuiltInParameter.WALL_ATTR_ROOM_BOUNDING
            or BuiltInParameter.LEVEL_IS_BUILDING_STORY;
    }

    private static bool TryConvertToInteger(object value, out int intValue)
    {
        switch (value)
        {
            case int i:
                intValue = i;
                return true;
            case long l when l >= int.MinValue && l <= int.MaxValue:
                intValue = (int)l;
                return true;
            case double d when Math.Abs(d % 1) < double.Epsilon:
                intValue = (int)d;
                return true;
            case bool b:
                intValue = b ? 1 : 0;
                return true;
            case string s when bool.TryParse(s, out var boolResult):
                intValue = boolResult ? 1 : 0;
                return true;
            case string s when int.TryParse(s, out var parsedInt):
                intValue = parsedInt;
                return true;
            case string s when string.Equals(s, "yes", StringComparison.OrdinalIgnoreCase):
                intValue = 1;
                return true;
            case string s when string.Equals(s, "no", StringComparison.OrdinalIgnoreCase):
                intValue = 0;
                return true;
            default:
                intValue = 0;
                return false;
        }
    }

    private static bool TryConvertToDouble(object value, out double doubleValue)
    {
        switch (value)
        {
            case double d:
                doubleValue = d;
                return true;
            case float f:
                doubleValue = f;
                return true;
            case int i:
                doubleValue = i;
                return true;
            case long l:
                doubleValue = l;
                return true;
            case string s when double.TryParse(s, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var parsedDouble):
                doubleValue = parsedDouble;
                return true;
            default:
                doubleValue = 0;
                return false;
        }
    }

    private static bool TryConvertToElementId(object value, out ElementId elementId)
    {
        elementId = ElementId.InvalidElementId;

        switch (value)
        {
            case int i:
                elementId = RevitMCPCommandSet.Utils.ElementIdExtensions.FromLong(i);
                return true;
            case long l:
                elementId = RevitMCPCommandSet.Utils.ElementIdExtensions.FromLong(l);
                return true;
            case string s when long.TryParse(s, out var parsedId):
                elementId = RevitMCPCommandSet.Utils.ElementIdExtensions.FromLong(parsedId);
                return true;
            default:
                return false;
        }
    }
}
