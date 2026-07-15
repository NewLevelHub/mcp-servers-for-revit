namespace RevitMCPCommandSet.Utils
{
    /// <summary>
    /// Filters OST_Floors down to finish floors for экспликация полов (REV-49).
    /// Excludes structural slabs, ceiling insulation layers, and facade floor-like types
    /// that live in the same Revit category on org templates (e.g. Короткий блок).
    /// </summary>
    public static class FloorFinishClassifier
    {
        /// <summary>
        /// Type-name fragments that are not floor finishes in экспликация.
        /// Evidence: Короткий блок — (плита_перекрытия)*, (потолок_утеплитель)*, (фасад)*.
        /// </summary>
        private static readonly string[] NonFinishKeywords =
        {
            "плита_перекрытия",
            "потолок_утеплитель",
            "(фасад)",
            "фасад)",
            "foundation slab",
            "structural floor",
        };

        /// <summary>
        /// Explicit finish naming used in org templates.
        /// </summary>
        private static readonly string[] FinishKeywords =
        {
            "(полы)",
            "(пол)",
        };

        /// <summary>
        /// Returns true when the floor type should appear in floor finish / экспликация export.
        /// </summary>
        public static bool IsFloorFinish(string typeName, string familyName = "")
        {
            var text = CombineNames(familyName, typeName);
            if (string.IsNullOrWhiteSpace(text))
                return false;

            var normalized = text.ToLowerInvariant();
            if (ContainsAnyKeyword(normalized, NonFinishKeywords))
                return false;

            // Org templates: keep (полы)* explicitly; still allow generic Floor types
            // when naming convention is absent (empty/default projects).
            if (ContainsAnyKeyword(normalized, FinishKeywords))
                return true;

            // Generic / non-prefixed floor types that survived the exclude list.
            return true;
        }

        /// <summary>
        /// Strict org-template finishes: type name contains (полы) / (пол).
        /// Used to discover экспликация level groups (ignores bare «Floor» / slabs that slipped past excludes).
        /// </summary>
        public static bool IsExplicitFloorFinish(string typeName, string familyName = "")
        {
            var text = CombineNames(familyName, typeName);
            if (string.IsNullOrWhiteSpace(text))
                return false;

            var normalized = text.ToLowerInvariant();
            if (ContainsAnyKeyword(normalized, NonFinishKeywords))
                return false;

            return ContainsAnyKeyword(normalized, FinishKeywords);
        }

        /// <summary>
        /// Convenience for Floor instances.
        /// </summary>
        public static bool IsFloorFinish(Autodesk.Revit.DB.Floor floor)
        {
            if (floor == null)
                return false;

            var doc = floor.Document;
            var floorType = doc?.GetElement(floor.GetTypeId()) as Autodesk.Revit.DB.FloorType;
            return IsFloorFinish(floorType?.Name ?? floor.Name, floorType?.FamilyName ?? "");
        }

        /// <summary>
        /// Explicit (полы)* / (пол)* finish on a Floor instance.
        /// </summary>
        public static bool IsExplicitFloorFinish(Autodesk.Revit.DB.Floor floor)
        {
            if (floor == null)
                return false;

            var doc = floor.Document;
            var floorType = doc?.GetElement(floor.GetTypeId()) as Autodesk.Revit.DB.FloorType;
            return IsExplicitFloorFinish(floorType?.Name ?? floor.Name, floorType?.FamilyName ?? "");
        }

        /// <summary>
        /// True when type name looks like a non-finish floor (slab / insulation / facade).
        /// </summary>
        public static bool IsNonFinishFloor(string typeName, string familyName = "")
        {
            return !IsFloorFinish(typeName, familyName);
        }

        private static string CombineNames(string familyName, string typeName)
        {
            return $"{familyName ?? string.Empty} {typeName ?? string.Empty}".Trim();
        }

        private static bool ContainsAnyKeyword(string normalizedText, string[] keywords)
        {
            foreach (var keyword in keywords)
            {
                if (normalizedText.Contains(keyword))
                    return true;
            }

            return false;
        }
    }
}
