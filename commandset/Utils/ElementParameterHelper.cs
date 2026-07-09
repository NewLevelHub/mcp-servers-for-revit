using Autodesk.Revit.DB;
using RevitMCPCommandSet.Models.Common;

namespace RevitMCPCommandSet.Utils;

public static class ElementParameterHelper
{
    public static Parameter? FindParameter(Element element, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(parameterName))
            return null;

        var direct = element.LookupParameter(parameterName);
        if (direct != null)
            return direct;

        return element.Parameters
            .Cast<Parameter>()
            .FirstOrDefault(param =>
                string.Equals(param.Definition.Name, parameterName, StringComparison.OrdinalIgnoreCase));
    }

    public static ElementParameterInfo ToParameterInfo(Parameter parameter, Document doc)
    {
        var info = new ElementParameterInfo
        {
            Name = parameter.Definition.Name,
            StorageType = parameter.StorageType.ToString(),
            UnitType = GetUnitTypeLabel(parameter),
            IsReadOnly = parameter.IsReadOnly,
            HasValue = parameter.HasValue,
            IsShared = parameter.IsShared,
            BuiltInParameter = GetBuiltInParameterName(parameter),
            DisplayValue = parameter.HasValue ? parameter.AsValueString() ?? string.Empty : string.Empty,
            RawValue = GetRawValue(parameter, doc)
        };

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
