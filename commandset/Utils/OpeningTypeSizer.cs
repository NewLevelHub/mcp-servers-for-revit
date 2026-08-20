using Autodesk.Revit.DB;

namespace RevitMCPCommandSet.Utils
{
    /// <summary>
    /// Finding — or making — an opening type of an exact size.
    /// </summary>
    /// <remarks>
    /// Lifted out of <c>EnsureOpeningTypeEventHandler</c> (REV-153) when the задание на
    /// отверстия (REV-168) needed the same thing: every opening it cuts is a different
    /// size, and placing the nearest stock type instead is a silent dimensional error in
    /// the model. One table of width/height parameters, so the two cannot drift apart.
    /// </remarks>
    public static class OpeningTypeSizer
    {
        /// <summary>Families name their width differently; any writable one will do.</summary>
        public static readonly BuiltInParameter[] WidthParameters =
        {
            BuiltInParameter.DOOR_WIDTH,
            BuiltInParameter.WINDOW_WIDTH,
            BuiltInParameter.FAMILY_WIDTH_PARAM,
            BuiltInParameter.GENERIC_WIDTH
        };

        public static readonly BuiltInParameter[] HeightParameters =
        {
            BuiltInParameter.DOOR_HEIGHT,
            BuiltInParameter.WINDOW_HEIGHT,
            BuiltInParameter.FAMILY_HEIGHT_PARAM,
            BuiltInParameter.GENERIC_HEIGHT
        };

        public static bool TrySetLength(
            FamilySymbol symbol,
            double valueFt,
            BuiltInParameter[] candidates,
            out string error)
        {
            foreach (var bip in candidates)
            {
                var param = symbol?.get_Parameter(bip);
                if (param == null || param.IsReadOnly || param.StorageType != StorageType.Double)
                    continue;

                if (param.Set(valueFt))
                {
                    error = null;
                    return true;
                }
            }

            error = "no writable width/height parameter on this family";
            return false;
        }

        public static double ReadLength(FamilySymbol symbol, BuiltInParameter[] candidates)
        {
            foreach (var bip in candidates)
            {
                var param = symbol?.get_Parameter(bip);
                if (param != null && param.StorageType == StorageType.Double)
                    return param.AsDouble();
            }

            return 0;
        }

        /// <summary>A type of the family already at this size, or null.</summary>
        public static FamilySymbol FindMatchingSymbol(
            Document doc,
            FamilySymbol source,
            double widthFt,
            double heightFt,
            double tolFt)
        {
            var family = source?.Family;
            if (doc == null || family == null)
                return null;

            foreach (var id in family.GetFamilySymbolIds())
            {
                if (!(doc.GetElement(id) is FamilySymbol symbol))
                    continue;

                var w = ReadLength(symbol, WidthParameters);
                if (w <= 0 || Math.Abs(w - widthFt) > tolFt)
                    continue;

                if (heightFt > 0)
                {
                    var h = ReadLength(symbol, HeightParameters);
                    if (h > 0 && Math.Abs(h - heightFt) > tolFt)
                        continue;
                }

                return symbol;
            }

            return null;
        }

        /// <summary>
        /// The type to place: an existing one of this size, or a fresh duplicate sized to
        /// order. Must be called inside a transaction — it may create a type.
        /// </summary>
        /// <remarks>
        /// A задание на отверстия makes dozens of these, so the lookup comes first: two
        /// pipes of the same diameter through two walls must share one type, not leave
        /// the project with two identical ones.
        /// </remarks>
        public static FamilySymbol EnsureSizedSymbol(
            Document doc,
            FamilySymbol source,
            double widthMm,
            double heightMm,
            double toleranceMm,
            string typeName,
            out string error)
        {
            error = null;

            if (doc == null || source == null)
            {
                error = "no source family type to size from";
                return null;
            }

            var widthFt = RevitUnitConversion.FromMillimeters(widthMm);
            var heightFt = RevitUnitConversion.FromMillimeters(heightMm);
            var tolFt = RevitUnitConversion.FromMillimeters(Math.Max(0.1, toleranceMm));

            var existing = FindMatchingSymbol(doc, source, widthFt, heightFt, tolFt);
            if (existing != null)
            {
                Activate(existing);
                return existing;
            }

            var name = UniqueTypeName(source, typeName);
            if (!(source.Duplicate(name) is FamilySymbol created))
            {
                error = $"Duplicate('{name}') did not return a FamilySymbol";
                return null;
            }

            if (!TrySetLength(created, widthFt, WidthParameters, out error))
                return null;

            if (heightFt > 0 && !TrySetLength(created, heightFt, HeightParameters, out error))
                return null;

            Activate(created);
            return created;
        }

        /// <summary>An inactive symbol cannot be placed, and says so only at NewFamilyInstance.</summary>
        public static void Activate(FamilySymbol symbol)
        {
            if (symbol != null && !symbol.IsActive)
                symbol.Activate();
        }

        /// <summary>Duplicate() throws on a name clash, so step the name until it is free.</summary>
        private static string UniqueTypeName(FamilySymbol source, string desired)
        {
            var family = source.Family;
            var taken = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);

            if (family != null)
            {
                var doc = source.Document;
                foreach (var id in family.GetFamilySymbolIds())
                {
                    if (doc.GetElement(id) is FamilySymbol symbol && !string.IsNullOrEmpty(symbol.Name))
                        taken.Add(symbol.Name);
                }
            }

            var baseName = string.IsNullOrWhiteSpace(desired) ? "Проём" : desired.Trim();
            if (!taken.Contains(baseName))
                return baseName;

            for (var suffix = 2; suffix < 1000; suffix++)
            {
                var candidate = $"{baseName} ({suffix})";
                if (!taken.Contains(candidate))
                    return candidate;
            }

            return $"{baseName} {Guid.NewGuid():N}";
        }
    }
}
