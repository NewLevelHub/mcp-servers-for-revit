using Autodesk.Revit.DB;

namespace RevitMCPCommandSet.Utils
{
    /// <summary>
    /// Filters OST_Doors / OST_Windows instances down to scheduleable opening fills
    /// (door/window blocks), excluding slopes, trims, and similar accessories (REV-41 / REV-48).
    /// Matching is by family/type name heuristics used in customer templates.
    /// </summary>
    public static class OpeningFillClassifier
    {
        /// <summary>
        /// Name fragments that mark door accessories in OST_Doors (not door blocks).
        /// Evidence: Короткий блок families like "(откос)двери_внутренний".
        /// </summary>
        private static readonly string[] DoorAccessoryKeywords =
        {
            "откос",
            "обвязк",
            "наличник",
            "добор",
            "reveal",
            "door trim",
            "jamb trim",
        };

        /// <summary>
        /// Name fragments that mark window accessories in OST_Windows (not window blocks).
        /// </summary>
        private static readonly string[] WindowAccessoryKeywords =
        {
            "откос",
            "подоконник",
            "слив",
            "reveal",
            "window sill",
            "drip",
        };

        /// <summary>
        /// Returns true when the element should be counted as a door block in schedules.
        /// </summary>
        public static bool IsSchedulableDoor(FamilyInstance instance)
        {
            if (instance?.Symbol == null)
                return false;

            return !IsDoorAccessory(instance.Symbol.FamilyName, instance.Symbol.Name);
        }

        /// <summary>
        /// Returns true when the element should be counted as a door block in schedules.
        /// </summary>
        public static bool IsSchedulableDoor(Element element)
        {
            if (element is FamilyInstance instance)
                return IsSchedulableDoor(instance);

            GetFamilyAndTypeNames(element, out var familyName, out var typeName);
            return !IsDoorAccessory(familyName, typeName);
        }

        /// <summary>
        /// Returns true when the element should be counted as a window block in schedules.
        /// </summary>
        public static bool IsSchedulableWindow(FamilyInstance instance)
        {
            if (instance?.Symbol == null)
                return false;

            return !IsWindowAccessory(instance.Symbol.FamilyName, instance.Symbol.Name);
        }

        /// <summary>
        /// Returns true when the element should be counted as a window block in schedules.
        /// </summary>
        public static bool IsSchedulableWindow(Element element)
        {
            if (element is FamilyInstance instance)
                return IsSchedulableWindow(instance);

            GetFamilyAndTypeNames(element, out var familyName, out var typeName);
            return !IsWindowAccessory(familyName, typeName);
        }

        /// <summary>
        /// True when family/type name indicates a door accessory (slope, trim, etc.).
        /// </summary>
        public static bool IsDoorAccessory(string familyName, string typeName = "")
        {
            return ContainsAnyKeyword(CombineNames(familyName, typeName), DoorAccessoryKeywords);
        }

        /// <summary>
        /// True when family/type name indicates a window accessory (slope, sill, drip, etc.).
        /// </summary>
        public static bool IsWindowAccessory(string familyName, string typeName = "")
        {
            return ContainsAnyKeyword(CombineNames(familyName, typeName), WindowAccessoryKeywords);
        }

        private static string CombineNames(string familyName, string typeName)
        {
            return $"{familyName ?? string.Empty} {typeName ?? string.Empty}".Trim();
        }

        private static bool ContainsAnyKeyword(string text, string[] keywords)
        {
            if (string.IsNullOrWhiteSpace(text))
                return false;

            var normalized = text.ToLowerInvariant();
            foreach (var keyword in keywords)
            {
                if (normalized.Contains(keyword))
                    return true;
            }

            return false;
        }

        private static void GetFamilyAndTypeNames(Element element, out string familyName, out string typeName)
        {
            familyName = element?.get_Parameter(BuiltInParameter.ELEM_FAMILY_PARAM)?.AsValueString()
                ?? string.Empty;
            typeName = element?.get_Parameter(BuiltInParameter.ELEM_TYPE_PARAM)?.AsValueString()
                ?? element?.Name
                ?? string.Empty;
        }
    }
}
